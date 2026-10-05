namespace Galapa.Core.Patcher.ZiPatch;

/// <summary>
/// Thrown by a chunk's <c>ApplyChunk</c> when it cannot be applied (e.g. a SQPK data
/// command targets a .dat that does not exist).
///
/// DQXUpdater's chunk dispatcher (<c>sub_4259c0</c>) returns failure for such a chunk, and
/// the apply loop (<c>ZiPatchApply_DoApply</c> → <c>sub_423b00</c>) then <b>aborts the rest
/// of the patch</b> — it does NOT skip the chunk and continue. <see cref="ZiPatchInstaller"/>
/// catches this and stops, leaving the patch partially applied, which is exactly the
/// oracle's behaviour. Verified by inline-tracing a real apply of game patch
/// 1.6.97629.3→1.6.101161.1: it applies data00000000..data00080000, then hits a DeleteData
/// on the absent data00130000 and stops — so data00150000 (which would otherwise be patched
/// by later chunks) is left untouched.
/// </summary>
public sealed class ZiPatchApplyAbortedException(string relativePath)
    : Exception($"ZiPatch apply aborted: target file does not exist: {relativePath}")
{
    public string RelativePath { get; } = relativePath;
}
