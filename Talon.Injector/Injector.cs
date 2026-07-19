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
    /// <param name="armExecBpRva">
    /// If set, arm a hardware execute breakpoint (DR0) at <c>imageBase + rva</c> on the main
    /// thread while it is still suspended, so it is live before the target's entry point runs.
    /// The injected boot DLL's VEH catches it. The universal barrier uses this to anchor on the
    /// KONN second-stage entry, which executes too early to instrument from a worker thread.
    /// </param>
    public static InjectResult LaunchAndInject(string gameCommandLine, string workingDir, string bootDllPath,
        uint? armExecBpRva = null)
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

            // 3. Optionally arm a hardware execute breakpoint on the still-suspended main
            //    thread, so it is live before the entry point (and the packer stub) runs.
            //    Must happen before ResumeThread; the boot DLL's VEH handles the trap.
            if (armExecBpRva is { } rva)
                ArmExecuteBreakpoint(pi.hProcess, pi.hThread, rva);

            // 4. Queue the APC onto the (still suspended) primary thread, then resume.
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

    /// <summary>
    /// Finds and decodes this packer's 32-byte KONN stage record. The record contains
    /// both the PE entry RVA and the second-stage entry RVA, which makes the validation
    /// independent of file offsets and game load addresses.
    /// </summary>
    public static uint FindKonnStageEntryRva(string path)
    {
        var image = File.ReadAllBytes(path);
        if (image.Length < 0x100)
            throw new BadImageFormatException($"'{path}' is too small to be a PE image.");

        var peOffset = BitConverter.ToInt32(image, 0x3C);
        if (peOffset < 0 || peOffset + 0x78 > image.Length ||
            BitConverter.ToUInt32(image, peOffset) != 0x00004550)
            throw new BadImageFormatException($"'{path}' has no valid PE header.");

        var optionalHeader = peOffset + 24;
        if (BitConverter.ToUInt16(image, optionalHeader) != 0x010B)
            throw new BadImageFormatException($"'{path}' is not a PE32 image.");
        var peEntryRva = BitConverter.ToUInt32(image, optionalHeader + 16);
        var sizeOfImage = BitConverter.ToUInt32(image, optionalHeader + 56);

        var raw = new uint[8];
        var decoded = new uint[8];
        for (var offset = 0; offset + 32 <= image.Length; offset += 4)
        {
            var raw0 = BitConverter.ToUInt32(image, offset);
            var raw1 = BitConverter.ToUInt32(image, offset + 4);
            if ((raw1 ^ raw0) != 0x4E4E4F4B) // bytes "KONN"
                continue;

            for (var i = 0; i < 8; ++i)
                raw[i] = BitConverter.ToUInt32(image, offset + i * 4);

            decoded[0] = raw[0];
            var previous = raw[0];
            for (uint i = 0; i < 7; ++i)
            {
                decoded[(int)i + 1] = raw[(int)i + 1] ^ previous;
                previous = unchecked((raw[(int)i + 1] - i + previous) ^ (i * i));
            }

            var stageBaseRva = decoded[3];
            var stageEntryRva = decoded[6];
            if (decoded[1] == 0x4E4E4F4B && decoded[2] == peEntryRva &&
                stageBaseRva < sizeOfImage && stageEntryRva >= stageBaseRva &&
                stageEntryRva < sizeOfImage)
            {
                Console.WriteLine($"[talon] KONN metadata @ file+0x{offset:X}: stage2 RVA 0x{stageEntryRva:X}");
                return stageEntryRva;
            }
        }

        throw new InvalidDataException(
            $"No valid KONN unpacker record was found in '{path}'; refusing to arm an unsafe barrier.");
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

    /// <summary>
    /// Arms DR0 as a 1-byte execute breakpoint at <c>imageBase + rva</c> on the (suspended)
    /// thread. Reads the target's load base from its PEB, since the module isn't enumerable
    /// via the usual APIs while suspended.
    /// </summary>
    private static void ArmExecuteBreakpoint(nint hProcess, nint hThread, uint rva)
    {
        var imageBase = GetRemoteImageBase(hProcess);
        var target = imageBase + rva;
        Console.WriteLine($"[talon] arming DR0 @ 0x{target:X8} (imageBase 0x{imageBase:X8} + 0x{rva:X})");

        var ctx = new CONTEXT_X86 { ContextFlags = CONTEXT_DEBUG_REGISTERS };
        if (!GetThreadContext(hThread, ref ctx))
            throw new InvalidOperationException($"GetThreadContext failed (Win32 error {Marshal.GetLastWin32Error()}).");

        ctx.Dr0 = target;
        ctx.Dr7 = 0x00000001;                 // L0=1, RW0=00 (execute), LEN0=00 (1 byte)
        ctx.ContextFlags = CONTEXT_DEBUG_REGISTERS;
        if (!SetThreadContext(hThread, ref ctx))
            throw new InvalidOperationException($"SetThreadContext failed (Win32 error {Marshal.GetLastWin32Error()}).");
    }

    /// <summary>Reads the target process's image base from PEB.ImageBaseAddress (PEB+0x08 on x86).</summary>
    private static uint GetRemoteImageBase(nint hProcess)
    {
        var pbi = new PROCESS_BASIC_INFORMATION();
        var status = NtQueryInformationProcess(hProcess, 0, ref pbi, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _);
        if (status != 0)
            throw new InvalidOperationException($"NtQueryInformationProcess failed (NTSTATUS 0x{status:X8}).");

        var buf = new byte[4];
        if (!ReadProcessMemory(hProcess, pbi.PebBaseAddress + 0x08, buf, 4, out _))
            throw new InvalidOperationException($"ReadProcessMemory(PEB.ImageBaseAddress) failed (Win32 error {Marshal.GetLastWin32Error()}).");
        return BitConverter.ToUInt32(buf, 0);
    }

    private const uint CONTEXT_DEBUG_REGISTERS = 0x00010010;   // CONTEXT_i386 | 0x10
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

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadProcessMemory(nint hProcess, nint lpBaseAddress, byte[] lpBuffer, nuint nSize, out nuint lpNumberOfBytesRead);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetThreadContext(nint hThread, ref CONTEXT_X86 lpContext);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadContext(nint hThread, ref CONTEXT_X86 lpContext);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(nint hProcess, int processInformationClass,
        ref PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);

    // x86 CONTEXT is 716 (0x2CC) bytes; we only touch ContextFlags, Dr0 and Dr7. Explicit
    // layout with the full size lets Get/SetThreadContext marshal the whole structure while
    // we address just the debug-register fields.
    [StructLayout(LayoutKind.Explicit, Size = 0x2CC)]
    private struct CONTEXT_X86
    {
        [FieldOffset(0x00)] public uint ContextFlags;
        [FieldOffset(0x04)] public uint Dr0;
        [FieldOffset(0x18)] public uint Dr7;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public nint ExitStatus;
        public nint PebBaseAddress;
        public nint AffinityMask;
        public nint BasePriority;
        public nint UniqueProcessId;
        public nint InheritedFromUniqueProcessId;
    }

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
