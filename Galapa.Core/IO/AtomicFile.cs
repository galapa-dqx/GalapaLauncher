using System.Text;

namespace Galapa.Core.IO;

/// <summary>
///     Writes files by staging them in a temporary sibling and moving that over the destination, so readers only ever
///     see a complete document. Callers are responsible for serializing concurrent writes to the same path.
/// </summary>
public static class AtomicFile
{
    /// <summary>
    ///     Atomically replaces <paramref name="path" /> with <paramref name="contents" />, encoded as UTF-8 without a BOM.
    /// </summary>
    public static Task WriteAllTextAsync(string path, string contents, CancellationToken cancellationToken = default) =>
        WriteAsync(path, async (stream, token) =>
        {
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true);
            await writer.WriteAsync(contents.AsMemory(), token).ConfigureAwait(false);
            await writer.FlushAsync(token).ConfigureAwait(false);
        }, cancellationToken);

    /// <summary>
    ///     Atomically replaces <paramref name="path" /> with whatever <paramref name="write" /> puts into the stream it is
    ///     given. The stream may be wrapped (for example by an obfuscator) but must not be disposed by the callback.
    /// </summary>
    public static async Task WriteAsync(
        string path,
        Func<Stream, CancellationToken, Task> write,
        CancellationToken cancellationToken = default)
    {
        var destinationDirectory = Path.GetDirectoryName(Path.GetFullPath(path))
                                   ?? throw new ArgumentException("The path has no directory.", nameof(path));

        Directory.CreateDirectory(destinationDirectory);
        string? temporaryPath = Path.Combine(destinationDirectory, $"{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await write(stream, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, path, true);
            temporaryPath = null;
        }
        catch (Exception writeException) when (temporaryPath is not null)
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (Exception cleanupException) when (cleanupException is IOException or UnauthorizedAccessException)
            {
                throw new AggregateException(
                    "The write failed and its temporary file could not be removed.",
                    writeException,
                    cleanupException);
            }

            throw;
        }
    }
}
