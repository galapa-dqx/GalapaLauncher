using Galapa.Core.Patcher;
using Galapa.Core.Patcher.ZiPatch;
using Galapa.TestUtilities;

namespace Galapa.Core.Tests.Patcher;

/// <summary>
/// The apply outcome must distinguish a full apply from an aborted (partial) one, so a caller
/// isn't blind to a patch that stopped at an unresolvable .dat — the oracle-matching partial apply
/// that DQXUpdater also produces.
/// </summary>
public class ApplyOutcomeTests
{
    [Fact]
    public void InstallPatch_AbortsOnMissingSpanBase_AndReportsTarget()
    {
        // DeleteData on data00130000.dat0 — a span base that doesn't exist — must abort the patch.
        var bytes = new PatchBuilder()
            .FileHeaderV2()
            .TargetInfo(ZiPatchConfig.PlatformId.Win32)
            .DeleteData(mainId: 13, subId: 0, fileId: 0, blockOffset: 0, blockNumber: 1)
            .EndOfFile()
            .Build();

        using var dir = new TempDirectory();
        var patchPath = Path.Combine(dir.Path, "abort.patch");
        File.WriteAllBytes(patchPath, bytes);

        var result = ZiPatchInstaller.InstallPatch(patchPath, dir.Path);

        Assert.True(result.Aborted);
        Assert.Contains("data00130000", result.AbortedTarget!);
    }

    [Fact]
    public void InstallPatch_CleanPatch_ReportsNotAborted()
    {
        var bytes = new PatchBuilder()
            .FileHeaderV2()
            .AddFile("Bin/hello.txt", [PatchBuilder.StoredBlock("hi"u8.ToArray())])
            .EndOfFile()
            .Build();

        using var dir = new TempDirectory();
        var patchPath = Path.Combine(dir.Path, "clean.patch");
        File.WriteAllBytes(patchPath, bytes);

        var result = ZiPatchInstaller.InstallPatch(patchPath, dir.Path);

        Assert.False(result.Aborted);
        Assert.Null(result.AbortedTarget);
    }
}
