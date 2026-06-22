using Galapa.Core.Patcher.ZiPatch.Util;

namespace Galapa.Core.Patcher.ZiPatch.Chunk.SqpkCommand;

/// <summary>SQPK 'A' — write raw block data into a .dat, optionally wiping trailing blocks.</summary>
internal sealed class SqpkAddData(BinaryReader reader, long offset, long size)
    : SqpkChunk(reader, offset, size)
{
    public const string CommandName = "A";

    public SqpackDatFile TargetFile { get; private set; } = null!;
    public long BlockOffset { get; private set; }
    public long BlockNumber { get; private set; }
    public long BlockDeleteNumber { get; private set; }

    public byte[] BlockData { get; private set; } = [];
    public long BlockDataSourceOffset { get; private set; }

    protected override void ReadChunk()
    {
        using var advanceAfter = GetAdvanceOnDispose();
        Reader.ReadBytes(3); // Alignment

        TargetFile = new SqpackDatFile(Reader);

        // block* fields are in 0x80 units.
        BlockOffset = (long)Reader.ReadUInt32BE() << 7;
        BlockNumber = (long)Reader.ReadUInt32BE() << 7;
        BlockDeleteNumber = (long)Reader.ReadUInt32BE() << 7;

        BlockDataSourceOffset = Offset + Reader.BaseStream.Position;
        BlockData = Reader.ReadBytes(checked((int)BlockNumber));
    }

    public override void ApplyChunk(ZiPatchConfig config)
    {
        TargetFile.ResolvePath(config.Platform);

        // AddData on an absent .dat is skipped, not fatal — DQXUpdater neither creates the file
        // nor aborts the patch (unlike DeleteData/ExpandData, which abort). It does extend an
        // existing .dat up to the write, so we don't clamp.
        if (!TargetFile.Exists(config.GamePath))
            return;

        var file = config.Store == null
            ? TargetFile.OpenStream(config.GamePath, FileMode.Open)
            : TargetFile.OpenStream(config.Store, config.GamePath, FileMode.Open);

        file.WriteFromOffset(BlockData, BlockOffset);
        file.Wipe(BlockDeleteNumber);
    }

    public override string ToString() =>
        $"{TypeName}:{CommandName}:{TargetFile}:{BlockOffset}:{BlockNumber}:{BlockDeleteNumber}";
}
