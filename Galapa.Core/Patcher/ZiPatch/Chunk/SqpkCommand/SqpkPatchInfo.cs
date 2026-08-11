using Galapa.Core.Patcher.ZiPatch.Util;

namespace Galapa.Core.Patcher.ZiPatch.Chunk.SqpkCommand;

/// <summary>SQPK 'X' — patch info. A NOP on modern patchers; parsed for completeness.</summary>
internal sealed class SqpkPatchInfo(BinaryReader reader, long offset, long size)
    : SqpkChunk(reader, offset, size)
{
    public const string CommandName = "X";

    public byte Status { get; private set; }
    public byte Version { get; private set; }
    public ulong InstallSize { get; private set; }

    protected override void ReadChunk()
    {
        using var advanceAfter = GetAdvanceOnDispose();
        Status = Reader.ReadByte();
        Version = Reader.ReadByte();
        Reader.ReadByte(); // Alignment

        InstallSize = Reader.ReadUInt64BE();
    }

    public override string ToString() => $"{TypeName}:{CommandName}:{Status}:{Version}:{InstallSize}";
}
