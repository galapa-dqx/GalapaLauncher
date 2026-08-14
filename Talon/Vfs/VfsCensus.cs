using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Talon.Vfs;

internal enum VfsResolutionOutcome
{
    OriginalHit,
    OriginalMiss,
    OriginalError,
    OverrideHit,
    OverrideFallbackHit,
    OverrideFallbackMiss,
    OverrideFallbackError,
}

// Aggregates VFS reads in memory and sends changed paths to one background
// writer. Game detours never wait for SQLite I/O.
internal sealed class VfsCensus : IDisposable
{
    internal const int SchemaVersion = VfsCensusDatabase.SchemaVersion;

    private readonly ConcurrentDictionary<string, CensusEntry> entries =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> dirtyPaths =
        new(StringComparer.Ordinal);
    private readonly Channel<byte> dirty = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly CancellationTokenSource cancellation = new();
    private readonly SemaphoreSlim writeLock = new(1, 1);
    private readonly VfsCensusDatabase database;
    private readonly TimeSpan writeDelay;
    private readonly Task writerTask;
    private int disposed;
    private int recordFailureLogged;
    private int writeFailureLogged;

    public VfsCensus(string? outputPath = null, TimeSpan? writeDelay = null)
    {
        database = new VfsCensusDatabase(outputPath);
        this.writeDelay = writeDelay ?? TimeSpan.FromMilliseconds(500);
        writerTask = Task.Run(RunAsync);
        dirty.Writer.TryWrite(0);
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        Log.Info($"VFS census enabled: {OutputPath}");
    }

    public string OutputPath => database.OutputPath;

    internal static string GetDefaultOutputPath() =>
        VfsCensusDatabase.GetDefaultOutputPath();

