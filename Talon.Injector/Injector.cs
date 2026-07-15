using System.Runtime.InteropServices;

namespace Talon.Injector;

/// <summary>
/// Launches a target process suspended and loads a native boot DLL into it via an
/// early-bird APC, so the DLL is mapped before the target executes its own entry point.
///
/// P/Invoke style deliberately mirrors <c>Galapa.Core/Game/GameProcess.cs</c>
/// (source-generated <c>[LibraryImport]</c>, <c>partial</c>, UTF-16 marshalling) — the
/// convention is copied, not the dependency: Talon must not depend on Galapa.
/// </summary>
public static partial class Injector
{
    /// <summary>Result of a launch + inject operation.</summary>
    public readonly record struct InjectResult(int ProcessId, nint ProcessHandle, nint ThreadHandle);

    /// <summary>
    /// Launches <paramref name="gameCommandLine"/> suspended, queues an APC that loads
    /// <paramref name="bootDllPath"/>, then resumes the process. The APC drains during the
    /// loader's early alertable wait, before the target's entry point runs.
    /// </summary>
    /// <param name="gameCommandLine">
    /// Full command line for the target, verbatim — first token is the quoted exe path.
    /// Passed straight to <c>CreateProcessW</c>'s <c>lpCommandLine</c>; the caller owns any
    /// quoting (see the raw-tail handoff in Program.cs).
    /// </param>
    /// <param name="workingDir">Working directory for the target process.</param>
    /// <param name="bootDllPath">Absolute path to the native x86 boot DLL to inject.</param>
    public static InjectResult LaunchAndInject(string gameCommandLine, string workingDir, string bootDllPath)
    {
        if (string.IsNullOrWhiteSpace(gameCommandLine))
            throw new ArgumentException("Game command line is empty.", nameof(gameCommandLine));
        if (!File.Exists(bootDllPath))
            throw new FileNotFoundException("Boot DLL not found.", bootDllPath);

        // Fail loudly if the boot DLL is the wrong architecture rather than failing
        // mysteriously inside the target.
        var bootMachine = ReadPeMachine(bootDllPath);
        if (bootMachine != IMAGE_FILE_MACHINE_I386)
            throw new BadImageFormatException(
                $"Boot DLL '{bootDllPath}' is machine 0x{bootMachine:X4}, expected x86 (0x{IMAGE_FILE_MACHINE_I386:X4}). " +
                "The injector and boot DLL must both be x86 to inject the 32-bit game.");

        var startupInfo = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };

        var created = CreateProcess(
            null,
            gameCommandLine,
            nint.Zero,
            nint.Zero,
            false,
            CREATE_SUSPENDED,
            nint.Zero,
            workingDir,
            ref startupInfo,
            out var pi);
        if (!created)
            throw new InvalidOperationException($"CreateProcess failed (Win32 error {Marshal.GetLastWin32Error()}).");

        try
        {
            // 1. Allocate a buffer in the target and write the UTF-16 DLL path into it.
            //    LoadLibraryW will read the path from here when the APC fires.
            var pathBytes = System.Text.Encoding.Unicode.GetBytes(bootDllPath + '\0');
            var remotePath = VirtualAllocEx(pi.hProcess, nint.Zero, (nuint)pathBytes.Length,
                MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (remotePath == nint.Zero)
                throw new InvalidOperationException($"VirtualAllocEx failed (Win32 error {Marshal.GetLastWin32Error()}).");

            if (!WriteProcessMemory(pi.hProcess, remotePath, pathBytes, (nuint)pathBytes.Length, out _))
                throw new InvalidOperationException($"WriteProcessMemory failed (Win32 error {Marshal.GetLastWin32Error()}).");

            // 2. Resolve LoadLibraryW. kernel32 is ASLR-randomized once per boot with a
            //    base shared across same-bitness processes, so this address is valid in
            //    the target too — which is why the injector must be x86.
            var kernel32 = GetModuleHandle("kernel32.dll");
            if (kernel32 == nint.Zero)
                throw new InvalidOperationException("GetModuleHandle(kernel32.dll) returned null.");
            var loadLibraryW = GetProcAddress(kernel32, "LoadLibraryW");
            if (loadLibraryW == nint.Zero)
                throw new InvalidOperationException("GetProcAddress(LoadLibraryW) returned null.");

            // 3. Queue the APC onto the (still suspended) primary thread, then resume.
            //    The APC calls LoadLibraryW(remotePath) during the loader's early
            //    alertable wait — before the game's entry point executes.
            if (QueueUserAPC(loadLibraryW, pi.hThread, remotePath) == 0)
                throw new InvalidOperationException($"QueueUserAPC failed (Win32 error {Marshal.GetLastWin32Error()}).");

            if (ResumeThread(pi.hThread) == unchecked((uint)-1))
                throw new InvalidOperationException($"ResumeThread failed (Win32 error {Marshal.GetLastWin32Error()}).");

            // Note: remotePath is intentionally not freed — the target reads it
            // asynchronously after we return. It is a small, one-shot leak in the target.
            return new InjectResult((int)pi.dwProcessId, pi.hProcess, pi.hThread);
        }
        catch
        {
            // On failure, don't leave a suspended zombie around.
            TerminateProcess(pi.hProcess, 1);
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            throw;
        }
    }

    /// <summary>Reads the COFF machine field from a PE file on disk (0x14C == x86).</summary>
    private static ushort ReadPeMachine(string path)
    {
        using var fs = File.OpenRead(path);
        using var reader = new BinaryReader(fs);
        if (reader.ReadUInt16() != 0x5A4D) // "MZ"
            throw new BadImageFormatException($"'{path}' is not a PE image (no MZ header).");
        fs.Position = 0x3C;
        var peOffset = reader.ReadInt32();
        fs.Position = peOffset;
        if (reader.ReadUInt32() != 0x00004550) // "PE\0\0"
            throw new BadImageFormatException($"'{path}' has no PE signature.");
        return reader.ReadUInt16(); // Machine
    }

    private const uint CREATE_SUSPENDED = 0x00000004;
    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint PAGE_READWRITE = 0x04;
    private const ushort IMAGE_FILE_MACHINE_I386 = 0x014C;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcess(
        string? lpApplicationName,
        string lpCommandLine,
        nint lpProcessAttributes,
        nint lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        nint lpEnvironment,
        string lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint VirtualAllocEx(nint hProcess, nint lpAddress, nuint dwSize, uint flAllocationType, uint flProtect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteProcessMemory(nint hProcess, nint lpBaseAddress, byte[] lpBuffer, nuint nSize, out nuint lpNumberOfBytesWritten);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandle(string lpModuleName);

    [LibraryImport("kernel32.dll", EntryPoint = "GetProcAddress", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint GetProcAddress(nint hModule, string lpProcName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint QueueUserAPC(nint pfnAPC, nint hThread, nint dwData);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint ResumeThread(nint hThread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(nint hProcess, uint uExitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct STARTUPINFO
    {
        public int cb;
        public nint lpReserved;
        public nint lpDesktop;
        public nint lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public nint lpReserved2;
        public nint hStdInput;
        public nint hStdOutput;
        public nint hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public nint hProcess;
        public nint hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }
}
