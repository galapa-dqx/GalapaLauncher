using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Galapa.Core.Configuration;

public partial class Settings : ObservableValidator
{
    private static readonly SemaphoreSlim SaveGate = new(1, 1);
    public const string DefaultThemeId = "estella";
    public static readonly string GameExecutableRelativePath = Path.Combine("Game", "DQXGame.exe");
    public static string GameExecutableDisplayPath =>
        GameExecutableRelativePath.Replace(Path.DirectorySeparatorChar, '\\').Replace(Path.AltDirectorySeparatorChar, '\\');

    [ObservableProperty]
    [Required]
    [CustomValidation(typeof(Settings), "ValidateGameFolderPath")]
    private string? _gameFolderPath;

    [ObservableProperty][Required] private string? _saveFolderPath;

    [ObservableProperty][Required] private bool? _errorReporting;

    [ObservableProperty][Required] private string _themeId = DefaultThemeId;

    private static Settings GetDefaults()
    {
        return new Settings
        {
            GameFolderPath = InstallInfo.Location,
            SaveFolderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "My Games", "Dragon Quest X"),
            ErrorReporting = false,
            ThemeId = DefaultThemeId
        };
    }

    public static Settings Load()
    {
        if (File.Exists(Paths.Settings))
        {
            try
            {
                var json = File.ReadAllText(Paths.Settings);
                var loaded = JsonSerializer.Deserialize<Settings>(json) ?? GetDefaults();
                if (string.IsNullOrWhiteSpace(loaded.ThemeId)) loaded.ThemeId = DefaultThemeId;
                return loaded;
            }
            catch (JsonException)
            {
                return GetDefaults();
            }
        }

        return GetDefaults();
    }

    public void Save() => SaveCoreAsync(CancellationToken.None).GetAwaiter().GetResult();

    public async Task SaveAsync(CancellationToken cancellationToken = default)
        => await SaveCoreAsync(cancellationToken);

    private async Task SaveCoreAsync(CancellationToken cancellationToken)
    {
        await SaveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var json = JsonSerializer.Serialize(this);
            Directory.CreateDirectory(Paths.AppData);
            temporaryPath = Path.Combine(Paths.AppData, $"settings.{Guid.NewGuid():N}.tmp");
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, 16 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream))
            {
                await writer.WriteAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, Paths.Settings, true);
            temporaryPath = null;
        }
        finally
        {
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            SaveGate.Release();
        }
    }

    public static bool IsValidGameFolder(string? gameFolderPath) =>
        !string.IsNullOrWhiteSpace(gameFolderPath) &&
        Directory.Exists(gameFolderPath) &&
        File.Exists(Path.Combine(gameFolderPath, GameExecutableRelativePath));

    public static ValidationResult ValidateGameFolderPath(string? gameFolderPath, ValidationContext context)
    {
        if (string.IsNullOrWhiteSpace(gameFolderPath) || !Directory.Exists(gameFolderPath))
            return new ValidationResult("Folder does not exist");
        if (!File.Exists(Path.Combine(gameFolderPath, GameExecutableRelativePath)))
            return new ValidationResult($"{GameExecutableDisplayPath} does not exist");

        return ValidationResult.Success!;
    }
}
