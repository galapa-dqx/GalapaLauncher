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

        // A data command on a missing .dat: DQXUpdater's ResolveTargetFile only creates a NEW
        // .dat when it extends an existing span (fileId > 0, i.e. dat0..dat{N-1} already exist).
        // A missing dat0 (a whole span that doesn't exist) fails to resolve and ABORTS the patch
        // — this is why the transient data00130000/data00150000 are never created. (It extends
        // an existing .dat up to the write, so we don't clamp.)
        if (!TargetFile.Exists(config.GamePath) && TargetFile.FileId == 0)
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
