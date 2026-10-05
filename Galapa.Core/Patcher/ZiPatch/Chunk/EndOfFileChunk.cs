namespace Galapa.Core.Patcher.ZiPatch.Chunk;

/// <summary>EOF_ — terminates the chunk stream. Empty payload.</summary>
public sealed class EndOfFileChunk(BinaryReader reader, long offset, long size)
    : ZiPatchChunk(reader, offset, size)
{
    public const string TypeName = "EOF_";
    public override string ChunkType => TypeName;

    public override string ToString() => TypeName;
}
