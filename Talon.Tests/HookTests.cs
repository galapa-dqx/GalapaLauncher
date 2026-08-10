using System.Runtime.InteropServices;
using Reloaded.Hooks.Definitions.X86;
using Talon.Hooking;

namespace Talon.Tests;

public sealed class HookTests
{
    [Fact]
    public void FunctionPointerOriginalDisposeSafeUsesStoredFunctionAddress()
    {
        UnaryDelegate original = value => value + 1;
        UnaryDelegate detour = value => value + 2;
        var slot = Marshal.AllocHGlobal(nint.Size);
        try
        {
            var originalAddress = Marshal.GetFunctionPointerForDelegate(original);
            var detourAddress = Marshal.GetFunctionPointerForDelegate(detour);
            Marshal.WriteIntPtr(slot, originalAddress);
            var hook = new FunctionPointerVariableHook<UnaryDelegate>(slot, detour);

            hook.Enable();
            Assert.Equal(detourAddress, Marshal.ReadIntPtr(slot));
            hook.Dispose();

            Assert.Equal(originalAddress, Marshal.ReadIntPtr(slot));
            Assert.Equal(42, hook.OriginalDisposeSafe(41));
            Assert.Throws<ObjectDisposedException>(() => _ = hook.Original);
            GC.KeepAlive(original);
            GC.KeepAlive(detour);
        }
        finally
        {
            Marshal.FreeHGlobal(slot);
        }
    }

    [Fact]
    public void FunctionPointerHookPreservesThiscallReceiverAndStackArgument()
    {
        Assert.False(Environment.Is64BitProcess);
        HookDelegateValidator.Validate<ThiscallDelegate>();
        ThiscallDelegate original = static (self, value) => (int)self + value;
        nint receivedSelf = 0;
        var receivedValue = 0;
        ThiscallDelegate detour = (self, value) =>
        {
            receivedSelf = self;
            receivedValue = value;
            return value + 100;
        };
        var slot = Marshal.AllocHGlobal(nint.Size);
        try
        {
            Marshal.WriteIntPtr(slot, Marshal.GetFunctionPointerForDelegate(original));
            using var hook = new FunctionPointerVariableHook<ThiscallDelegate>(slot, detour);
            hook.Enable();

            var invoke = Marshal.GetDelegateForFunctionPointer<ThiscallDelegate>(
                Marshal.ReadIntPtr(slot));
            Assert.Equal(107, invoke((nint)0x1234, 7));
            Assert.Equal((nint)0x1234, receivedSelf);
            Assert.Equal(7, receivedValue);
            Assert.Equal(12, hook.OriginalDisposeSafe((nint)5, 7));
            GC.KeepAlive(original);
            GC.KeepAlive(detour);
        }
        finally { Marshal.FreeHGlobal(slot); }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int UnaryDelegate(int value);

    [Function(CallingConventions.MicrosoftThiscall)]
    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate int ThiscallDelegate(nint self, int value);
}
