using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Galapa.Core.Configuration;
using Galapa.TestUtilities;

namespace Galapa.Core.Tests.Services;

[Collection("Sequential")]
public class SettingsTests : IDisposable
{
    private readonly TempDirectory _tempDir;

    public SettingsTests()
    {
        // Set up temporary directory for test isolation
        this._tempDir = new TempDirectory();
        Paths.AppData = this._tempDir.Path;
    }

    public void Dispose()
    {
        // Reset to default path
        Paths.AppData = null;
        this._tempDir.Dispose();
    }

    [Fact]
    public void ValidateGameFolderPath_RejectsNonExistentDirectory()
    {
        // Arrange
        var nonExistentPath = Path.Combine(this._tempDir.Path, "NonExistent");
        var context = new ValidationContext(new Settings());

        // Act
        var result = Settings.ValidateGameFolderPath(nonExistentPath, context);

        // Assert
        Assert.NotNull(result);
        Assert.NotEqual(ValidationResult.Success, result);
        Assert.Equal("Folder does not exist", result.ErrorMessage);
    }

    [Fact]
    public void ValidateGameFolderPath_RejectsMissingDQXGameExe()
    {
        // Arrange
        var gameFolderPath = Path.Combine(this._tempDir.Path, "GameFolder");
        Directory.CreateDirectory(gameFolderPath);
        Directory.CreateDirectory(Path.Combine(gameFolderPath, "Game"));
        // Note: NOT creating DQXGame.exe

        var context = new ValidationContext(new Settings());

        // Act
        var result = Settings.ValidateGameFolderPath(gameFolderPath, context);

        // Assert
        Assert.NotNull(result);
        Assert.NotEqual(ValidationResult.Success, result);
        Assert.Equal("DQXGame.exe does not exist", result.ErrorMessage);
    }

    [Fact]
    public void ValidateGameFolderPath_AcceptsValidDirectory()
    {
        // Arrange
        var gameFolderPath = Path.Combine(this._tempDir.Path, "GameFolder");
        var gameSubFolder = Path.Combine(gameFolderPath, "Game");
        Directory.CreateDirectory(gameSubFolder);

        var exePath = Path.Combine(gameSubFolder, "DQXGame.exe");
        File.WriteAllText(exePath, "fake exe");

        var context = new ValidationContext(new Settings());

        // Act
        var result = Settings.ValidateGameFolderPath(gameFolderPath, context);

        // Assert
        Assert.Equal(ValidationResult.Success, result);
    }

    [Fact]
    public void Load_CreatesDefaults_WhenFileDoesNotExist()
    {
        // Arrange & Act
        var settings = Settings.Load();

        // Assert
        Assert.NotNull(settings);
        Assert.NotNull(settings.SaveFolderPath);
        Assert.NotNull(settings.ErrorReporting);
        Assert.False(settings.ErrorReporting.Value);

        // GameFolderPath will be InstallInfo.Location which might be null
        // We just verify the settings object was created
    }

    [Fact]
    public void Save_PersistsToFile()
    {
        // Arrange
        var settings = new Settings
        {
            GameFolderPath = "C:\\TestGamePath",
            SaveFolderPath = "C:\\TestSavePath",
            ErrorReporting = true
        };

        // Act
        settings.Save();

        // Assert
        var settingsPath = Path.Combine(Paths.AppData, "Settings.json");
        Assert.True(File.Exists(settingsPath));

        var jsonContent = File.ReadAllText(settingsPath);
        Assert.Contains("TestGamePath", jsonContent);
        Assert.Contains("TestSavePath", jsonContent);
        Assert.Contains("true", jsonContent.ToLower());
    }

    [Fact]
    public void Load_DeserializesExistingFile()
    {
        // Arrange
        var originalSettings = new Settings
        {
            GameFolderPath = "C:\\OriginalGamePath",
            SaveFolderPath = "C:\\OriginalSavePath",
            ErrorReporting = true
        };
        originalSettings.Save();

        // Act
        var loadedSettings = Settings.Load();

        // Assert
        Assert.NotNull(loadedSettings);
        Assert.Equal("C:\\OriginalGamePath", loadedSettings.GameFolderPath);
        Assert.Equal("C:\\OriginalSavePath", loadedSettings.SaveFolderPath);
        Assert.True(loadedSettings.ErrorReporting);
    }

