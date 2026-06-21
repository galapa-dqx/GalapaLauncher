namespace Galapa.Core.Patcher.ZiPatch.Util;

/// <summary>
/// Keeps one open <see cref="SqexFileStream"/> per target path for the duration of a
/// patch apply, so repeated SQPK commands against the same .dat/.idx don't pay the
/// open/close cost (and so writes are flushed once, at the end).
/// </summary>
public sealed class SqexFileStreamStore : IDisposable
{
    private readonly Dictionary<string, SqexFileStream> _streams = new();

    public SqexFileStream GetStream(string path, FileMode mode, int tries, int sleeptime)
    {
        path = System.IO.Path.GetFullPath(path);
        var key = path.ToLowerInvariant();

        if (_streams.TryGetValue(key, out var stream))
            return stream;

        stream = SqexFileStream.WaitForStream(path, mode, tries, sleeptime);
        _streams.Add(key, stream);

        return stream;
    }

    public void CloseStream(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        var key = path.ToLowerInvariant();

        if (_streams.Remove(key, out var stream))
            stream.Dispose();
    }

    public void Dispose()
    {
        foreach (var stream in _streams.Values)
        {
            stream.Flush(true);
            stream.Dispose();
        }

        _streams.Clear();
    }
}
