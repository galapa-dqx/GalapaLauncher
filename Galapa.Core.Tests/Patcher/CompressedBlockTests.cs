using Galapa.Core.Patcher;
using Galapa.Core.Patcher.ZiPatch;
using Galapa.TestUtilities;

namespace Galapa.Core.Tests.Patcher;

/// <summary>
/// A compressed AddFile block must inflate to exactly its declared size. A block whose deflate data
/// expands past that (a decompression bomb) is rejected rather than growing the target file unbounded.
/// </summary>
public class CompressedBlockTests
{
    [Fact]
    public void InstallPatch_RejectsDecompressionBomb()
    {
        // Deflate 4096 bytes but declare only 16 — DecompressInto must stop and throw.
        var payload = new byte[4096];
        var bytes = new PatchBuilder()
            .FileHeaderV2()
            .AddFile("Bin/bomb.bin", [PatchBuilder.OversizedDeflateBlock(payload, declaredDecompressedSize: 16)])
            .EndOfFile()
            .Build();

        using var dir = new TempDirectory();
        var patchPath = Path.Combine(dir.Path, "bomb.patch");
        File.WriteAllBytes(patchPath, bytes);

        Assert.Throws<ZiPatchException>(() => ZiPatchInstaller.InstallPatch(patchPath, dir.Path));
    }
}
