using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Galapa.Core.Configuration;

public partial class Settings : ObservableValidator
{
    public const string DefaultThemeId = "estella";
    public static readonly string GameExecutableRelativePath = Path.Combine("Game", "DQXGame.exe");
    public const string GameExecutableDisplayPath = @"Game\DQXGame.exe";

    [ObservableProperty] [Required] [CustomValidation(typeof(Settings), "ValidateGameFolderPath")]
    private string? _gameFolderPath;

    [ObservableProperty] [Required] private string? _saveFolderPath;

    [ObservableProperty] [Required] private bool? _errorReporting;

    [ObservableProperty] [Required] private string _themeId = DefaultThemeId;

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

    public void Save()
    {
        Directory.CreateDirectory(Paths.AppData);
        File.WriteAllText(Paths.Settings, JsonSerializer.Serialize(this));
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Paths.AppData);
        // Capture a consistent snapshot before yielding to file I/O.
        var json = JsonSerializer.Serialize(this);
        await File.WriteAllTextAsync(Paths.Settings, json, cancellationToken);
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
