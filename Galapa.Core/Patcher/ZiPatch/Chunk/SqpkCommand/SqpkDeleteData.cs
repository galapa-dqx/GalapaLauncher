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

        // Like AddData, DQXUpdater never creates a missing .dat for a DeleteData op.
        if (!TargetFile.Exists(config.GamePath))
            return;

        var file = config.Store == null
            ? TargetFile.OpenStream(config.GamePath, FileMode.Open)
            : TargetFile.OpenStream(config.Store, config.GamePath, FileMode.Open);

        SqpackDatFile.WriteEmptyFileBlockAt(file, BlockOffset, BlockNumber);
    }

    public override string ToString() => $"{TypeName}:{CommandName}:{TargetFile}:{BlockOffset}:{BlockNumber}";
}
