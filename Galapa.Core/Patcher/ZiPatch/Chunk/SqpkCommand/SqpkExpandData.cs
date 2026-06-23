using Galapa.Core.Patcher.ZiPatch.Util;

namespace Galapa.Core.Patcher.ZiPatch.Chunk.SqpkCommand;

/// <summary>
/// SQPK 'E' — like DeleteData, but tolerates writing past EOF (it grows the .dat).
/// Same wire layout as DeleteData.
/// </summary>
internal sealed class SqpkExpandData(BinaryReader reader, long offset, long size)
    : SqpkChunk(reader, offset, size)
{
    public const string CommandName = "E";

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

        // A missing .dat that can't resolve as a span extension ABORTS the patch — the span base
        // dat0, or an extension whose base is absent (see SqpkDeleteData / SqpkAddData). A missing
        // dat{N>0} whose base exists is created as a span extension.
        if (!TargetFile.Exists(config.GamePath) &&
            (TargetFile.FileId == 0 || TargetFile.PriorSpanFileMissing(config.GamePath, config.Platform)))
            throw new ZiPatchApplyAbortedException(TargetFile.RelativePath);

        var file = config.Store == null
            ? TargetFile.OpenStream(config.GamePath, FileMode.OpenOrCreate)
            : TargetFile.OpenStream(config.Store, config.GamePath, FileMode.OpenOrCreate);

        SqpackDatFile.WriteEmptyFileBlockAt(file, BlockOffset, BlockNumber);
    }

    public override string ToString() => $"{TypeName}:{CommandName}:{BlockOffset}:{BlockNumber}";
}
