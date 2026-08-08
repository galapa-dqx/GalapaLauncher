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

        var vfs = new VfsHooks(scanner, interop, startInfo);
        vfs.Initialize();
        Lifetime.Add(vfs);

        var network = new NetworkHooks(scanner, interop, startInfo);
        network.Initialize();
        Lifetime.Add(network);

        Log.Info("managed hook initialization complete");
    }
}
