using Galapa.Core.Patcher;
using Galapa.Core.Patcher.ZiPatch;
using Galapa.TestUtilities;

namespace Galapa.Core.Tests.Patcher;

/// <summary>
/// Differential tests against the DQX updater "oracle": apply a real patch with our
/// patcher and compare byte-for-byte to output produced by the genuine
/// DQXUpdater.exe::ZiPatchApply::DoApply (see dqx-zipatch/oracle).
///
/// These require the dqx-zipatch research tree (patches + oracle output). Point at it
/// with the DQX_ZIPATCH_DIR environment variable; otherwise a couple of well-known
/// Desktop locations are tried. When the tree isn't present (e.g. CI) the tests
/// no-op rather than fail.
/// </summary>
public class OracleDifferentialTests
{
    private const string BootPatchRelative = "Boot/1.6.0.0-7.6.332245.1.patch";
    private const string BootOracleRelative = "oracle/out_boot";

    [Fact]
    public void BootPatch_AppliedToEmptyDir_MatchesOracleOutputByteForByte()
    {
        if (FindFixtureDir() is not { } fixtures)
            return;

        var patch = Path.Combine(fixtures, "Boot", "1.6.0.0-7.6.332245.1.patch");
        var oracleOut = Path.Combine(fixtures, "oracle", "out_boot");
        if (!File.Exists(patch) || !Directory.Exists(oracleOut))
            return;

        using var dir = new TempDirectory();
        ZiPatchInstaller.InstallPatch(patch, dir.Path);

        var diffs = DirectoryComparer.Compare(oracleOut, dir.Path).ToList();
        Assert.True(diffs.Count == 0, "Output differs from the DQX updater oracle:\n  " + string.Join("\n  ", diffs));
    }

    [Fact]
    public void BootPatch_HasExpectedChunkShape()
    {
        if (FindFixtureDir() is not { } fixtures)
            return;

        var patch = Path.Combine(fixtures, "Boot", "1.6.0.0-7.6.332245.1.patch");
        if (!File.Exists(patch))
            return;

        using var file = ZiPatchFile.FromFileName(patch);
        var byType = file.GetChunks()
            .GroupBy(c => c.ChunkType)
            .ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(1, byType["FHDR"]);
        Assert.Equal(2, byType["APLY"]);
        Assert.Equal(24, byType["SQPK"]);
        Assert.Equal(1, byType["EOF_"]);
    }

    [Fact]
    public void BootPatch_EveryChunkChecksumIsValid()
    {
        if (FindFixtureDir() is not { } fixtures)
            return;

        var patch = Path.Combine(fixtures, "Boot", "1.6.0.0-7.6.332245.1.patch");
        if (!File.Exists(patch))
            return;

        using var file = ZiPatchFile.FromFileName(patch, needsChecksum: true);
        Assert.All(file.GetChunks(), chunk => Assert.True(chunk.IsChecksumValid, $"{chunk} has an invalid CRC"));
    }

    private static string? FindFixtureDir()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("DQX_ZIPATCH_DIR"),
            "/Users/emma/Desktop/dqx-zipatch",
            @"\\psf\Home\Desktop\dqx-zipatch",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Desktop", "dqx-zipatch"),
        };

        return candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && Directory.Exists(c));
    }
}
