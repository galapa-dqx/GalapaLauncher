using System.Text;

namespace Galapa.Core.Patcher.ZiPatch.Util;

/// <summary>A SqPack data file: <c>data{mainId:D4}{subId:D4}.{platform}.dat{fileId}</c>.</summary>
public sealed class SqpackDatFile(BinaryReader reader) : SqpackFile(reader)
{
    protected override string GetFileName(ZiPatchConfig.PlatformId platform) =>
        $"{base.GetFileName(platform)}.dat{FileId}";

    /// <summary>
    /// Stamps an empty-file-block record over <paramref name="blockNumber"/> blocks
    /// starting at <paramref name="offset"/>, used by DeleteData/ExpandData to punch
    /// a hole in a .dat without shifting the rest of the file.
    /// </summary>
    public static void WriteEmptyFileBlockAt(SqexFileStream stream, long offset, long blockNumber)
    {
        stream.WipeFromOffset(blockNumber << 7, offset);
        stream.Position = offset;

        using var file = new BinaryWriter(stream, Encoding.Default, true);

        // FileBlockHeader: DQXUpdater writes exactly five u32s = [0x80, 0, 0, blockNumber-1, 0]
        // (the 20-byte record described by ZiPatch_SqpkDeleteData/ExpandData_WriteEmptyBlocks).
        // NOTE: blockNumber-1 must be a 4-byte int — XIVLauncher writes it as a long, which
        // would make the record 24 bytes and diverge from the DQX updater.
        file.Write(1 << 7);                  // block size (0x80)
        file.Write(0);
        file.Write(0);                       // file size
        file.Write((int)(blockNumber - 1));  // number of additional empty blocks
        file.Write(0);
    }
}
