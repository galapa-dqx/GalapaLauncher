using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using CommunityToolkit.Mvvm.ComponentModel;
using Galapa.Core.IO;

namespace Galapa.Core.Configuration;

/// <summary>
///     Thrown instead of writing when <see cref="Settings" /> holds invalid values.
/// </summary>
public class InvalidSettingsException(IReadOnlyList<ValidationResult> errors)
    : Exception("Settings were not saved because they are invalid: " +
                string.Join("; ", errors.Select(error => error.ErrorMessage)))
{
    public IReadOnlyList<ValidationResult> Errors { get; } = errors;
}

/// <summary>
///     The launcher's own settings. Every property validates when it is set, so <see cref="ObservableValidator.HasErrors" />
///     reflects the current values, and the writer refuses to persist an instance that has errors.
/// </summary>
[NotifyDataErrorInfo]
public partial class Settings : ObservableValidator
{
    private static readonly SemaphoreSlim SaveGate = new(1, 1);

    // ObservableValidator.HasErrors is public state, not a setting; keep it out of the file.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                info =>
                {
                    if (info.Type != typeof(Settings)) return;
                    foreach (var property in info.Properties.Where(p => p.Name == nameof(HasErrors)).ToList())
                        info.Properties.Remove(property);
                }
            }
        }
    };

    /// <summary>
    ///     The <see cref="Configuration.InstallRoot" /> location, stored under its original name for compatibility.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallRoot))]
    [Required(ErrorMessage = "Choose the folder Dragon Quest X is installed in")]
    [CustomValidation(typeof(Settings), nameof(ValidateGameFolderPath))]
    private string? _gameFolderPath;

    [ObservableProperty] [Required] private bool? _errorReporting;

    /// <summary>
    ///     The install root derived from <see cref="GameFolderPath" />, or null when none is set. This does not imply the
    ///     folder is valid; check <see cref="ObservableValidator.HasErrors" /> or <see cref="Configuration.InstallRoot.Validate" />.
    /// </summary>
    [JsonIgnore]
    public InstallRoot? InstallRoot => this.GameFolderPath is null ? null : new InstallRoot(this.GameFolderPath);

    private static Settings GetDefaults()
    {
        return new Settings
        {
            GameFolderPath = InstallInfo.Location,
            ErrorReporting = false
        };
    }

    /// <summary>
    ///     Loads the settings file, falling back to defaults per field. Loading is tolerant: a stale or missing install
    ///     folder loads with errors flagged rather than throwing.
    /// </summary>
    public static Settings Load()
    {
        var settings = LoadFile();
        settings.ValidateAllProperties();
        return settings;
    }

    private static Settings LoadFile()
    {
        if (!File.Exists(Paths.Settings)) return GetDefaults();

        try
        {
            var json = File.ReadAllText(Paths.Settings);
            var settings = JsonSerializer.Deserialize<Settings>(json, JsonOptions);
            if (settings is null) return GetDefaults();

            // A file missing a key (or holding null) deserializes to null; fall back to the default per field.
            var defaults = GetDefaults();
            settings.GameFolderPath ??= defaults.GameFolderPath;
            settings.ErrorReporting ??= defaults.ErrorReporting;
            return settings;
        }
        catch (JsonException)
        {
            return GetDefaults();
        }
    }

    /// <summary>
    ///     Synchronously writes the current settings to disk. See <see cref="SaveAsync" />.
    /// </summary>
    /// <exception cref="InvalidSettingsException">The settings hold invalid values; nothing was written.</exception>
    public void Save() => this.SaveCoreAsync(CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    ///     Writes a snapshot of the current settings to disk, taken when this method is called.
    ///     The file is written to a temporary path and atomically moved over the existing file, and
    ///     saves from this process are serialized, so readers only ever see a complete document.
    /// </summary>
    /// <exception cref="InvalidSettingsException">The settings hold invalid values; nothing was written.</exception>
    public Task SaveAsync(CancellationToken cancellationToken = default) => this.SaveCoreAsync(cancellationToken);

    private async Task SaveCoreAsync(CancellationToken cancellationToken)
    {
        // Validate and snapshot on the caller's thread, before any await, so a save queued behind another never
        // serializes on a pool thread while the UI thread is still mutating properties. Revalidating everything also
        // catches properties that were never assigned, and folders that have changed on disk since they were set.
        this.ValidateAllProperties();
        if (this.HasErrors) throw new InvalidSettingsException(this.GetErrors().ToList());

        var json = JsonSerializer.Serialize(this, JsonOptions);

        await SaveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await AtomicFile.WriteAllTextAsync(Paths.Settings, json, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            SaveGate.Release();
        }
    }

    public static ValidationResult ValidateGameFolderPath(string? gameFolderPath, ValidationContext context) =>
        Configuration.InstallRoot.Validate(gameFolderPath);
}
