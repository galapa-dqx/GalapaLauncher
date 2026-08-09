using Talon.Hooking;
using Talon.Interop;
using Talon.Network;
using Talon.Vfs;

namespace Talon;

// Builds the process-wide managed services and keeps their hooks alive.
internal static class RuntimeHost
{
    private static readonly List<IDisposable> Lifetime = [];

    public static void Initialize(TalonStartInfo startInfo)
    {
        var scanner = new SigScanner();
        var interop = new GameInteropProvider(scanner);
        var signatures = scanner.ScanTextBatch(
            [VfsHooks.LoadResourceSignature, .. VceResolver.Signatures]);

        var initialized = 0;
        if (TryInitialize(
                "VFS",
                () => new VfsHooks(signatures, interop, startInfo),
                hooks => hooks.Initialize()))
            initialized++;
        if (TryInitialize(
                "network",
                () => new NetworkHooks(scanner, signatures, interop, startInfo),
                hooks => hooks.Initialize()))
            initialized++;

        Log.Info($"managed hook initialization complete ({initialized}/2 subsystems active)");
    }

    public static void Shutdown()
    {
        for (var index = Lifetime.Count - 1; index >= 0; index--)
        {
            try { Lifetime[index].Dispose(); }
            catch (Exception exception)
            {
                Log.Error("managed subsystem teardown failed", exception);
            }
        }
        Lifetime.Clear();
    }

    private static bool TryInitialize<T>(
        string name,
        Func<T> create,
        Action<T> initialize) where T : IDisposable
    {
        T? subsystem = default;
        try
        {
            subsystem = create();
            initialize(subsystem);
            Lifetime.Add(subsystem);
            return true;
        }
        catch (Exception exception)
        {
            try { subsystem?.Dispose(); }
            catch (Exception disposeException)
            {
                Log.Error($"managed {name} cleanup failed", disposeException);
            }
            Log.Error($"managed {name} initialization failed; continuing without it", exception);
            return false;
        }
    }
}