    public void Record(
        string path,
        int expansion,
        int mount,
        VfsResolutionOutcome outcome,
        int? replacementBytes = null)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        try
        {
            var normalizedPath = DqxPathHash.Normalize(path);
            var now = DateTimeOffset.UtcNow;
            entries.GetOrAdd(
                    normalizedPath,
                    static value => new CensusEntry(DqxPathHash.DescribeNormalized(value)))
                .Record(expansion, mount, outcome, replacementBytes, now);
            dirtyPaths[normalizedPath] = 0;
            dirty.Writer.TryWrite(0);
        }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref recordFailureLogged, 1) == 0)
                Log.Error("VFS census aggregation failed", exception);
        }
    }

    internal Task FlushAsync(CancellationToken cancellationToken = default) =>
        WritePendingAsync(cancellationToken);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        dirty.Writer.TryComplete();
        try
        {
            if (!writerTask.Wait(TimeSpan.FromSeconds(3)))
            {
                cancellation.Cancel();
                writerTask.GetAwaiter().GetResult();
            }
        }
        catch (OperationCanceledException) { }
        catch (AggregateException exception)
        {
            Log.Error("VFS census writer failed during shutdown", exception.GetBaseException());
        }
        catch (Exception exception)
        {
            Log.Error("VFS census writer failed during shutdown", exception);
        }
        cancellation.Dispose();
        writeLock.Dispose();
    }

    private void OnProcessExit(object? sender, EventArgs args) => Dispose();

    private async Task RunAsync()
    {
        try
        {
            await foreach (var signal in dirty.Reader.ReadAllAsync(cancellation.Token))
            {
                _ = signal;
                while (dirty.Reader.TryRead(out _)) { }
                if (writeDelay > TimeSpan.Zero)
                    await Task.Delay(writeDelay, cancellation.Token).ConfigureAwait(false);
                while (dirty.Reader.TryRead(out _)) { }
                await TryWritePendingAsync(cancellation.Token).ConfigureAwait(false);
            }

            await TryWritePendingAsync(CancellationToken.None).ConfigureAwait(false);
            await TryEndSessionAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }

    private async Task TryWritePendingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await WritePendingAsync(cancellationToken).ConfigureAwait(false);
            if (Interlocked.Exchange(ref writeFailureLogged, 0) != 0)
                Log.Info("VFS census database writer recovered");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            if (Interlocked.Exchange(ref writeFailureLogged, 1) == 0)
                Log.Error("VFS census database write failed", exception);
        }
    }

    private async Task WritePendingAsync(CancellationToken cancellationToken)
    {
        await writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        var snapshots = DrainDirtyEntries();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            database.Write(snapshots);
        }
        catch
        {
            foreach (var snapshot in snapshots)
                dirtyPaths[snapshot.Path] = 0;
            dirty.Writer.TryWrite(0);
            throw;
        }
        finally { writeLock.Release(); }
    }

    private async Task TryEndSessionAsync()
    {
        try
        {
            await writeLock.WaitAsync().ConfigureAwait(false);
            try { database.CompleteSession(); }
            finally { writeLock.Release(); }
        }
        catch (Exception exception)
        {
            Log.Error("VFS census session finalization failed", exception);
        }
    }

    private VfsCensusEntrySnapshot[] DrainDirtyEntries()
    {
        var snapshots = new List<VfsCensusEntrySnapshot>();
        foreach (var path in dirtyPaths.Keys)
            if (dirtyPaths.TryRemove(path, out _) && entries.TryGetValue(path, out var entry))
                snapshots.Add(entry.Snapshot());
        return snapshots.ToArray();
    }

    private static string GetOutcomeName(VfsResolutionOutcome outcome) => outcome switch
    {
        VfsResolutionOutcome.OriginalHit => "original-hit",
        VfsResolutionOutcome.OriginalMiss => "original-miss",
        VfsResolutionOutcome.OriginalError => "original-error",
        VfsResolutionOutcome.OverrideHit => "override-hit",
        VfsResolutionOutcome.OverrideFallbackHit => "override-fallback-hit",
        VfsResolutionOutcome.OverrideFallbackMiss => "override-fallback-miss",
        VfsResolutionOutcome.OverrideFallbackError => "override-fallback-error",
        _ => throw new ArgumentOutOfRangeException(nameof(outcome)),
    };

    private sealed class CensusEntry(DqxVirtualPath path)
    {
        private readonly object sync = new();
        private readonly Dictionary<ResolutionKey, ResolutionAggregate> resolutions = [];

        public void Record(
            int expansion,
            int mount,
            VfsResolutionOutcome outcome,
            int? replacementBytes,
            DateTimeOffset seen)
        {
            lock (sync)
            {
                var key = new ResolutionKey(expansion, mount, outcome);
                if (!resolutions.TryGetValue(key, out var aggregate))
                    resolutions.Add(key, aggregate = new ResolutionAggregate(seen));
                aggregate.Count++;
                aggregate.LastSeen = seen;
                if (replacementBytes.HasValue)
                    aggregate.ReplacementBytes = replacementBytes;
            }
        }

        public VfsCensusEntrySnapshot Snapshot()
        {
            lock (sync)
                return new VfsCensusEntrySnapshot(
                    path.Path,
                    path.Directory,
                    path.File,
                    path.DirectoryHash,
                    path.FileHash,
                    resolutions
                        .OrderBy(static pair => pair.Key.Expansion)
                        .ThenBy(static pair => pair.Key.Mount)
                        .ThenBy(static pair => pair.Key.Outcome)
                        .Select(static pair => new VfsResolutionSnapshot(
                            pair.Key.Expansion,
                            pair.Key.Mount,
                            GetOutcomeName(pair.Key.Outcome),
                            pair.Value.Count,
                            pair.Value.FirstSeen,
                            pair.Value.LastSeen,
                            pair.Value.ReplacementBytes))
                        .ToArray());
        }
    }

    private readonly record struct ResolutionKey(
        int Expansion,
        int Mount,
        VfsResolutionOutcome Outcome);

    private sealed class ResolutionAggregate(DateTimeOffset firstSeen)
    {
        public long Count { get; set; }
        public DateTimeOffset FirstSeen { get; } = firstSeen;
        public DateTimeOffset LastSeen { get; set; } = firstSeen;
        public int? ReplacementBytes { get; set; }
    }
}

internal sealed record VfsCensusEntrySnapshot(
    string Path,
    string Directory,
    string File,
    string DirectoryHash,
    string FileHash,
    IReadOnlyList<VfsResolutionSnapshot> Resolutions);

internal sealed record VfsResolutionSnapshot(
    int Expansion,
    int Mount,
    string Outcome,
    long Count,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    int? ReplacementBytes);
