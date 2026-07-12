using System.IO.Compression;

namespace Galapa.Core.Patcher.ZiPatch.Util;

/// <summary>
/// One block emitted by SqpkFile AddFile: a 0x10-byte little-endian header followed
/// by either a raw deflate stream or a verbatim ("stored") copy, padded up to the
/// next 0x80 boundary.
///
/// DQX divergence from FFXIV: a block is stored uncompressed when
/// <see cref="CompressedSize"/> equals <see cref="StoredBlockSentinel"/> = 0x1F400
/// (128000), twice the 64000-byte block size. FFXIV uses 0x7D00 (32000) for its
/// 16000-byte blocks. The 0x80-alignment math is otherwise identical.
/// </summary>
internal sealed class SqpkCompressedBlock
{
    /// <summary>The <see cref="CompressedSize"/> value that marks a stored (uncompressed) block in DQX patches.</summary>
    public const int StoredBlockSentinel = 0x1F400;

    public int HeaderSize { get; }
    public int CompressedSize { get; }
    public int DecompressedSize { get; }

    public bool IsCompressed => CompressedSize != StoredBlockSentinel;

    public int CompressedBlockLength =>
        (int)(((IsCompressed ? CompressedSize : DecompressedSize) + 143) & 0xFFFF_FF80);

    public byte[] CompressedBlock { get; }

    public SqpkCompressedBlock(BinaryReader reader)
    {
        // These four header fields are little-endian inside the big-endian container.
        HeaderSize = reader.ReadInt32();
        reader.ReadUInt32(); // Pad / reserved

        CompressedSize = reader.ReadInt32();
        DecompressedSize = reader.ReadInt32();

        if (IsCompressed)
        {
            CompressedBlock = reader.ReadBytesRequired(CompressedBlockLength - HeaderSize);
        }
        else
        {
            CompressedBlock = reader.ReadBytesRequired(DecompressedSize);
            reader.ReadBytesRequired(CompressedBlockLength - HeaderSize - DecompressedSize); // alignment padding
        }
    }

    public void DecompressInto(Stream outStream)
    {
        if (IsCompressed)
        {
            using var stream = new DeflateStream(new MemoryStream(CompressedBlock), CompressionMode.Decompress);
            // Copy exactly DecompressedSize bytes — no more, no less. A crafted block could otherwise
            // declare a small size but supply deflate data that inflates far beyond it (a decompression
            // bomb), growing the target file until the disk fills. A legitimate block inflates to
            // exactly this size, so this is transparent to real patches.
            CopyExactly(stream, outStream, DecompressedSize);
        }
        else
        {
            using var stream = new MemoryStream(CompressedBlock);
            stream.CopyTo(outStream);
        }
    }

    private static void CopyExactly(Stream source, Stream destination, int count)
    {
        var buffer = new byte[81920];
        var remaining = count;

        while (remaining > 0)
        {
            var read = source.Read(buffer, 0, Math.Min(buffer.Length, remaining));
            if (read == 0)
                throw new ZiPatchException("compressed block inflated to fewer bytes than declared.");

            destination.Write(buffer, 0, read);
            remaining -= read;
        }

        // Anything beyond DecompressedSize means the block lied about its size — reject it.
        if (source.ReadByte() != -1)
            throw new ZiPatchException("compressed block inflated beyond its declared size.");
    }
}
