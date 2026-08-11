using System.Text;

namespace Galapa.Core.Patcher.ZiPatch.Util;

/// <summary>
/// Big-endian read helpers. The ZiPatch container is big-endian, so almost every
/// integer in the format is read through one of these. (A handful of fields inside
/// SQPK commands are little-endian; those use the stock <see cref="BinaryReader"/>
/// little-endian methods instead.)
/// </summary>
internal static class BinaryReaderExtensions
{
    public static byte[] ReadBytesRequired(this BinaryReader reader, int byteCount)
    {
        var result = reader.ReadBytes(byteCount);
        if (result.Length != byteCount)
            throw new EndOfStreamException($"{byteCount} bytes required from stream, but only {result.Length} returned.");
        return result;
    }

    public static string ReadFixedLengthString(this BinaryReader reader, uint length) =>
        Encoding.ASCII.GetString(reader.ReadBytesRequired((int)length)).TrimEnd('\0');

    public static ushort ReadUInt16BE(this BinaryReader reader)
    {
        var b = reader.ReadBytesRequired(sizeof(ushort));
        return (ushort)((b[0] << 8) | b[1]);
    }

    public static short ReadInt16BE(this BinaryReader reader) => (short)reader.ReadUInt16BE();

    public static uint ReadUInt32BE(this BinaryReader reader)
    {
        var b = reader.ReadBytesRequired(sizeof(uint));
        return ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];
    }

    public static int ReadInt32BE(this BinaryReader reader) => (int)reader.ReadUInt32BE();

    public static ulong ReadUInt64BE(this BinaryReader reader)
    {
        var hi = reader.ReadUInt32BE();
        var lo = reader.ReadUInt32BE();
        return ((ulong)hi << 32) | lo;
    }

    public static long ReadInt64BE(this BinaryReader reader) => (long)reader.ReadUInt64BE();
}