    [Fact]
    public void DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var settings = Settings.Load();

        // Assert
        Assert.NotNull(settings.ErrorReporting);
        Assert.False(settings.ErrorReporting.Value);

        // Default save folder should contain "Dragon Quest X"
        Assert.NotNull(settings.SaveFolderPath);
        Assert.Contains("Dragon Quest X", settings.SaveFolderPath);
    }

    [Fact]
    public void GameFolderPath_PropertyChanged_Fires()
    {
        // Arrange
        var settings = new Settings();
        using var tracker = new PropertyChangedTracker(settings);

        // Act
        settings.GameFolderPath = "C:\\NewPath";

        // Assert
        Assert.True(tracker.WasPropertyChanged(nameof(Settings.GameFolderPath)));
    }

    [Fact]
    public void SaveFolderPath_PropertyChanged_Fires()
    {
        // Arrange
        var settings = new Settings();
        using var tracker = new PropertyChangedTracker(settings);

        // Act
        settings.SaveFolderPath = "C:\\NewSavePath";

        // Assert
        Assert.True(tracker.WasPropertyChanged(nameof(Settings.SaveFolderPath)));
    }

    [Fact]
    public void ErrorReporting_PropertyChanged_Fires()
    {
        // Arrange
        var settings = new Settings();
        using var tracker = new PropertyChangedTracker(settings);

        // Act
        settings.ErrorReporting = true;

        // Assert
        Assert.True(tracker.WasPropertyChanged(nameof(Settings.ErrorReporting)));
    }

    [Fact]
    public void Save_CreatesDirectoryIfNotExists()
    {
        // Arrange - Use a subdirectory that doesn't exist yet
        var subDir = Path.Combine(this._tempDir.Path, "NewSubDir");
        Paths.AppData = subDir;

        var settings = new Settings
        {
            GameFolderPath = "C:\\TestPath",
            SaveFolderPath = "C:\\TestSave",
            ErrorReporting = false
        };

        // Act
        settings.Save();

        // Assert
        Assert.True(Directory.Exists(Paths.AppData));
        Assert.True(File.Exists(Path.Combine(Paths.AppData, "Settings.json")));
    }

    [Fact]
    public void Load_ReturnsDefaultsOnInvalidJson()
    {
        // Arrange
        var settingsPath = Path.Combine(Paths.AppData, "Settings.json");
        Directory.CreateDirectory(Paths.AppData);
        File.WriteAllText(settingsPath, "{ invalid json content }");

        // Act
        var settings = Settings.Load();

        // Assert - should return defaults instead of crashing
        Assert.NotNull(settings);
        Assert.NotNull(settings.ErrorReporting);
    }

    [Fact]
    public void SaveAndLoad_RoundTrip_PreservesAllValues()
    {
        // Arrange
        var original = new Settings
        {
            GameFolderPath = "C:\\RoundTripGame",
            SaveFolderPath = "C:\\RoundTripSave",
            ErrorReporting = true
        };

        // Act
        original.Save();
        var loaded = Settings.Load();

        // Assert
        Assert.Equal(original.GameFolderPath, loaded.GameFolderPath);
        Assert.Equal(original.SaveFolderPath, loaded.SaveFolderPath);
        Assert.Equal(original.ErrorReporting, loaded.ErrorReporting);
    }

    [Fact]
    public async Task SaveAsync_RoundTrip_PreservesAllValues()
    {
        var original = new Settings
        {
            GameFolderPath = "C:\\AsyncRoundTripGame",
            SaveFolderPath = "C:\\AsyncRoundTripSave",
            ErrorReporting = true
        };

        await original.SaveAsync();
        var loaded = Settings.Load();

        Assert.Equal(original.GameFolderPath, loaded.GameFolderPath);
        Assert.Equal(original.SaveFolderPath, loaded.SaveFolderPath);
        Assert.Equal(original.ErrorReporting, loaded.ErrorReporting);
        Assert.Empty(GetTemporaryFiles());
    }

    [Fact]
    public async Task SaveAsync_CancellationPreservesExistingFileAndRemovesTemporaryFile()
    {
        var settings = new Settings
        {
            GameFolderPath = "C:\\OriginalGame",
            SaveFolderPath = "C:\\OriginalSave",
            ErrorReporting = false
        };
        await settings.SaveAsync();
        var before = await File.ReadAllTextAsync(Paths.Settings);

        settings.GameFolderPath = "C:\\ReplacementGame";
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => settings.SaveAsync(cancellation.Token));

        Assert.Equal(before, await File.ReadAllTextAsync(Paths.Settings));
        Assert.Empty(GetTemporaryFiles());
    }

    [Fact]
    public async Task SaveAsync_FailedReplacementPreservesExistingFileAndRemovesTemporaryFile()
    {
        var settings = new Settings
        {
            GameFolderPath = "C:\\OriginalGame",
            SaveFolderPath = "C:\\OriginalSave",
            ErrorReporting = false
        };
        await settings.SaveAsync();
        var before = await File.ReadAllTextAsync(Paths.Settings);

        settings.GameFolderPath = "C:\\ReplacementGame";
        await using var held = new FileStream(Paths.Settings, FileMode.Open, FileAccess.Read, FileShare.Read);

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => settings.SaveAsync());

        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal(before, await File.ReadAllTextAsync(Paths.Settings));
        Assert.Empty(GetTemporaryFiles());
    }

    [Fact]
    public async Task ConcurrentSyncAndAsyncSavesLeaveOneCompleteSettingsDocument()
    {
        var settings = Enumerable.Range(0, 20)
            .Select(index => new Settings
            {
                GameFolderPath = $"game-{index}",
                SaveFolderPath = $"save-{index}",
                ErrorReporting = index % 2 == 0
            })
            .ToArray();

        var saves = settings.Select((value, index) => index % 2 == 0
            ? Task.Run(value.Save)
            : value.SaveAsync());

        await Task.WhenAll(saves);

        var json = await File.ReadAllTextAsync(Paths.Settings);
        var loaded = JsonSerializer.Deserialize<Settings>(json);
        Assert.NotNull(loaded);
        var index = Assert.Single(
            Enumerable.Range(0, settings.Length)
                .Where(candidate => loaded.GameFolderPath == $"game-{candidate}"));
        Assert.Equal($"save-{index}", loaded.SaveFolderPath);
        Assert.Equal(index % 2 == 0, loaded.ErrorReporting);
        Assert.Empty(GetTemporaryFiles());
    }

    [Fact]
    public void ValidateGameFolderPath_WithGameSubfolder_WorksCorrectly()
    {
        // Arrange
        var gameFolderPath = Path.Combine(this._tempDir.Path, "InstallFolder");
        var gameSubFolder = Path.Combine(gameFolderPath, "Game");
        Directory.CreateDirectory(gameSubFolder);
        File.WriteAllText(Path.Combine(gameSubFolder, "DQXGame.exe"), "");

        var context = new ValidationContext(new Settings());

        // Act
        var result = Settings.ValidateGameFolderPath(gameFolderPath, context);

        // Assert
        Assert.Equal(ValidationResult.Success, result);
    }

    [Fact]
    public void Properties_CanBeSetAndRetrieved()
    {
        // Arrange
        var settings = new Settings();

        // Act
        settings.GameFolderPath = "C:\\Test1";
        settings.SaveFolderPath = "C:\\Test2";
        settings.ErrorReporting = true;

        // Assert
        Assert.Equal("C:\\Test1", settings.GameFolderPath);
        Assert.Equal("C:\\Test2", settings.SaveFolderPath);
        Assert.True(settings.ErrorReporting);
    }

    private static IEnumerable<string> GetTemporaryFiles() =>
        Directory.Exists(Paths.AppData)
            ? Directory.EnumerateFiles(Paths.AppData, "Settings.json.*.tmp")
            : [];
}
