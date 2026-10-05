namespace Galapa.Core.Patcher.ZiPatch.Util;

/// <summary>
/// The reflected (zlib/PNG) CRC-32 used for ZiPatch chunk checksums.
///
/// NOTE: this is a different algorithm from <see cref="Galapa.Core.Utils.Crc32"/>,
/// which is the MSB-first variant (polynomial 0x04C11DB7) used by DQX's filename
/// obfuscator. ZiPatch's per-chunk checksum is the reflected variant with polynomial
/// 0xEDB88320 and the usual 0xFFFFFFFF init / final-xor, so the two are not
/// interchangeable.
/// </summary>
internal sealed class ZiPatchCrc32
{
    private const uint Polynomial = 0xEDB88320;

    private static readonly uint[] Table = BuildTable();

    private uint _crc = 0xFFFFFFFF;

    public uint Checksum => ~_crc;

    public void Init() => _crc = 0xFFFFFFFF;

    public void Update(byte b) => _crc = Table[(_crc ^ b) & 0xFF] ^ (_crc >> 8);

    public void Update(byte[] data)
    {
        foreach (var b in data)
            Update(b);
    }

    public static uint Calculate(byte[] data, int offset, int length)
    {
        var crc = 0xFFFFFFFFu;
        for (var i = offset; i < offset + length; i++)
            crc = Table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var k = i;
            for (var j = 0; j < 8; j++)
                k = (k & 1) != 0 ? (k >> 1) ^ Polynomial : k >> 1;
            table[i] = k;
        }

        return table;
    }
}
