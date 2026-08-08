using System.Runtime.InteropServices;
using Talon.Hooking;
using Talon.Interop;

namespace Talon.Vfs;

// Redirects matching game VFS reads to canonicalized loose files.
internal sealed class VfsHooks(
    ISigScanner scanner,
    IGameInteropProvider interop,
    TalonStartInfo startInfo) : IDisposable
{
    private const string VfsSignature =
        "53 8B DC 83 ?? ?? 83 ?? ?? 83 ?? ?? 55 8B ?? ?? 89 ?? ?? ?? " +
        "8B EC B8 ?? ?? ?? ?? E8 ?? ?? ?? ?? A1 ?? ?? ?? ?? 33 C5 89 45 FC " +
        "8B 43 0C 8B 53 08";

    private const int CensusCap = 400;
    private readonly LooseFileAssetProvider? provider =
        string.IsNullOrWhiteSpace(startInfo.OverrideDirectory)
            ? null
            : new LooseFileAssetProvider(startInfo.OverrideDirectory);
    private Hook<VfsLoadResourceDelegate>? hook;
    private int censusCount;

    public void Initialize()
    {
        var matches = scanner.ScanAllText(VfsSignature);
        if (matches.Length != 1)
            throw new InvalidOperationException(
                $"VFS signature expected one match but found {matches.Length}.");

        hook = interop.HookFromAddress(
            matches[0],
            (VfsLoadResourceDelegate)VfsLoadResourceDetour);
        hook.Enable();
        Log.Info($"VFS hook enabled at 0x{matches[0]:X8}");
    }

    public void Dispose() => hook?.Dispose();

    private nint VfsLoadResourceDetour(
        nint self,
        nint pathPointer,
        int expansion,
        int mount,
        int mustBeZero)
    {
        var path = pathPointer == 0 ? null : Marshal.PtrToStringAnsi(pathPointer);
        if (startInfo.VfsCensus && path is not null &&
            Interlocked.Increment(ref censusCount) <= CensusCap)
            Log.Info($"VFS census exp={expansion} mount={mount} path={path}");

        if (self == 0 || path is null || provider is null ||
            !provider.TryResolve(path, out var overridePath))
            return hook!.Original(self, pathPointer, expansion, mount, mustBeZero);

        try
        {
            var bytes = File.ReadAllBytes(overridePath);
            if (bytes.Length == 0) return hook!.Original(
                self, pathPointer, expansion, mount, mustBeZero);

            var allocateAddress = Marshal.ReadIntPtr(self + 0x110);
            var freeAddress = Marshal.ReadIntPtr(self + 0x114);
            var constructAddress = Marshal.ReadIntPtr(self + 0x11C);
            if (allocateAddress == 0 || freeAddress == 0 || constructAddress == 0)
                return hook!.Original(self, pathPointer, expansion, mount, mustBeZero);

            var allocate = Marshal.GetDelegateForFunctionPointer<VfsAllocateDelegate>(
                allocateAddress);
            var free = Marshal.GetDelegateForFunctionPointer<VfsFreeDelegate>(freeAddress);
            var construct = Marshal.GetDelegateForFunctionPointer<VfsConstructDelegate>(
                constructAddress);
            var buffer = allocate(0, checked((uint)bytes.Length), 1);
            if (buffer == 0)
                return hook!.Original(self, pathPointer, expansion, mount, mustBeZero);

            try
            {
                Marshal.Copy(bytes, 0, buffer, bytes.Length);
                var resource = construct(
                    pathPointer,
                    checked((uint)bytes.Length),
                    buffer,
                    0,
                    0);
                Log.Info($"VFS override {path} ({bytes.Length} bytes) -> 0x{resource:X8}");
                return resource;
            }
            catch
            {
                free(0, buffer);
                throw;
            }
        }
        catch (Exception exception)
        {
            Log.Error($"VFS override failed for '{path}', falling back", exception);
            return hook!.Original(self, pathPointer, expansion, mount, mustBeZero);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate nint VfsLoadResourceDelegate(
        nint self,
        nint path,
        int expansion,
        int mount,
        int mustBeZero);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint VfsAllocateDelegate(int tag, uint size, int flag);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void VfsFreeDelegate(int tag, nint buffer);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint VfsConstructDelegate(
        nint path,
        uint size,
        nint buffer,
        nint file,
        uint offset);
}
