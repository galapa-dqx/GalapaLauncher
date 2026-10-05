using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Galapa.Core.Configuration;
using Galapa.TestUtilities;

namespace Galapa.Core.Tests.Services;

[Collection("Sequential")]
public class SettingsTests : IDisposable
{
    private readonly FakeGameInstall _install = new();
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
        this._install.Dispose();
    }

    private Settings ValidSettings(bool errorReporting = false) =>
        new() { GameFolderPath = this._install.Path, ErrorReporting = errorReporting };

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
        var context = new ValidationContext(new Settings());

        var result = Settings.ValidateGameFolderPath(this._install.Path, context);

        Assert.Equal(ValidationResult.Success, result);
    }

    [Fact]
    public void SettingInvalidGameFolderPath_FlagsErrors()
    {
        var settings = this.ValidSettings();
        Assert.False(settings.HasErrors);

        settings.GameFolderPath = Path.Combine(this._tempDir.Path, "Missing");

        Assert.True(settings.HasErrors);
        Assert.Single(settings.GetErrors(nameof(Settings.GameFolderPath)));
    }

    [Fact]
    public void SettingValidGameFolderPath_ClearsErrors()
    {
        var settings = new Settings { GameFolderPath = Path.Combine(this._tempDir.Path, "Missing"), ErrorReporting = false };

        settings.GameFolderPath = this._install.Path;

        Assert.False(settings.HasErrors);
    }

    [Fact]
    public void InstallRoot_FollowsGameFolderPath()
    {
        var settings = this.ValidSettings();
        using var tracker = new PropertyChangedTracker(settings);

        settings.GameFolderPath = Path.Combine(this._tempDir.Path, "Elsewhere");

        Assert.True(tracker.WasPropertyChanged(nameof(Settings.InstallRoot)));
        Assert.Equal(Path.Combine(this._tempDir.Path, "Elsewhere", "Game", "DQXGame.exe"),
            settings.InstallRoot!.ExecutablePath);
    }

    [Fact]
    public void Load_CreatesDefaults_WhenFileDoesNotExist()
    {
        // Arrange & Act
        var settings = Settings.Load();

        // Assert
        Assert.NotNull(settings);
        Assert.NotNull(settings.ErrorReporting);
        Assert.False(settings.ErrorReporting.Value);

        // GameFolderPath will be InstallInfo.Location which might be null
        // We just verify the settings object was created
    }

    [Fact]
    public void Load_WithMissingGameFolder_FlagsErrorsInsteadOfThrowing()
    {
        Directory.CreateDirectory(Paths.AppData);
        var stale = Path.Combine(this._tempDir.Path, "Uninstalled");
        File.WriteAllText(Paths.Settings, JsonSerializer.Serialize(new { GameFolderPath = stale, ErrorReporting = true }));

        var settings = Settings.Load();

        Assert.Equal(stale, settings.GameFolderPath);
        Assert.True(settings.ErrorReporting);
        Assert.True(settings.HasErrors);
        Assert.Single(settings.GetErrors(nameof(Settings.GameFolderPath)));
    }

    [Fact]
    public void Load_WithValidFile_HasNoErrors()
    {
        this.ValidSettings().Save();

        var settings = Settings.Load();

        Assert.False(settings.HasErrors);
    }

    [Fact]
    public void Load_IgnoresLegacySaveFolderPath()
    {
        Directory.CreateDirectory(Paths.AppData);
        File.WriteAllText(Paths.Settings, JsonSerializer.Serialize(new
        {
            GameFolderPath = this._install.Path,
            SaveFolderPath = "C:\\Legacy",
            ErrorReporting = true
        }));

        var settings = Settings.Load();

        Assert.Equal(this._install.Path, settings.GameFolderPath);
        Assert.False(settings.HasErrors);
    }

    [Fact]
    public void Save_PersistsToFile()
    {
        // Arrange
        var settings = this.ValidSettings(true);

        // Act
        settings.Save();

        // Assert
        var settingsPath = Path.Combine(Paths.AppData, "Settings.json");
        Assert.True(File.Exists(settingsPath));

        var jsonContent = File.ReadAllText(settingsPath);
        Assert.Contains(JsonSerializer.Serialize(this._install.Path), jsonContent);
        Assert.Contains("true", jsonContent.ToLower());
        Assert.DoesNotContain(nameof(Settings.HasErrors), jsonContent);
        Assert.DoesNotContain(nameof(Settings.InstallRoot), jsonContent);
    }

    [Fact]
    public void Save_RefusesInvalidSettings()
    {
        var settings = this.ValidSettings();
        settings.GameFolderPath = Path.Combine(this._tempDir.Path, "Missing");

        var refusal = Assert.Throws<InvalidSettingsException>(settings.Save);

        Assert.Contains("Folder does not exist", refusal.Message);
        Assert.False(File.Exists(Paths.Settings));
    }

    [Fact]
    public async Task SaveAsync_RefusesInvalidSettingsAndKeepsExistingFile()
    {
        var settings = this.ValidSettings();
        await settings.SaveAsync();
        var before = await File.ReadAllTextAsync(Paths.Settings);

        settings.GameFolderPath = null;

        await Assert.ThrowsAsync<InvalidSettingsException>(() => settings.SaveAsync());
        Assert.Equal(before, await File.ReadAllTextAsync(Paths.Settings));
    }

    [Fact]
    public void Save_RefusesPropertiesThatWereNeverSet()
    {
        var settings = new Settings { GameFolderPath = this._install.Path };
        Assert.False(settings.HasErrors);

        var refusal = Assert.Throws<InvalidSettingsException>(settings.Save);

        Assert.Contains(refusal.Errors, error => error.MemberNames.Contains(nameof(Settings.ErrorReporting)));
    }

    [Fact]
    public void Save_RefusesFolderThatDisappearedAfterItWasSet()
    {
        using var install = new FakeGameInstall();
        var settings = new Settings { GameFolderPath = install.Path, ErrorReporting = false };
        File.Delete(Path.Combine(install.Path, "Game", "DQXGame.exe"));

        Assert.Throws<InvalidSettingsException>(settings.Save);
    }

    [Fact]
    public void Load_DeserializesExistingFile()
    {
        // Arrange
        this.ValidSettings(true).Save();

        // Act
        var loadedSettings = Settings.Load();

        // Assert
        Assert.NotNull(loadedSettings);
        Assert.Equal(this._install.Path, loadedSettings.GameFolderPath);
        Assert.True(loadedSettings.ErrorReporting);
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

        // Act
        this.ValidSettings().Save();

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
    public void Load_FillsMissingAndNullFieldsWithDefaults()
    {
        Directory.CreateDirectory(Paths.AppData);
        File.WriteAllText(Paths.Settings, """{ "GameFolderPath": "C:\\Game", "ErrorReporting": null }""");

        var settings = Settings.Load();

        Assert.Equal("C:\\Game", settings.GameFolderPath);
        Assert.False(settings.ErrorReporting);
    }

    [Fact]
    public void SaveAndLoad_RoundTrip_PreservesAllValues()
    {
        // Arrange
        var original = this.ValidSettings(true);

        // Act
        original.Save();
        var loaded = Settings.Load();

        // Assert
        Assert.Equal(original.GameFolderPath, loaded.GameFolderPath);
        Assert.Equal(original.ErrorReporting, loaded.ErrorReporting);
    }

    [Fact]
    public async Task SaveAsync_RoundTrip_PreservesAllValues()
    {
        var original = this.ValidSettings(true);

        await original.SaveAsync();
        var loaded = Settings.Load();

        Assert.Equal(original.GameFolderPath, loaded.GameFolderPath);
        Assert.Equal(original.ErrorReporting, loaded.ErrorReporting);
        Assert.Empty(GetTemporaryFiles());
    }

    [Fact]
    public async Task SaveAsync_CancellationPreservesExistingFileAndRemovesTemporaryFile()
    {
        var settings = this.ValidSettings();
        await settings.SaveAsync();
        var before = await File.ReadAllTextAsync(Paths.Settings);

        settings.ErrorReporting = true;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => settings.SaveAsync(cancellation.Token));

        Assert.Equal(before, await File.ReadAllTextAsync(Paths.Settings));
        Assert.Empty(GetTemporaryFiles());
    }

    [Fact]
    public async Task SaveAsync_FailedReplacementPreservesExistingFileAndRemovesTemporaryFile()
    {
        var settings = this.ValidSettings();
        await settings.SaveAsync();
        var before = await File.ReadAllTextAsync(Paths.Settings);

        settings.ErrorReporting = true;
        await using var held = new FileStream(Paths.Settings, FileMode.Open, FileAccess.Read, FileShare.Read);

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => settings.SaveAsync());

        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal(before, await File.ReadAllTextAsync(Paths.Settings));
        Assert.Empty(GetTemporaryFiles());
    }

    [Fact]
    public async Task ConcurrentSyncAndAsyncSavesLeaveOneCompleteSettingsDocument()
    {
        var installs = Enumerable.Range(0, 20).Select(_ => new FakeGameInstall()).ToArray();
        try
        {
            var settings = installs
                .Select((install, index) => new Settings
                {
                    GameFolderPath = install.Path,
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
                    .Where(candidate => loaded.GameFolderPath == installs[candidate].Path));
            Assert.Equal(index % 2 == 0, loaded.ErrorReporting);
            Assert.Empty(GetTemporaryFiles());
        }
        finally
        {
            foreach (var install in installs) install.Dispose();
        }
    }

    [Fact]
    public void Properties_CanBeSetAndRetrieved()
    {
        // Arrange
        var settings = new Settings();

        // Act
        settings.GameFolderPath = "C:\\Test1";
        settings.ErrorReporting = true;

        // Assert
        Assert.Equal("C:\\Test1", settings.GameFolderPath);
        Assert.True(settings.ErrorReporting);
    }

    private static IEnumerable<string> GetTemporaryFiles() =>
        Directory.Exists(Paths.AppData)
            ? Directory.EnumerateFiles(Paths.AppData, "Settings.json.*.tmp")
            : [];
}
