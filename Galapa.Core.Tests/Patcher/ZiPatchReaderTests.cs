using System.Text;
using Galapa.Core.Patcher.ZiPatch;
using Galapa.Core.Patcher.ZiPatch.Chunk;
using Galapa.Core.Patcher.ZiPatch.Util;
using Galapa.TestUtilities;

namespace Galapa.Core.Tests.Patcher;

public class ZiPatchReaderTests
{
    [Fact]
    public void Constructor_RejectsBadMagic()
    {
        var notAPatch = new MemoryStream("not a zipatch file at all"u8.ToArray());
        Assert.Throws<ZiPatchException>(() => new ZiPatchFile(notAPatch));
    }

    [Fact]
    public void GetChunks_WalksToEndOfFile_WithExpectedTypes()
    {
        var patch = new PatchBuilder()
            .FileHeaderV2()
            .TargetInfo()
            .AddFile("a.bin", [PatchBuilder.StoredBlock("hello"u8.ToArray())])
            .EndOfFile();

        using var file = new ZiPatchFile(patch.ToStream());
        var types = file.GetChunks().Select(c => c.ChunkType).ToList();

        Assert.Equal(["FHDR", "SQPK", "SQPK", "EOF_"], types);
    }

    [Fact]
    public void AddFile_StoredBlock_WritesVerbatimBytes()
    {
        var payload = "the quick brown fox"u8.ToArray();
        using var dir = new TempDirectory();

        Apply(new PatchBuilder().AddFile("stored.bin", [PatchBuilder.StoredBlock(payload)]).EndOfFile(), dir.Path);

        Assert.Equal(payload, File.ReadAllBytes(Path.Combine(dir.Path, "stored.bin")));
    }

    [Fact]
    public void AddFile_DeflateBlock_RoundTripsThroughInflate()
    {
        // Larger, compressible payload so the deflate path is actually taken.
        var payload = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("DRAGON QUEST X! ", 4000)));
        using var dir = new TempDirectory();

        Apply(new PatchBuilder().AddFile("deflated.bin", [PatchBuilder.DeflateBlock(payload)]).EndOfFile(), dir.Path);

        Assert.Equal(payload, File.ReadAllBytes(Path.Combine(dir.Path, "deflated.bin")));
    }

    [Fact]
    public void AddFile_MultipleBlocks_AreConcatenatedInOrder()
    {
        var part1 = Encoding.ASCII.GetBytes(new string('A', 5000));
        var part2 = "small stored tail"u8.ToArray();
        using var dir = new TempDirectory();

        Apply(
            new PatchBuilder()
                .AddFile("multi.bin", [PatchBuilder.DeflateBlock(part1), PatchBuilder.StoredBlock(part2)])
                .EndOfFile(),
            dir.Path);

        Assert.Equal(part1.Concat(part2), File.ReadAllBytes(Path.Combine(dir.Path, "multi.bin")));
    }

    [Fact]
    public void AddFile_ForwardSlashPath_CreatesNestedDirectories()
    {
        var payload = "nested"u8.ToArray();
        using var dir = new TempDirectory();

        Apply(new PatchBuilder().AddFile("Bin/sub/file.bin", [PatchBuilder.StoredBlock(payload)]).EndOfFile(), dir.Path);

        Assert.Equal(payload, File.ReadAllBytes(Path.Combine(dir.Path, "Bin", "sub", "file.bin")));
    }

    [Fact]
    public void DeleteFile_RemovesAPreviouslyAddedFile()
    {
        using var dir = new TempDirectory();

        Apply(
            new PatchBuilder()
                .AddFile("doomed.bin", [PatchBuilder.StoredBlock("bytes"u8.ToArray())])
                .DeleteFile("doomed.bin")
                .EndOfFile(),
            dir.Path);

        Assert.False(File.Exists(Path.Combine(dir.Path, "doomed.bin")));
    }

    [Fact]
    public void GetChunks_WithChecksumValidation_AcceptsWellFormedCrcs()
    {
        var patch = new PatchBuilder()
            .FileHeaderV2()
            .AddFile("a.bin", [PatchBuilder.StoredBlock("crc me"u8.ToArray())])
            .EndOfFile();

        using var file = new ZiPatchFile(patch.ToStream(), needsChecksum: true);

        Assert.All(file.GetChunks(), chunk => Assert.True(chunk.IsChecksumValid, $"{chunk.ChunkType} CRC mismatch"));
    }

    private static void Apply(PatchBuilder builder, string gamePath)
    {
        using var file = new ZiPatchFile(builder.ToStream());
        using var store = new SqexFileStreamStore();
        var config = new ZiPatchConfig(gamePath) { Store = store };

        foreach (var chunk in file.GetChunks())
            chunk.ApplyChunk(config);
    }
}
