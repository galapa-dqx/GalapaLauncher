namespace Galapa.Core.Patcher.ZiPatch.Util;

/// <summary>
/// Scopes the reading of a single chunk body: on dispose it advances the underlying
/// stream to exactly the end of the chunk, regardless of how many bytes the chunk's
/// reader actually consumed. When checksums are enabled the remaining bytes are read
/// through (rather than seeked over) so they still contribute to the running CRC.
/// </summary>
public sealed class AdvanceOnDispose : IDisposable
{
    private readonly BinaryReader _reader;
    private readonly bool _forceRead;
    public readonly long OffsetBefore;
    public readonly long OffsetAfter;

    public AdvanceOnDispose(BinaryReader reader, long size, bool forceRead)
    {
        _reader = reader;
        _forceRead = forceRead;
        OffsetBefore = _reader.BaseStream.Position;
        OffsetAfter = OffsetBefore + size;
    }

    public long NumBytesRemaining => OffsetAfter - _reader.BaseStream.Position;

    public void Dispose()
    {
        if (_forceRead)
        {
            _ = _reader.ReadBytes((int)NumBytesRemaining);
            return;
        }

        _reader.BaseStream.Position = OffsetAfter;
    }
}
