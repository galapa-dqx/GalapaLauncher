using Galapa.Core.Patcher.ZiPatch.Chunk.SqpkCommand;
using Galapa.Core.Patcher.ZiPatch.Util;

namespace Galapa.Core.Patcher.ZiPatch.Chunk;

/// <summary>
/// Base class for the chunks that make up a ZiPatch. The on-disk framing is
/// <c>u32 size | char name[4] | payload[size] | u32 crc32</c> (size counts the
/// payload only). <see cref="GetChunk"/> reads one frame, dispatches on the fourcc,
/// lets the concrete type parse its payload, then reads the trailing checksum.
/// </summary>
public abstract class ZiPatchChunk
{
    /// <summary>The 4-character chunk type of this instance (e.g. "FHDR", "SQPK").</summary>
    public abstract string ChunkType { get; }

    public long Offset { get; }
    public long Size { get; }
    public uint Checksum { get; private set; }

    /// <summary>
    /// CRC computed over the chunk's bytes. Only meaningful when the patch was opened
    /// with checksum validation enabled; otherwise 0.
    /// </summary>
    public uint CalculatedChecksum { get; private set; }

    public bool IsChecksumValid => CalculatedChecksum == Checksum;

    protected readonly BinaryReader Reader;
    private bool _needsChecksum;

    // Reused per-async-context scratch buffer: each chunk frame is copied here so the
    // concrete reader (and optional CRC) works against memory rather than the file.
    private static readonly AsyncLocal<MemoryStream> LocalMemoryStream = new();

    private static readonly Dictionary<string, Func<BinaryReader, long, long, ZiPatchChunk>> ChunkTypes = new()
    {
        { FileHeaderChunk.TypeName, (r, o, s) => new FileHeaderChunk(r, o, s) },
        { ApplyOptionChunk.TypeName, (r, o, s) => new ApplyOptionChunk(r, o, s) },
        { ApplyFreeSpaceChunk.TypeName, (r, o, s) => new ApplyFreeSpaceChunk(r, o, s) },
        { AddDirectoryChunk.TypeName, (r, o, s) => new AddDirectoryChunk(r, o, s) },
        { DeleteDirectoryChunk.TypeName, (r, o, s) => new DeleteDirectoryChunk(r, o, s) },
        { SqpkChunk.TypeName, SqpkChunk.GetCommand },
        { EndOfFileChunk.TypeName, (r, o, s) => new EndOfFileChunk(r, o, s) },
    };

    public static ZiPatchChunk GetChunk(Stream stream, bool needsChecksum)
    {
        var memoryStream = LocalMemoryStream.Value ??= new MemoryStream();

        try
        {
            var reader = new BinaryReader(stream);
            var size = checked((int)reader.ReadUInt32BE());
            var baseOffset = stream.Position;

            // payload + name (4) + crc (4)
            var readSize = size + 4 + 4;

            var maxLen = Math.Max(readSize, memoryStream.Capacity);
            if (memoryStream.Length < maxLen)
                memoryStream.SetLength(maxLen);

            // Copy the whole frame into the scratch buffer in one read.
            ReadExactly(reader.BaseStream, memoryStream.GetBuffer(), readSize);

            BinaryReader binaryReader;
            if (needsChecksum)
            {
                var checksumReader = new ChecksumBinaryReader(memoryStream);
                checksumReader.InitCrc32();
                binaryReader = checksumReader;
            }
            else
            {
                binaryReader = new BinaryReader(memoryStream);
            }

            var type = binaryReader.ReadFixedLengthString(4u);
            if (!ChunkTypes.TryGetValue(type, out var constructor))
                throw new ZiPatchException($"Unknown chunk type '{type}'.");

            var chunk = constructor(binaryReader, baseOffset, size);

            chunk._needsChecksum = needsChecksum;
            chunk.ReadChunk();
            chunk.ReadChecksum();
            return chunk;
        }
        catch (EndOfStreamException e)
        {
            throw new ZiPatchException("Could not get chunk", e);
        }
        finally
        {
            memoryStream.Position = 0;
        }
    }

    protected ZiPatchChunk(BinaryReader reader, long offset, long size)
    {
        Reader = reader;
        Offset = offset;
        Size = size;
    }

    /// <summary>Parses this chunk's payload. The default reads nothing (body is skipped on dispose).</summary>
    protected virtual void ReadChunk()
    {
        using var advanceAfter = GetAdvanceOnDispose();
    }

    /// <summary>Applies this chunk's effect to the game install described by <paramref name="config"/>.</summary>
    public virtual void ApplyChunk(ZiPatchConfig config) { }

    protected AdvanceOnDispose GetAdvanceOnDispose() => new(Reader, Size, _needsChecksum);

    private void ReadChecksum()
    {
        if (_needsChecksum && Reader is ChecksumBinaryReader checksumReader)
            CalculatedChecksum = checksumReader.GetCrc32();
        Checksum = Reader.ReadUInt32BE();
    }

    private static void ReadExactly(Stream stream, byte[] buffer, int count)
    {
        var read = 0;
        while (read < count)
        {
            var n = stream.Read(buffer, read, count - read);
            if (n == 0)
                throw new EndOfStreamException("Premature end of patch stream.");
            read += n;
        }
    }
}
