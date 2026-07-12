using Galapa.Core.Patcher.ZiPatch.Util;

namespace Galapa.Core.Patcher.ZiPatch.Chunk;

/// <summary>DELD — delete a directory under the game root.</summary>
public sealed class DeleteDirectoryChunk(BinaryReader reader, long offset, long size)
    : ZiPatchChunk(reader, offset, size)
{
    public const string TypeName = "DELD";
    public override string ChunkType => TypeName;

    public string DirName { get; private set; } = string.Empty;

    protected override void ReadChunk()
    {
        using var advanceAfter = GetAdvanceOnDispose();
        var dirNameLen = Reader.ReadUInt32BE();
        DirName = Reader.ReadFixedLengthString(dirNameLen);
    }

    public override void ApplyChunk(ZiPatchConfig config)
    {
        var dir = SqexFile.ResolveUnderBase(config.GamePath, DirName);

        // DELD targets a directory that earlier chunks have emptied; tolerate it already being
        // gone rather than throwing out of the whole apply. Non-recursive, matching the format.
        if (Directory.Exists(dir))
            Directory.Delete(dir);
    }

    public override string ToString() => $"{TypeName}:{DirName}";
}
