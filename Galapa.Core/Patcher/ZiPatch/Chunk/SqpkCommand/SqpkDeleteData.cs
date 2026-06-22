using Galapa.Core.Patcher.ZiPatch.Util;

namespace Galapa.Core.Patcher.ZiPatch.Chunk.SqpkCommand;

/// <summary>SQPK 'D' — stamp an empty-block record over blocks in a .dat.</summary>
internal sealed class SqpkDeleteData(BinaryReader reader, long offset, long size)
    : SqpkChunk(reader, offset, size)
{
    public const string CommandName = "D";

    public SqpackDatFile TargetFile { get; private set; } = null!;
    public long BlockOffset { get; private set; }
    public long BlockNumber { get; private set; }

    protected override void ReadChunk()
    {
        using var advanceAfter = GetAdvanceOnDispose();
        Reader.ReadBytes(3); // Alignment

        TargetFile = new SqpackDatFile(Reader);

        BlockOffset = (long)Reader.ReadUInt32BE() << 7;
        BlockNumber = Reader.ReadUInt32BE();

        Reader.ReadUInt32(); // Reserved (little-endian)
    }

    public override void ApplyChunk(ZiPatchConfig config)
    {
        TargetFile.ResolvePath(config.Platform);

        // A missing dat0 (whole span absent) fails to resolve and ABORTS the patch (e.g. the
        // data00130000 DeleteData in 1.6.97629.3->1.6.101161.1, which leaves data00150000 and
        // every later chunk unapplied). A missing dat{N>0} is created as a span extension.
        if (!TargetFile.Exists(config.GamePath) && TargetFile.FileId == 0)
            throw new ZiPatchApplyAbortedException(TargetFile.RelativePath);

        var file = config.Store == null
            ? TargetFile.OpenStream(config.GamePath, FileMode.OpenOrCreate)
            : TargetFile.OpenStream(config.Store, config.GamePath, FileMode.OpenOrCreate);

        SqpackDatFile.WriteEmptyFileBlockAt(file, BlockOffset, BlockNumber);
    }

    public override string ToString() => $"{TypeName}:{CommandName}:{TargetFile}:{BlockOffset}:{BlockNumber}";
}
