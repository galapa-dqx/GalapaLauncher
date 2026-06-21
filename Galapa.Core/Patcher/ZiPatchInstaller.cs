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
    public static void InstallPatch(string patchPath, string gamePath, Action<ZiPatch.Chunk.ZiPatchChunk>? progress = null)
    {
        using var patchFile = ZiPatchFile.FromFileName(patchPath);
        using var store = new SqexFileStreamStore();

        var config = new ZiPatchConfig(gamePath) { Store = store };

        foreach (var chunk in patchFile.GetChunks())
        {
            chunk.ApplyChunk(config);
            progress?.Invoke(chunk);
        }
    }

    /// <summary>
    /// Applies <paramref name="patchPaths"/> in order into <paramref name="gamePath"/>.
    /// Patches must be supplied oldest-to-newest.
    /// </summary>
    public static void InstallPatches(IEnumerable<string> patchPaths, string gamePath, Action<ZiPatch.Chunk.ZiPatchChunk>? progress = null)
    {
        foreach (var patchPath in patchPaths)
            InstallPatch(patchPath, gamePath, progress);
    }
}
