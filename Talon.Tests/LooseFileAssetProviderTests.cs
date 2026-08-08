using Talon.Vfs;

namespace Talon.Tests;

public sealed class LooseFileAssetProviderTests : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), $"talon-vfs-{Guid.NewGuid():N}");

    [Fact]
    public void ResolvesExistingFileBelowRoot()
    {
        Directory.CreateDirectory(Path.Combine(root, "ui"));
        File.WriteAllText(Path.Combine(root, "ui", "message.bin"), "translated");
        var provider = new LooseFileAssetProvider(root);

        Assert.True(provider.TryResolve("ui/message.bin", out var result));
        Assert.Equal(
            Path.Combine(root, "ui", "message.bin"),
            result,
            ignoreCase: true);
    }

    [Theory]
    [InlineData("../outside.bin")]
    [InlineData("ui/../../outside.bin")]
    [InlineData(@"C:\outside.bin")]
    [InlineData(@"\\server\share\outside.bin")]
    [InlineData("translated.bin:stream")]
    [InlineData("bad\0path")]
    public void RejectsUnsafeOrInvalidPaths(string path)
    {
        Directory.CreateDirectory(root);
        var provider = new LooseFileAssetProvider(root);

        Assert.False(provider.TryResolve(path, out _));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
