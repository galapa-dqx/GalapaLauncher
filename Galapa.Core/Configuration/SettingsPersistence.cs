namespace Galapa.Core.Configuration;

/// <summary>
///     Persists <see cref="Settings" /> to disk; injectable so callers can be tested without touching the file system.
/// </summary>
public interface ISettingsPersistence
{
    /// <summary>
    ///     Saves <paramref name="settings" />, atomically replacing the settings file. The returned task completes once a
    ///     write that includes the settings as they were at call time has finished.
    /// </summary>
    /// <exception cref="InvalidSettingsException">The settings hold invalid values; nothing was written.</exception>
    Task SaveAsync(Settings settings, CancellationToken cancellationToken = default);
}

/// <summary>
///     Default <see cref="ISettingsPersistence" />. Requests are coalesced, latest wins: all requests made before a write
///     takes its snapshot share that write, so a burst of commits produces one write rather than one per commit.
/// </summary>
/// <remarks>
///     The snapshot is taken one turn after the first request, on the caller's synchronization context. Call this from
///     the UI thread so the snapshot (and the validation that precedes it) never races with property changes.
/// </remarks>
public class SettingsPersistence : ISettingsPersistence
{
    private readonly object _lock = new();
    private Task _current = Task.CompletedTask;
    private Task? _pending;
    private Settings? _pendingSettings;

    public Task SaveAsync(Settings settings, CancellationToken cancellationToken = default)
    {
        lock (this._lock)
        {
            this._pendingSettings = settings;
            this._pending ??= this.WriteAfterAsync(this._current);
            return this._pending.WaitAsync(cancellationToken);
        }
    }

    private async Task WriteAfterAsync(Task previous)
    {
        // Yield so every request made in the same turn joins this write before it takes its snapshot.
        await Task.Yield();

        try
        {
            await previous;
        }
        catch
        {
            // The previous write's callers observe its failure; this write proceeds regardless.
        }

        Task write;
        lock (this._lock)
        {
            var settings = this._pendingSettings!;
            this._pending = null;
            this._pendingSettings = null;
            write = this.WriteAsync(settings);
            this._current = write;
        }

        await write;
    }

    /// <summary>
    ///     Performs one write. The synchronous part, up to its first await, runs inside the coalescing lock and must take
    ///     the snapshot.
    /// </summary>
    protected virtual Task WriteAsync(Settings settings) => settings.SaveAsync();
}
