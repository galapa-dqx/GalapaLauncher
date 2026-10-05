using System.ComponentModel.DataAnnotations;
using Galapa.Core.Configuration;
using Galapa.Core.Game;
using Galapa.TestUtilities;

namespace Galapa.Core.Tests.Services;

[Collection("Sequential")]
public class PathModelTests : IDisposable
{
    private const string SessionId = "0123456789abcdef0123456789abcdef0123456789abcdef01234567";

    public void Dispose()
    {
        SaveRoot.Location = null;
    }

    [Fact]
    public void InstallRoot_DerivesGameDirectoryAndExecutable()
    {
        var root = new InstallRoot(@"D:\Games\DQX");

        Assert.Equal(@"D:\Games\DQX\Game", root.GameDirectory);
        Assert.Equal(@"D:\Games\DQX\Game\DQXGame.exe", root.ExecutablePath);
        Assert.Equal(@"Game\DQXGame.exe", InstallRoot.RelativeExecutablePath);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void InstallRoot_Validate_RejectsBlank(string? location)
    {
        Assert.Equal("Folder does not exist", InstallRoot.Validate(location).ErrorMessage);
    }

    [Fact]
    public void InstallRoot_Validate_AcceptsInstall()
    {
        using var install = new FakeGameInstall();

        Assert.Equal(ValidationResult.Success, InstallRoot.Validate(install.Path));
    }

    [Fact]
    public void SaveRoot_DefaultsToMyGamesFolder()
    {
        Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Dragon Quest X"),
            SaveRoot.Location);
    }

    [Fact]
    public void SaveRoot_OverrideAndReset()
    {
        using var folder = new TempDirectory();

        SaveRoot.Location = folder.Path;
        Assert.Equal(folder.Path, SaveRoot.Location);

        SaveRoot.Location = null;
        Assert.Equal(SaveRoot.Default, SaveRoot.Location);
    }

    [Fact]
    public void GameProcess_DerivesEveryPathFromTheCurrentInstallRoot()
    {
        using var first = new FakeGameInstall();
        using var second = new FakeGameInstall();
        var settings = new Settings { GameFolderPath = first.Path, ErrorReporting = false };
        var process = new GameProcess(settings) { SessionId = SessionId };

        settings.GameFolderPath = second.Path;

        Assert.Equal(Path.Combine(second.Path, "Game"), process.WorkingDirectory);
        Assert.StartsWith($"\"{Path.Combine(second.Path, "Game", "DQXGame.exe")}\" ", process.BuildCommandLine());
    }

    [Fact]
    public void GameProcess_RefusesInvalidInstallRoot()
    {
        using var folder = new TempDirectory();
        var settings = new Settings { GameFolderPath = folder.Path, ErrorReporting = false };
        var process = new GameProcess(settings) { SessionId = SessionId };

        Assert.Throws<InvalidOperationException>(process.BuildCommandLine);
        Assert.Throws<InvalidOperationException>(() => process.WorkingDirectory);
        Assert.Throws<InvalidOperationException>(process.Start);
    }
}
