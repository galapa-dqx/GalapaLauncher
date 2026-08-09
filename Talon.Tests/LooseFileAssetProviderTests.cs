using Talon.Vfs;

namespace Talon.Tests;

public sealed class LooseFileAssetProviderTests : IDisposable
{
    private readonly string root =
        Path.Combine(Path.GetTempPath(), $"talon-vfs-{Guid.NewGuid():N}");
    private readonly string outside =
        Path.Combine(Path.GetTempPath(), $"talon-vfs-outside-{Guid.NewGuid():N}");

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

    [Fact]
    public void MissingOverrideReturnsFalse()
    {
        Directory.CreateDirectory(root);
        var provider = new LooseFileAssetProvider(root);

        Assert.False(provider.TryOpen("ui/missing.bin", out _));
    }

    [Fact]
    public void RejectsSiblingPrefixEscape()
    {
        Directory.CreateDirectory(root);
        var sibling = root + "-evil";
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "message.bin"), "outside");
        try
        {
            var provider = new LooseFileAssetProvider(root);
            Assert.False(provider.TryResolve("../" + Path.GetFileName(sibling) + "/message.bin", out _));
        }
        finally { Directory.Delete(sibling, recursive: true); }
    }

    [Fact]
    public void RejectsSymbolicLinkEscape()
    {
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.bin"), "outside");
        Directory.CreateSymbolicLink(Path.Combine(root, "link"), outside);

        var provider = new LooseFileAssetProvider(root);

        Assert.False(provider.TryResolve("link/secret.bin", out _));
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        if (Directory.Exists(outside)) Directory.Delete(outside, recursive: true);
    }
}
