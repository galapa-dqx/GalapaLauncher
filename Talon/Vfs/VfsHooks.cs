using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions.X86;
using Talon.Hooking;
using Talon.Interop;

namespace Talon.Vfs;

// Redirects matching game VFS reads to canonicalized loose files.
internal sealed class VfsHooks(
    SignatureScanResult signatures,
    IGameInteropProvider interop,
    TalonStartInfo startInfo) : IDisposable
{
    internal static readonly SignatureQuery LoadResourceSignature = new(
        "vfs.load-resource",
        "53 8B DC 83 ?? ?? 83 ?? ?? 83 ?? ?? 55 8B ?? ?? 89 ?? ?? ?? " +
        "8B EC B8 ?? ?? ?? ?? E8 ?? ?? ?? ?? A1 ?? ?? ?? ?? 33 C5 89 45 FC " +
        "8B 43 0C 8B 53 08");

    private const int CensusCap = 400;
    private readonly LooseFileAssetProvider? provider =
        string.IsNullOrWhiteSpace(startInfo.OverrideDirectory)
            ? null
            : new LooseFileAssetProvider(startInfo.OverrideDirectory);
    private Hook<VfsLoadResourceDelegate>? hook;
    private int censusCount;

    public void Initialize()
    {
        var matches = signatures.GetMatches(LoadResourceSignature.Name);
        if (matches.Count != 1)
            throw new InvalidOperationException(
                $"VFS signature expected one match but found {matches.Count}.");

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
            if (TryConstructOverride(self, pathPointer, overridePath, out var resource, out var size))
            {
                Log.Info($"VFS override {path} ({size} bytes) -> 0x{resource:X8}");
                return resource;
            }
        }
        catch (Exception exception)
        {
            Log.Error($"VFS override failed for '{path}', falling back", exception);
        }
        return hook!.Original(self, pathPointer, expansion, mount, mustBeZero);
    }

    private static bool TryConstructOverride(
        nint self,
        nint pathPointer,
        string overridePath,
        out nint resource,
        out int size)
    {
        resource = 0;
        // Read each request so loose translated assets can be edited while DQX runs.
        var bytes = File.ReadAllBytes(overridePath);
        size = bytes.Length;
        if (bytes.Length == 0) return false;

        var allocateAddress = Marshal.ReadIntPtr(self + 0x110);
        var freeAddress = Marshal.ReadIntPtr(self + 0x114);
        var constructAddress = Marshal.ReadIntPtr(self + 0x11C);
        if (allocateAddress == 0 || freeAddress == 0 || constructAddress == 0)
            return false;

        var allocate = Marshal.GetDelegateForFunctionPointer<VfsAllocateDelegate>(
            allocateAddress);
        var free = Marshal.GetDelegateForFunctionPointer<VfsFreeDelegate>(freeAddress);
        var construct = Marshal.GetDelegateForFunctionPointer<VfsConstructDelegate>(
            constructAddress);
        var buffer = allocate(0, checked((uint)bytes.Length), 1);
        if (buffer == 0) return false;

        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            resource = construct(
                pathPointer,
                checked((uint)bytes.Length),
                buffer,
                0,
                0);
            if (resource != 0) return true;

            free(0, buffer);
            Log.Warning("VFS constructor rejected override; falling back");
            return false;
        }
        catch
        {
            free(0, buffer);
            throw;
        }
    }

    [Function(CallingConventions.MicrosoftThiscall)]
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
