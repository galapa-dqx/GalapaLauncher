using Galapa.Core.Patcher.ZiPatch.Util;

namespace Galapa.Core.Patcher.ZiPatch.Chunk;

/// <summary>
/// APFS — apply free space. A NOP on modern patchers and absent from the DQX sample
/// patches, so the two fields here are theoretical (mirrored from FFXIV).
/// </summary>
public sealed class ApplyFreeSpaceChunk(BinaryReader reader, long offset, long size)
    : ZiPatchChunk(reader, offset, size)
{
    public const string TypeName = "APFS";
    public override string ChunkType => TypeName;

    public long UnknownFieldA { get; private set; }
    public long UnknownFieldB { get; private set; }

    protected override void ReadChunk()
    {
        using var advanceAfter = GetAdvanceOnDispose();
        UnknownFieldA = Reader.ReadInt64BE();
        UnknownFieldB = Reader.ReadInt64BE();
    }

    public override string ToString() => $"{TypeName}:{UnknownFieldA}:{UnknownFieldB}";
}
