using Galapa.Core.Patcher.ZiPatch.Util;

namespace Galapa.Core.Patcher.ZiPatch.Chunk.SqpkCommand;

/// <summary>SQPK 'I' — index add/delete. A NOP on modern patchers; parsed for completeness.</summary>
internal sealed class SqpkIndex(BinaryReader reader, long offset, long size)
    : SqpkChunk(reader, offset, size)
{
    public const string CommandName = "I";

    public enum IndexCommandKind : byte
    {
        Add = (byte)'A',
        Delete = (byte)'D',
    }

    public IndexCommandKind IndexCommand { get; private set; }
    public bool IsSynonym { get; private set; }
    public SqpackIndexFile TargetFile { get; private set; } = null!;
    public ulong FileHash { get; private set; }
    public uint BlockOffset { get; private set; }
    public uint BlockNumber { get; private set; }

    protected override void ReadChunk()
    {
        using var advanceAfter = GetAdvanceOnDispose();
        IndexCommand = (IndexCommandKind)Reader.ReadByte();
        IsSynonym = Reader.ReadBoolean();
        Reader.ReadByte(); // Alignment

        TargetFile = new SqpackIndexFile(Reader);

        FileHash = Reader.ReadUInt64BE();

        BlockOffset = Reader.ReadUInt32BE();
        BlockNumber = Reader.ReadUInt32BE();
    }

    public override string ToString() =>
        $"{TypeName}:{CommandName}:{IndexCommand}:{IsSynonym}:{TargetFile}:{FileHash:X8}:{BlockOffset}:{BlockNumber}";
}
