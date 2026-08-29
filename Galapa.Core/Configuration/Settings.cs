using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Galapa.Core.Configuration;

public partial class Settings : ObservableValidator
{
    private static readonly SemaphoreSlim SaveGate = new(1, 1);

    [ObservableProperty] [Required] [CustomValidation(typeof(Settings), "ValidateGameFolderPath")]
    private string? _gameFolderPath;

    [ObservableProperty] [Required] private string? _saveFolderPath;

    [ObservableProperty] [Required] private bool? _errorReporting;

    private static Settings GetDefaults()
    {
        return new Settings
        {
            GameFolderPath = InstallInfo.Location,
            SaveFolderPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                "My Games", "Dragon Quest X"),
            ErrorReporting = false
        };
    }

    public static Settings Load()
    {
        if (File.Exists(Paths.Settings))
        {
            try
            {
                var json = File.ReadAllText(Paths.Settings);
                return JsonSerializer.Deserialize<Settings>(json) ?? GetDefaults();
            }
            catch (JsonException)
            {
                return GetDefaults();
            }
        }

        return GetDefaults();
    }

    public void Save() => SaveCoreAsync(CancellationToken.None).GetAwaiter().GetResult();

    public Task SaveAsync(CancellationToken cancellationToken = default) => SaveCoreAsync(cancellationToken);

    private async Task SaveCoreAsync(CancellationToken cancellationToken)
    {
        await SaveGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? temporaryPath = null;

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Capture one complete snapshot while this process owns the settings writer.
            var json = JsonSerializer.Serialize(this);
            var destinationPath = Paths.Settings;
            var destinationDirectory = Path.GetDirectoryName(destinationPath)
                                       ?? throw new InvalidOperationException("The settings path has no directory.");

            Directory.CreateDirectory(destinationDirectory);
            temporaryPath = Path.Combine(
                destinationDirectory,
                $"{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");

            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream))
            {
                await writer.WriteAsync(json.AsMemory(), cancellationToken).ConfigureAwait(false);
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destinationPath, true);
            temporaryPath = null;
        }
        catch (Exception saveException) when (temporaryPath is not null)
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
            {
                throw new AggregateException(
                    "The settings save failed and its temporary file could not be removed.",
                    saveException,
                    cleanupException);
            }

            throw;
        }
        finally
        {
            SaveGate.Release();
        }
    }

    public static ValidationResult ValidateGameFolderPath(string gameFolderPath, ValidationContext context)
    {
        if (!Directory.Exists(gameFolderPath))
            return new ValidationResult("Folder does not exist");
        if (!File.Exists(Path.Combine(gameFolderPath, "Game\\DQXGame.exe")))
            return new ValidationResult("DQXGame.exe does not exist");

        return ValidationResult.Success!;
    }
}
