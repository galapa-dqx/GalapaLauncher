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

        var initialized = 0;
        if (TryInitialize(
                "VFS",
                () => new VfsHooks(scanner, interop, startInfo),
                hooks => hooks.Initialize()))
            initialized++;
        if (TryInitialize(
                "network",
                () => new NetworkHooks(scanner, interop, startInfo),
                hooks => hooks.Initialize()))
            initialized++;

        Log.Info($"managed hook initialization complete ({initialized}/2 subsystems active)");
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
