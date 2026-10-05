using Galapa.Core.Patcher.ZiPatch;
using Galapa.Core.Patcher.ZiPatch.Util;

namespace Galapa.Core.Patcher;

/// <summary>
/// Applies DQX ZiPatch (.patch) files to a game installation. This is the reusable
/// entry point shared by the Galapa.PatchInstaller CLI and (eventually) the launcher.
/// </summary>
public static class ZiPatchInstaller
{
    /// <summary>
    /// Applies a single patch file into <paramref name="gamePath"/> (the directory the
    /// patch's relative paths are resolved against, e.g. a Boot or Game/Content root).
    /// </summary>
    /// <param name="patchPath">Path to the .patch file.</param>
    /// <param name="gamePath">Target directory to apply into.</param>
    /// <param name="progress">Optional callback invoked once per applied chunk.</param>
    public static ZiPatchApplyResult InstallPatch(string patchPath, string gamePath, Action<ZiPatch.Chunk.ZiPatchChunk>? progress = null)
    {
        using var patchFile = ZiPatchFile.FromFileName(patchPath);
        using var store = new SqexFileStreamStore();

        var config = new ZiPatchConfig(gamePath) { Store = store };

        foreach (var chunk in patchFile.GetChunks())
        {
            try
            {
                chunk.ApplyChunk(config);
            }
            catch (ZiPatch.ZiPatchApplyAbortedException ex)
            {
                // DQXUpdater aborts the remainder of the patch when a chunk fails (e.g. a SQPK
                // data command targeting a .dat that doesn't exist). Stop here, leaving the patch
                // partially applied — this matches the oracle byte-for-byte. The abort is an
                // EXPECTED outcome for a patch that touches a repository this install lacks, not a
                // failure; surface it so callers can log/branch rather than mistake a partial
                // apply for a full one. (Genuine errors throw other exceptions and propagate.)
                return new ZiPatchApplyResult(Aborted: true, AbortedTarget: ex.RelativePath);
            }

            progress?.Invoke(chunk);
        }

        return new ZiPatchApplyResult(Aborted: false, AbortedTarget: null);
    }

    /// <summary>
    /// Applies <paramref name="patchPaths"/> in order into <paramref name="gamePath"/>.
    /// Patches must be supplied oldest-to-newest. Returns one result per patch, in order.
    ///
    /// A patch that aborts does NOT stop the chain: a DQX abort only skips content for a repository
    /// this install doesn't have, and later patches build on the repositories that ARE present
    /// (which were applied in full), so continuing matches DQXUpdater byte-for-byte across the whole
    /// 1.6→current chain. A caller that wants stop-on-abort can inspect the returned results and stop.
    /// </summary>
    public static IReadOnlyList<ZiPatchApplyResult> InstallPatches(IEnumerable<string> patchPaths, string gamePath, Action<ZiPatch.Chunk.ZiPatchChunk>? progress = null)
    {
        var results = new List<ZiPatchApplyResult>();
        foreach (var patchPath in patchPaths)
            results.Add(InstallPatch(patchPath, gamePath, progress));
        return results;
    }
}

/// <summary>
/// Outcome of applying one patch. <see cref="Aborted"/> is true when a SQPK data command targeted a
/// .dat that could not be resolved, so the rest of the patch was skipped — the same partial apply
/// DQXUpdater produces (expected when a patch touches a repository this install doesn't have), not
/// an error. <see cref="AbortedTarget"/> is the relative path that triggered the abort, else null.
/// </summary>
public readonly record struct ZiPatchApplyResult(bool Aborted, string? AbortedTarget);
