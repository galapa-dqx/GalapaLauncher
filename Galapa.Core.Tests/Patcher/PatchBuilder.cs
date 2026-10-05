using System.IO.Compression;
using System.Text;
using Galapa.Core.Patcher.ZiPatch;

namespace Galapa.Core.Tests.Patcher;

/// <summary>
/// Authors synthetic ZiPatch byte streams for tests: magic, then chunks framed as
/// <c>u32 size | name[4] | payload | u32 crc32</c> (big-endian, except the few fields
/// the format itself stores little-endian). CRCs are real (zlib variant), so a patch
/// built here parses cleanly with checksum validation enabled.
/// </summary>
internal sealed class PatchBuilder
{
    private static readonly byte[] Magic =
        [0x91, (byte)'Z', (byte)'I', (byte)'P', (byte)'A', (byte)'T', (byte)'C', (byte)'H', 0x0D, 0x0A, 0x1A, 0x0A];

    private readonly List<byte> _bytes = [.. Magic];

    /// <summary>Appends a raw chunk with the given fourcc and payload.</summary>
    public PatchBuilder Chunk(string name, ReadOnlySpan<byte> payload)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name);
        var crcInput = new byte[nameBytes.Length + payload.Length];
        nameBytes.CopyTo(crcInput, 0);
        payload.CopyTo(crcInput.AsSpan(nameBytes.Length));

        _bytes.AddRange(Be((uint)payload.Length));
        _bytes.AddRange(nameBytes);
        _bytes.AddRange(payload.ToArray());
        _bytes.AddRange(Be(Crc32(crcInput)));
        return this;
    }

    /// <summary>Appends a SQPK chunk: <c>u32 innerSize | opcode | body</c> (innerSize == chunk size).</summary>
    public PatchBuilder Sqpk(char opcode, ReadOnlySpan<byte> body)
    {
        var payload = new List<byte>();
        payload.AddRange(Be((uint)(5 + body.Length))); // innerSize
        payload.Add((byte)opcode);
        payload.AddRange(body.ToArray());
        return Chunk("SQPK", payload.ToArray());
    }

    public PatchBuilder FileHeaderV2(string patchType = "DIFF", uint entryFiles = 0)
    {
        var p = new List<byte>();
        p.AddRange([0, 0, 2, 0]); // version 2 == (LE u32 0x00020000 >> 16)
        p.AddRange(Encoding.ASCII.GetBytes(patchType));
        p.AddRange(Be(entryFiles));
        return Chunk("FHDR", p.ToArray());
    }

    public PatchBuilder TargetInfo(ZiPatchConfig.PlatformId platform = ZiPatchConfig.PlatformId.Win32)
    {
        var p = new List<byte>();
        p.AddRange(new byte[3]);          // reserved
        p.AddRange(Be((ushort)platform)); // platform (BE)
        p.AddRange(Be((short)-1));        // region = Global (BE)
        p.AddRange(Be((short)0));         // isDebug
        p.AddRange(Be((ushort)0));        // version
        p.AddRange(new byte[8]);          // deletedDataSize (LE)
        p.AddRange(new byte[8]);          // seekCount (LE)
        return Sqpk('T', p.ToArray());
    }

    public PatchBuilder AddFile(string path, IEnumerable<byte[]> blocks, long fileOffset = 0, long fileSize = 0, ushort expansionId = 0)
    {
        var pathBytes = Encoding.ASCII.GetBytes(path);
        var body = new List<byte> { (byte)'A', 0, 0 };
        body.AddRange(Be(fileOffset));
        body.AddRange(Be(fileSize));
        body.AddRange(Be((uint)pathBytes.Length));
        body.AddRange(Be(expansionId));
        body.AddRange([0, 0]);
        body.AddRange(pathBytes);
        foreach (var b in blocks)
            body.AddRange(b);
        return Sqpk('F', body.ToArray());
    }

    public PatchBuilder DeleteFile(string path, ushort expansionId = 0)
    {
        var pathBytes = Encoding.ASCII.GetBytes(path);
        var body = new List<byte> { (byte)'D', 0, 0 };
        body.AddRange(Be(0L)); // fileOffset
        body.AddRange(Be(0L)); // fileSize
        body.AddRange(Be((uint)pathBytes.Length));
        body.AddRange(Be(expansionId));
        body.AddRange([0, 0]);
        body.AddRange(pathBytes);
        return Sqpk('F', body.ToArray());
    }

    /// <summary>A SQPK 'D' (DeleteData) command against a SqPack .dat, by (main, sub, file) triple.</summary>
    public PatchBuilder DeleteData(ushort mainId, ushort subId, uint fileId, uint blockOffset, uint blockNumber)
    {
        var body = new List<byte> { 0, 0, 0 }; // alignment
        body.AddRange(Be(mainId));
        body.AddRange(Be(subId));
        body.AddRange(Be(fileId));
        body.AddRange(Be(blockOffset));
        body.AddRange(Be(blockNumber));
        body.AddRange(new byte[4]); // reserved
        return Sqpk('D', body.ToArray());
    }

    public PatchBuilder EndOfFile() => Chunk("EOF_", ReadOnlySpan<byte>.Empty);

    public byte[] Build() => _bytes.ToArray();

    public MemoryStream ToStream() => new(Build(), writable: false);

    // --- block builders -----------------------------------------------------

    /// <summary>A "stored" (uncompressed) AddFile block: compressedSize == DQX sentinel 0x1F400.</summary>
    public static byte[] StoredBlock(byte[] data)
    {
        var total = Align(data.Length);
        var b = new List<byte>();
        b.AddRange(Le(0x10));                 // headerSize
        b.AddRange(Le(0u));                   // reserved
        b.AddRange(Le(0x1F400));              // compressedSize == stored sentinel
        b.AddRange(Le((uint)data.Length));    // decompressedSize
        b.AddRange(data);
        b.AddRange(new byte[total - 16 - data.Length]);
        return b.ToArray();
    }

    /// <summary>A deflate-compressed AddFile block.</summary>
    public static byte[] DeflateBlock(byte[] data)
    {
        var deflated = Deflate(data);
        var total = Align(deflated.Length);
        var b = new List<byte>();
        b.AddRange(Le(0x10));                 // headerSize
        b.AddRange(Le(0u));                   // reserved
        b.AddRange(Le((uint)deflated.Length));// compressedSize
        b.AddRange(Le((uint)data.Length));    // decompressedSize
        b.AddRange(deflated);
        b.AddRange(new byte[total - 16 - deflated.Length]);
        return b.ToArray();
    }

    /// <summary>
    /// A deflate block that lies about its decompressed size — the data inflates to
    /// <paramref name="data"/>.Length but the header declares <paramref name="declaredDecompressedSize"/>.
    /// Used to exercise the decompression-bomb guard.
    /// </summary>
    public static byte[] OversizedDeflateBlock(byte[] data, uint declaredDecompressedSize)
    {
        var deflated = Deflate(data);
        var total = Align(deflated.Length);
        var b = new List<byte>();
        b.AddRange(Le(0x10));                     // headerSize
        b.AddRange(Le(0u));                       // reserved
        b.AddRange(Le((uint)deflated.Length));    // compressedSize
        b.AddRange(Le(declaredDecompressedSize)); // decompressedSize (deliberately wrong)
        b.AddRange(deflated);
        b.AddRange(new byte[total - 16 - deflated.Length]);
        return b.ToArray();
    }

    private static int Align(int size) => (size + 143) & ~0x7F;

    private static byte[] Deflate(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var ds = new DeflateStream(ms, CompressionLevel.Optimal, true))
            ds.Write(data, 0, data.Length);
        return ms.ToArray();
    }

    // --- endian helpers -----------------------------------------------------

    private static byte[] Be(uint v) => [(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v];
    private static byte[] Be(int v) => Be((uint)v);
    private static byte[] Be(ushort v) => [(byte)(v >> 8), (byte)v];
    private static byte[] Be(short v) => Be((ushort)v);

    private static byte[] Be(long v)
    {
        var b = new byte[8];
        for (var i = 0; i < 8; i++)
            b[i] = (byte)(v >> (56 - i * 8));
        return b;
    }

    private static byte[] Le(uint v) => [(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)];
    private static byte[] Le(int v) => Le((uint)v);

    // --- zlib CRC-32 (matches ZiPatchCrc32) ---------------------------------

    private static readonly uint[] CrcTable = BuildCrcTable();

    private static uint Crc32(byte[] data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
            crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return ~crc;
    }

    private static uint[] BuildCrcTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            var k = i;
            for (var j = 0; j < 8; j++)
                k = (k & 1) != 0 ? (k >> 1) ^ 0xEDB88320 : k >> 1;
            table[i] = k;
        }

        return table;
    }
}
