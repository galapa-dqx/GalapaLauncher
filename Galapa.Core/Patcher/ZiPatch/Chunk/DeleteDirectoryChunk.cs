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

    public override void ApplyChunk(ZiPatchConfig config) =>
        Directory.Delete($"{config.GamePath}/{DirName.TrimStart('/', '\\')}");

    public override string ToString() => $"{TypeName}:{DirName}";
}
