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

        // FileBlockHeader - the 0 writes are technically unnecessary but are in for illustrative purposes
        // Block size
        file.Write(1 << 7);
        // ????
        file.Write(0);
        // File size
        file.Write(0);
        // Total number of blocks?
        file.Write(blockNumber - 1);
        // Used number of blocks?
        file.Write(0);
    }
}
