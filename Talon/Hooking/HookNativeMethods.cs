using System.Runtime.InteropServices;

namespace Talon.Hooking;

internal static partial class HookNativeMethods
{
    internal const uint PageReadWrite = 0x04;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool VirtualProtect(
        nint address,
        nuint size,
        uint newProtect,
        out uint oldProtect);

    [LibraryImport("kernel32.dll")]
    internal static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool FlushInstructionCache(
        nint process,
        nint address,
        nuint size);
}
