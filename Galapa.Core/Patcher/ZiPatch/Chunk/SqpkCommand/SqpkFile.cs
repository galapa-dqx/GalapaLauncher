using Galapa.Core.Patcher.ZiPatch.Util;

namespace Galapa.Core.Patcher.ZiPatch.Chunk.SqpkCommand;

/// <summary>
/// SQPK 'F' — a file-level operation against a verbatim relative path: add a file
/// from the deflate blocks that follow ('A'), remove all of an expansion's files
/// ('R'), delete a single file ('D'), or make a directory tree ('M').
///
/// The DQX boot patch uses 'A' and 'D' heavily (including 'D', which FFXIV notes it
/// never saw in the wild). Paths are carried byte-for-byte — DQX's scrambled
/// filenames (e.g. <c>Bin/BurakqOnn!pcs--!qca</c>) are written exactly as stored.
/// </summary>
internal sealed class SqpkFile(BinaryReader reader, long offset, long size)
    : SqpkChunk(reader, offset, size)
{
    public const string CommandName = "F";

    public enum OperationKind : byte
    {
        AddFile = (byte)'A',
        RemoveAll = (byte)'R',
        DeleteFile = (byte)'D',
        MakeDirTree = (byte)'M',
    }

    public OperationKind Operation { get; private set; }
    public long FileOffset { get; private set; }
    public long FileSize { get; private set; }
    public ushort ExpansionId { get; private set; }
    public SqexFile TargetFile { get; private set; } = null!;

    public List<long> CompressedDataSourceOffsets { get; private set; } = [];
    public List<SqpkCompressedBlock> CompressedData { get; private set; } = [];

    protected override void ReadChunk()
    {
        using var advanceAfter = GetAdvanceOnDispose();
        Operation = (OperationKind)Reader.ReadByte();
        Reader.ReadBytes(2); // Alignment

        FileOffset = Reader.ReadInt64BE();
        FileSize = Reader.ReadInt64BE();

        var pathLen = Reader.ReadUInt32BE();

        ExpansionId = Reader.ReadUInt16BE();
        Reader.ReadBytes(2);

        TargetFile = new SqexFile(Reader.ReadFixedLengthString(pathLen));

        if (Operation == OperationKind.AddFile)
        {
            CompressedDataSourceOffsets = [];
            CompressedData = [];

            while (advanceAfter.NumBytesRemaining > 0)
            {
                CompressedDataSourceOffsets.Add(Offset + Reader.BaseStream.Position);
                var block = new SqpkCompressedBlock(Reader);
                CompressedData.Add(block);
                CompressedDataSourceOffsets[^1] += block.HeaderSize;
            }
        }
    }

    // Mirrors FFXIV: RemoveAll spares the .var and intro movie files. Unverified for
    // DQX (no 'F'/'R' command appears in the available DQX patches).
    private static bool RemoveAllFilter(string filePath) =>
        !new[] { ".var", "00000.bk2", "00001.bk2", "00002.bk2", "00003.bk2" }.Any(filePath.EndsWith);

    public override void ApplyChunk(ZiPatchConfig config)
    {
        switch (Operation)
        {
            // DQXUpdater treats any op that isn't R/D/M as AddFile (verified in
            // ZiPatchApply::onSqpkFile -> sub_4281b0: it early-returns for 'D'/'R', skips the
            // block write for 'M', and otherwise falls into AddFile), so default shares this arm.
            case OperationKind.AddFile:
            default:
                TargetFile.CreateDirectoryTree(config.GamePath);

                var fileStream = config.Store == null
                    ? TargetFile.OpenStream(config.GamePath, FileMode.OpenOrCreate)
                    : TargetFile.OpenStream(config.Store, config.GamePath, FileMode.OpenOrCreate);

                if (FileOffset == 0)
                    fileStream.SetLength(0);

                fileStream.Seek(FileOffset, SeekOrigin.Begin);
                foreach (var block in CompressedData)
                    block.DecompressInto(fileStream);

                break;

            case OperationKind.RemoveAll:
                // GetAllExpansionFiles yields absolute paths, but SqexFile.Delete re-roots on
                // GamePath — so pass each path relative to GamePath, not the absolute one.
                foreach (var file in SqexFile.GetAllExpansionFiles(config.GamePath, ExpansionId).Where(RemoveAllFilter))
                    new SqexFile(System.IO.Path.GetRelativePath(config.GamePath, file)).Delete(config.Store, config.GamePath);

                break;

            case OperationKind.DeleteFile:
                TargetFile.Delete(config.Store, config.GamePath);
                break;

            case OperationKind.MakeDirTree:
                Directory.CreateDirectory(SqexFile.ResolveUnderBase(config.GamePath, TargetFile.RelativePath));
                break;
        }
    }

    public override string ToString() =>
        $"{TypeName}:{CommandName}:{Operation}:{FileOffset}:{FileSize}:{ExpansionId}:{TargetFile}";
}
