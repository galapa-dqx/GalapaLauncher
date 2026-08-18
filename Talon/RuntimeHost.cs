using Talon.Hooking;
using Talon.Interop;
using Talon.Network;
using Talon.Vfs;

namespace Talon;

// Builds the process-wide managed services and keeps their hooks alive.
internal static class RuntimeHost
{
    private static PreparedRuntime? lifetime;

    public static PreparedRuntime Prepare(
        TalonStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        var prepared = new PreparedRuntime();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scanner = new SigScanner();
            var interop = new GameInteropProvider(scanner);
            var signatures = scanner.ScanTextBatch(
                [VfsHooks.LoadResourceSignature, .. VceResolver.Signatures],
                cancellationToken);

            var initialized = 0;
            // VCE structural refinement reads live .text, so resolve and install
            // network hooks before any other subsystem can patch that section.
            if (TryInitialize(
                    prepared,
                    cancellationToken,
                    "network",
                    () => new NetworkHooks(scanner, signatures, interop, startInfo),
                    hooks => hooks.Initialize()))
                initialized++;
            if (TryInitialize(
                    prepared,
                    cancellationToken,
                    "VFS",
                    () => new VfsHooks(signatures, interop, startInfo),
                    hooks => hooks.Initialize()))
                initialized++;

            cancellationToken.ThrowIfCancellationRequested();
            Log.Info($"managed hook preparation complete ({initialized}/2 subsystems active)");
            return prepared;
        }
        catch
        {
            prepared.Dispose();
            throw;
        }
    }

    public static void Commit(PreparedRuntime prepared)
    {
        ArgumentNullException.ThrowIfNull(prepared);
        if (Interlocked.CompareExchange(ref lifetime, prepared, null) is not null)
            throw new InvalidOperationException("Managed Talon is already committed.");
        Log.Info("managed hook preparation committed");
    }

    // Talon currently lives until DQX exits, so no production path calls this.
    // Keep teardown available for a future unloadable extension/runtime host.
    public static void Shutdown()
    {
        Interlocked.Exchange(ref lifetime, null)?.Dispose();
    }

    private static bool TryInitialize<T>(
        PreparedRuntime prepared,
        CancellationToken cancellationToken,
        string name,
        Func<T> create,
        Action<T> initialize) where T : IDisposable
    {
        T? subsystem = default;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            subsystem = create();
            initialize(subsystem);
            cancellationToken.ThrowIfCancellationRequested();
            prepared.Add(subsystem);
            return true;
        }
        catch (Exception exception)
        {
            try { subsystem?.Dispose(); }
            catch (Exception disposeException)
            {
                Log.Error($"managed {name} cleanup failed", disposeException);
            }
            cancellationToken.ThrowIfCancellationRequested();
            Log.Error($"managed {name} initialization failed; continuing without it", exception);
            FailureNotifier.ShowOnce(name, exception);
            return false;
        }
    }

    internal sealed class PreparedRuntime : IDisposable
    {
        private readonly List<IDisposable> subsystems = [];
        private int disposed;

        public void Add(IDisposable subsystem)
        {
            ObjectDisposedException.ThrowIf(disposed != 0, this);
            subsystems.Add(subsystem);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            for (var index = subsystems.Count - 1; index >= 0; index--)
            {
                try { subsystems[index].Dispose(); }
                catch (Exception exception)
                {
                    Log.Error("managed subsystem teardown failed", exception);
                }
            }
            subsystems.Clear();
        }
    }
}
