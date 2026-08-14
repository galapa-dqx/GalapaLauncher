using System.Text;

namespace Talon.Vfs;

// Computes the case-insensitive CRC pair stored in DQX archive indexes and
// dat_db.db. The high dword identifies the directory and the low dword the file.
internal static class DqxPathHash
{
    private static readonly uint[] Table = BuildTable();

    public static DqxVirtualPath Describe(string path) => DescribeNormalized(Normalize(path));

    public static string Normalize(string path) => path.Replace('\\', '/');

    public static DqxVirtualPath DescribeNormalized(string normalized)
    {
        var slash = normalized.LastIndexOf('/');
        var directory = slash < 0 ? string.Empty : normalized[..slash];
        var file = slash < 0 ? normalized : normalized[(slash + 1)..];
        var fileHash = ComputeComponent(file);
        var directoryHash = slash < 0 ? fileHash : ComputeComponent(directory);
        return new DqxVirtualPath(
            normalized,
            directory,
            file,
            directoryHash.ToString("x8"),
            fileHash.ToString("x8"));
    }

    public static ulong Compute(string path)
    {
        var description = Describe(path);
        return ((ulong)Convert.ToUInt32(description.DirectoryHash, 16) << 32) |
               Convert.ToUInt32(description.FileHash, 16);
    }

    private static uint ComputeComponent(string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        uint crc = 0xFFFFFFFF;
        foreach (var raw in bytes)
        {
            var folded = raw is >= (byte)'A' and <= (byte)'Z'
                ? (byte)(raw + 0x20)
                : raw;
            crc = (crc >> 8) ^ Table[(crc ^ folded) & 0xFF];
        }
        return crc;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint index = 0; index < table.Length; index++)
        {
            var crc = index;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            table[index] = crc;
        }
        return table;
    }
}

internal readonly record struct DqxVirtualPath(
    string Path,
    string Directory,
    string File,
    string DirectoryHash,
    string FileHash);
