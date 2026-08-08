using System.Runtime.InteropServices;

namespace Talon.Hooking;

internal sealed class FunctionPointerVariableHook<T> : Hook<T> where T : Delegate
{
    private readonly nint originalAddress;
    private readonly T original;
    private readonly T detour;
    private readonly nint detourAddress;
    private bool enabled;

    public FunctionPointerVariableHook(nint address, T detour) : base(address)
    {
        using var scope = HookManager.HookEnableSyncRoot.EnterScope();
        originalAddress = Marshal.ReadIntPtr(address);
        original = Marshal.GetDelegateForFunctionPointer<T>(originalAddress);
        this.detour = detour;
        detourAddress = Marshal.GetFunctionPointerForDelegate(detour);
    }

    public override T Original
    {
        get
        {
            CheckDisposed();
            return original;
        }
    }

    public override T OriginalDisposeSafe => original;
    public override bool IsEnabled => enabled;
    public override string BackendName => "Function pointer";

    public override void Enable()
    {
        using var scope = HookManager.HookEnableSyncRoot.EnterScope();
        CheckDisposed();
        if (IsEnabled) return;
        WritePointer(detourAddress);
        enabled = true;
    }

    public override void Disable()
    {
        using var scope = HookManager.HookEnableSyncRoot.EnterScope();
        if (IsDisposed || !IsEnabled) return;
        WritePointer(originalAddress);
        enabled = false;
    }

    public override void Dispose()
    {
        using var scope = HookManager.HookEnableSyncRoot.EnterScope();
        if (IsDisposed) return;
        Disable();
        GC.KeepAlive(detour);
        base.Dispose();
    }

    private void WritePointer(nint value)
    {
        if (!HookNativeMethods.VirtualProtect(
                Address,
                (nuint)nint.Size,
                HookNativeMethods.PageReadWrite,
                out var oldProtect))
            throw new InvalidOperationException(
                $"VirtualProtect failed at 0x{Address:X8} ({Marshal.GetLastWin32Error()}).");
        Marshal.WriteIntPtr(Address, value);
        HookNativeMethods.VirtualProtect(Address, (nuint)nint.Size, oldProtect, out _);
        HookNativeMethods.FlushInstructionCache(
            HookNativeMethods.GetCurrentProcess(),
            Address,
            (nuint)nint.Size);
    }
}
