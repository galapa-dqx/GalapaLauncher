using System.Runtime.InteropServices;
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

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int UnaryDelegate(int value);
}
