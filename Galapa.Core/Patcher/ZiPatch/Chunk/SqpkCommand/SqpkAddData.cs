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
        BlockData = Reader.ReadBytesRequired(checked((int)BlockNumber));
    }

    public override void ApplyChunk(ZiPatchConfig config)
    {
        TargetFile.ResolvePath(config.Platform);

        // A data command on a missing .dat: DQXUpdater's ResolveTargetFile only creates a NEW .dat
        // when it extends an existing span (fileId > 0 AND dat{fileId-1} already exists). It ABORTS
        // the patch otherwise — whether the missing .dat is the span base dat0 or an extension whose
        // base is absent (e.g. data00130000.dat1 with no data00130000.dat0 in 7.0.303921.4->
        // 7.0.306094.1). The abort stops the remaining chunks, so the later 'H' block never runs —
        // which is why that patch leaves every .dat header untouched. (When the .dat does exist we
        // extend it up to the write, so we don't clamp.)
        if (!TargetFile.Exists(config.GamePath) &&
            (TargetFile.FileId == 0 || TargetFile.PriorSpanFileMissing(config.GamePath, config.Platform)))
            throw new ZiPatchApplyAbortedException(TargetFile.RelativePath);

        var file = config.Store == null
            ? TargetFile.OpenStream(config.GamePath, FileMode.OpenOrCreate)
            : TargetFile.OpenStream(config.Store, config.GamePath, FileMode.OpenOrCreate);

        file.WriteFromOffset(BlockData, BlockOffset);
        file.Wipe(BlockDeleteNumber);
    }

    public override string ToString() =>
        $"{TypeName}:{CommandName}:{TargetFile}:{BlockOffset}:{BlockNumber}:{BlockDeleteNumber}";
}
