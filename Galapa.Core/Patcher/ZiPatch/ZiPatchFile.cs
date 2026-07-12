using Galapa.Core.Patcher.ZiPatch.Chunk;
using Galapa.Core.Patcher.ZiPatch.Util;

namespace Galapa.Core.Patcher.ZiPatch;

/// <summary>
/// A ZiPatch container: the 12-byte magic <c>\x91ZIPATCH\x0D\x0A\x1A\x0A</c> followed
/// by a flat big-endian sequence of chunks terminated by an <c>EOF_</c> chunk.
/// Enumerate <see cref="GetChunks"/> and call <c>ApplyChunk</c> on each in order to
/// apply the patch.
/// </summary>
public sealed class ZiPatchFile : IDisposable
{
    // "\x91ZIPATCH\x0D\x0A\x1A\x0A" read as three big-endian uint32s.
    private static readonly uint[] ZipatchMagic = [0x915A4950, 0x41544348, 0x0D0A1A0A];

    private readonly Stream _stream;
    private readonly bool _needsChecksum;

    /// <summary>Wraps a stream positioned at the start of a ZiPatch and validates the magic.</summary>
    public ZiPatchFile(Stream stream, bool needsChecksum = false)
    {
        _stream = stream;
        _needsChecksum = needsChecksum;

        var reader = new BinaryReader(stream);

        if (ZipatchMagic.Any(magic => magic != reader.ReadUInt32BE()))
        {
            stream.Dispose();
            throw new ZiPatchException("Not a ZiPatch file (bad magic).");
        }
    }

    /// <summary>Opens a ZiPatch from a file path.</summary>
    public static ZiPatchFile FromFileName(string filepath, bool needsChecksum = false)
    {
        var stream = SqexFileStream.WaitForStream(filepath, FileMode.Open);
        return new ZiPatchFile(stream, needsChecksum);
    }

    /// <summary>Lazily reads chunks in file order, ending with (and including) the <c>EOF_</c> chunk.</summary>
    public IEnumerable<ZiPatchChunk> GetChunks()
    {
        ZiPatchChunk chunk;

        do
        {
            chunk = ZiPatchChunk.GetChunk(_stream, _needsChecksum);
            yield return chunk;
        }
        while (chunk.ChunkType != EndOfFileChunk.TypeName);
    }

    public void Dispose() => _stream.Dispose();
}
