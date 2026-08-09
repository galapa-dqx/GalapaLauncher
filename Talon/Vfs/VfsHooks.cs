using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Text;
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
    private static readonly Encoding GamePathEncoding = CreateGamePathEncoding();
    private readonly LooseFileAssetProvider? provider =
        string.IsNullOrWhiteSpace(startInfo.OverrideDirectory)
            ? null
            : new LooseFileAssetProvider(startInfo.OverrideDirectory);
    private Hook<VfsLoadResourceDelegate>? hook;
    private VfsLoadResourceDelegate? original;
    private readonly ConcurrentDictionary<nint, VfsCallbacks> callbacks = [];
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
        original = hook.OriginalDisposeSafe;
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
        var originalCall = original;
        if (originalCall is null) return 0;
        try
        {
            var path = pathPointer == 0 ? null : DecodeGamePath(pathPointer);
            if (startInfo.VfsCensus && path is not null &&
                Interlocked.Increment(ref censusCount) <= CensusCap)
                Log.Info($"VFS census exp={expansion} mount={mount} path={path}");

            // Overrides are keyed by the logical VFS path, intentionally not by
            // expansion or mount. One translated asset replaces that path in any
            // archive layer from which DQX requests it.
            if (self == 0 || path is null || provider is null ||
                !provider.TryOpen(path, out var overrideStream))
                return TryCallOriginal(
                    originalCall,
                    self,
                    pathPointer,
                    expansion,
                    mount,
                    mustBeZero);

            using (overrideStream)
            {
                if (TryConstructOverride(
                        self,
                        pathPointer,
                        overrideStream,
                        out var resource,
                        out var size))
                {
                    Log.Info($"VFS override {path} ({size} bytes) -> 0x{resource:X8}");
                    return resource;
                }
            }
        }
        catch (Exception exception)
        {
            Log.Error("VFS detour failed open", exception);
        }
        return TryCallOriginal(
            originalCall,
            self,
            pathPointer,
            expansion,
            mount,
            mustBeZero);
    }

    private bool TryConstructOverride(
        nint self,
        nint pathPointer,
        FileStream overrideStream,
        out nint resource,
        out int size)
    {
        resource = 0;
        size = 0;
        if (overrideStream.Length is <= 0 or > int.MaxValue) return false;
        size = checked((int)overrideStream.Length);
        var bytes = new byte[size];
        overrideStream.ReadExactly(bytes);

        // DQX 8.0 Vfs_LoadResource calls these three slots with caller stack
        // cleanup (cdecl). It also passes the allocated payload to Construct and
        // does not free that payload afterward; a successful resource owns it.
        var callback = callbacks.GetOrAdd(self, static manager =>
        {
            var allocateAddress = Marshal.ReadIntPtr(manager + 0x110);
            var freeAddress = Marshal.ReadIntPtr(manager + 0x114);
            var constructAddress = Marshal.ReadIntPtr(manager + 0x11C);
            if (allocateAddress == 0 || freeAddress == 0 || constructAddress == 0)
                throw new InvalidOperationException("VFS allocator callbacks are null.");
            return new VfsCallbacks(
                Marshal.GetDelegateForFunctionPointer<VfsAllocateDelegate>(allocateAddress),
                Marshal.GetDelegateForFunctionPointer<VfsFreeDelegate>(freeAddress),
                Marshal.GetDelegateForFunctionPointer<VfsConstructDelegate>(constructAddress));
        });
        var buffer = callback.Allocate(0, checked((uint)bytes.Length), 1);
        if (buffer == 0) return false;

        try
        {
            Marshal.Copy(bytes, 0, buffer, bytes.Length);
            resource = callback.Construct(
                pathPointer,
                checked((uint)bytes.Length),
                buffer,
                0,
                0);
            if (resource != 0) return true;

            callback.Free(0, buffer);
            Log.Warning("VFS constructor rejected override; falling back");
            return false;
        }
        catch
        {
            callback.Free(0, buffer);
            throw;
        }
    }

    private static nint TryCallOriginal(
        VfsLoadResourceDelegate original,
        nint self,
        nint path,
        int expansion,
        int mount,
        int mustBeZero)
    {
        try { return original(self, path, expansion, mount, mustBeZero); }
        catch (Exception exception)
        {
            Log.Error("VFS original call failed", exception);
            return 0;
        }
    }

    private static string DecodeGamePath(nint pointer)
    {
        const int maximumPathBytes = 4096;
        var length = 0;
        while (length < maximumPathBytes && Marshal.ReadByte(pointer + length) != 0)
            length++;
        if (length == maximumPathBytes)
            throw new InvalidDataException("VFS path exceeds the 4096-byte safety limit.");
        var bytes = new byte[length];
        Marshal.Copy(pointer, bytes, 0, bytes.Length);
        return GamePathEncoding.GetString(bytes);
    }

    private static Encoding CreateGamePathEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(
            932,
            EncoderFallback.ExceptionFallback,
            DecoderFallback.ExceptionFallback);
    }

    private sealed record VfsCallbacks(
        VfsAllocateDelegate Allocate,
        VfsFreeDelegate Free,
        VfsConstructDelegate Construct);

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
