using Galapa.Core.Configuration;
using Galapa.Core.Game;

namespace Galapa.Core.Tests.Game;

public sealed class GameProcessTests
{
    [Fact]
    public void WorkingDirectoryUsesExecutableRelativePath()
    {
        var root = Path.Combine("C:\\", "Dragon Quest X");
        var process = new GameProcess(new Settings { GameFolderPath = root });

        Assert.Equal(
            Path.Combine(root, Path.GetDirectoryName(Settings.GameExecutableRelativePath)!),
            process.WorkingDirectory);
    }

    [Fact]
    public void DisplayPathUsesTheSameExecutableRelativePath()
    {
        Assert.Equal(
            Settings.GameExecutableRelativePath
                .Replace(Path.DirectorySeparatorChar, '\\')
                .Replace(Path.AltDirectorySeparatorChar, '\\'),
            Settings.GameExecutableDisplayPath);
    }
}
