using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

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
    public readonly record struct InjectResult(int ProcessId, nint ProcessHandle, nint ThreadHandle) : IDisposable
    {
        public void Dispose()
        {
            if (ThreadHandle != nint.Zero) CloseHandle(ThreadHandle);
            if (ProcessHandle != nint.Zero) CloseHandle(ProcessHandle);
        }
    }

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
    public static InjectResult LaunchAndInject(
        string gameCommandLine,
        string workingDir,
        string bootDllPath,
        string startInfoJson)
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
            // 1. Resolve the functions used by the target-side bootstrap thunk.
            // kernel32 is ASLR-randomized once per boot with a
            //    base shared across same-bitness processes, so this address is valid in
            //    the target too — which is why the injector must be x86.
            var kernel32 = GetModuleHandle("kernel32.dll");
            if (kernel32 == nint.Zero)
                throw new InvalidOperationException("GetModuleHandle(kernel32.dll) returned null.");
            var loadLibraryW = GetProcAddress(kernel32, "LoadLibraryW");
            if (loadLibraryW == nint.Zero)
                throw new InvalidOperationException("GetProcAddress(LoadLibraryW) returned null.");
            var getProcAddress = GetProcAddress(kernel32, "GetProcAddress");
            if (getProcAddress == nint.Zero)
                throw new InvalidOperationException("GetProcAddress(GetProcAddress) returned null.");
            var exitProcess = GetProcAddress(kernel32, "ExitProcess");
            if (exitProcess == nint.Zero)
                throw new InvalidOperationException("GetProcAddress(ExitProcess) returned null.");

            // Keep all Talon startup state in one remote allocation. Data occupies RW
            // pages and the generated x86 thunk occupies its own RX page.
            var pathBytes = Encoding.Unicode.GetBytes(bootDllPath + '\0');
            var jsonBytes = Encoding.UTF8.GetBytes(startInfoJson + '\0');
            var exportBytes = Encoding.ASCII.GetBytes("TalonInitialize\0");
            var jsonOffset = Align(pathBytes.Length, 4);
            var exportOffset = Align(jsonOffset + jsonBytes.Length, 4);
            var codeOffset = Align(exportOffset + exportBytes.Length, 0x1000);
            const int thunkLength = 68;
            var allocationLength = codeOffset + thunkLength;

            var remoteBase = VirtualAllocEx(pi.hProcess, nint.Zero, (nuint)allocationLength,
                MEM_COMMIT | MEM_RESERVE, PAGE_READWRITE);
            if (remoteBase == nint.Zero)
                throw new InvalidOperationException($"VirtualAllocEx failed (Win32 error {Marshal.GetLastWin32Error()}).");
            if ((ulong)remoteBase.ToInt64() > uint.MaxValue)
                throw new InvalidOperationException("Remote bootstrap allocation is outside the x86 address space.");

            var block = new byte[allocationLength];
            pathBytes.CopyTo(block, 0);
            jsonBytes.CopyTo(block, jsonOffset);
            exportBytes.CopyTo(block, exportOffset);

            var baseAddress = checked((uint)remoteBase.ToInt64());
            var thunk = BuildBootstrapThunk(
                baseAddress,
                checked(baseAddress + (uint)jsonOffset),
                checked(baseAddress + (uint)exportOffset),
                checked((uint)loadLibraryW.ToInt64()),
                checked((uint)getProcAddress.ToInt64()),
                checked((uint)exitProcess.ToInt64()));
            thunk.CopyTo(block, codeOffset);

            if (!WriteProcessMemory(pi.hProcess, remoteBase, block, (nuint)block.Length, out var written) ||
                written != (nuint)block.Length)
                throw new InvalidOperationException($"WriteProcessMemory failed (Win32 error {Marshal.GetLastWin32Error()}).");

            var remoteThunk = remoteBase + codeOffset;
            if (!VirtualProtectEx(pi.hProcess, remoteThunk, (nuint)thunkLength,
                    PAGE_EXECUTE_READ, out _))
                throw new InvalidOperationException($"VirtualProtectEx failed (Win32 error {Marshal.GetLastWin32Error()}).");
            if (!FlushInstructionCache(pi.hProcess, remoteThunk, (nuint)thunkLength))
                throw new InvalidOperationException($"FlushInstructionCache failed (Win32 error {Marshal.GetLastWin32Error()}).");

            // 3. Arm the deterministic post-APC rendezvous for every Talon launch.
            //    Managed VFS and network hooks both initialize after unpacking, so an
            //    empty override directory is no longer a no-op launch.
            ArmEntrypointRendezvous(pi.hProcess, pi.hThread);

            // 4. Queue the target-side thunk onto the primary thread, then resume.
            if (QueueUserAPC(remoteThunk, pi.hThread, nint.Zero) == 0)
                throw new InvalidOperationException($"QueueUserAPC failed (Win32 error {Marshal.GetLastWin32Error()}).");

            if (ResumeThread(pi.hThread) == unchecked((uint)-1))
                throw new InvalidOperationException($"ResumeThread failed (Win32 error {Marshal.GetLastWin32Error()}).");

            // The allocation intentionally remains valid because Boot retains the JSON
            // pointer for its asynchronous managed-runtime worker.
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

    internal static byte[] BuildBootstrapThunk(
        uint remoteDllPath,
        uint remoteStartInfoJson,
        uint remoteExportName,
        uint loadLibraryW,
        uint getProcAddress,
        uint exitProcess)
    {
        var code = new byte[68];
        var i = 0;

        void Byte(byte value) => code[i++] = value;
        void UInt32(uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(code.AsSpan(i, sizeof(uint)), value);
            i += sizeof(uint);
        }

        Byte(0x55);
        Byte(0x8B); Byte(0xEC);
        Byte(0x68); UInt32(remoteDllPath);
        Byte(0xB8); UInt32(loadLibraryW);
        Byte(0xFF); Byte(0xD0);
        Byte(0x85); Byte(0xC0);
        Byte(0x74); var loadFailureBranch = i; Byte(0x00);
        Byte(0x68); UInt32(remoteExportName);
        Byte(0x50);
        Byte(0xB8); UInt32(getProcAddress);
        Byte(0xFF); Byte(0xD0);
        Byte(0x85); Byte(0xC0);
        Byte(0x74); var exportFailureBranch = i; Byte(0x00);
        Byte(0x68); UInt32(remoteStartInfoJson);
        Byte(0xFF); Byte(0xD0);
        Byte(0x83); Byte(0xC4); Byte(0x04);
        Byte(0x85); Byte(0xC0);
        Byte(0x75); var initializeFailureBranch = i; Byte(0x00);
        Byte(0x5D);
        Byte(0xC2); Byte(0x04); Byte(0x00);

        var failure = i;
        // TalonInitialize returns a Win32 status. Earlier failures leave EAX at
        // zero, so turn that into a nonzero process exit code before failing shut.
        Byte(0x85); Byte(0xC0);
        Byte(0x75); Byte(0x01);
        Byte(0x40);
        Byte(0x50);
        Byte(0xB8); UInt32(exitProcess);
        Byte(0xFF); Byte(0xD0);
        Byte(0xCC);

        PatchShortForwardBranch(loadFailureBranch, failure);
        PatchShortForwardBranch(exportFailureBranch, failure);
        PatchShortForwardBranch(initializeFailureBranch, failure);

        if (i != code.Length)
            throw new InvalidOperationException($"Internal bootstrap thunk length mismatch: {i}.");
        return code;

        void PatchShortForwardBranch(int operand, int target)
        {
            var displacement = target - (operand + 1);
            code[operand] = checked((byte)displacement);
        }
    }

    private static int Align(int value, int alignment) =>
        checked((value + alignment - 1) & ~(alignment - 1));

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

    private static void ArmEntrypointRendezvous(nint process, nint thread)
    {
        var pbi = new PROCESS_BASIC_INFORMATION();
        var status = NtQueryInformationProcess(process, 0, ref pbi,
            Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _);
        if (status != 0 || pbi.PebBaseAddress == nint.Zero)
            throw new InvalidOperationException(
                $"NtQueryInformationProcess failed (NTSTATUS 0x{status:X8}).");

        var peb = ReadRemote(process, pbi.PebBaseAddress + 0x08, 4, "PEB.ImageBaseAddress");
        var imageBase = BitConverter.ToUInt32(peb, 0);
        var dos = ReadRemote(process, (nint)imageBase, 0x40, "DOS header");
        if (BitConverter.ToUInt16(dos, 0) != 0x5A4D)
            throw new BadImageFormatException("Target's mapped image has no MZ header.");

        var peOffset = BitConverter.ToInt32(dos, 0x3C);
        if (peOffset < 0x40 || peOffset > 0x100000 ||
            (uint)peOffset > uint.MaxValue - imageBase)
            throw new BadImageFormatException($"Target's mapped e_lfanew is invalid: 0x{peOffset:X}.");

        var nt = ReadRemote(process, (nint)(imageBase + (uint)peOffset), 0x60, "NT headers");
        if (BitConverter.ToUInt32(nt, 0) != 0x00004550 ||
            BitConverter.ToUInt16(nt, 4) != IMAGE_FILE_MACHINE_I386 ||
            BitConverter.ToUInt16(nt, 24) != 0x010B)
            throw new BadImageFormatException("Target's mapped image is not a valid x86 PE32 image.");

        var entryRva = BitConverter.ToUInt32(nt, 24 + 16);
        var sizeOfImage = BitConverter.ToUInt32(nt, 24 + 56);
        if (entryRva == 0 || entryRva >= sizeOfImage || entryRva > uint.MaxValue - imageBase)
            throw new BadImageFormatException(
                $"Target's mapped entrypoint RVA is invalid: 0x{entryRva:X}.");

        var target = imageBase + entryRva;
        var context = new CONTEXT_X86 { ContextFlags = CONTEXT_DEBUG_REGISTERS };
        if (!GetThreadContext(thread, ref context))
            throw new InvalidOperationException(
                $"GetThreadContext failed (Win32 error {Marshal.GetLastWin32Error()}).");

        context.Dr0 = target;
        context.Dr7 = (context.Dr7 & ~0x000F0003u) | 1u;
        context.ContextFlags = CONTEXT_DEBUG_REGISTERS;
        if (!SetThreadContext(thread, ref context))
            throw new InvalidOperationException(
                $"SetThreadContext failed (Win32 error {Marshal.GetLastWin32Error()}).");

        Console.WriteLine($"[talon] entry rendezvous: 0x{target:X8} " +
            $"(imageBase 0x{imageBase:X8} + RVA 0x{entryRva:X})");
    }

    private static byte[] ReadRemote(nint process, nint address, int size, string description)
    {
        var bytes = new byte[size];
        if (!ReadProcessMemory(process, address, bytes, (nuint)size, out var read) ||
            read != (nuint)size)
            throw new InvalidOperationException(
                $"ReadProcessMemory({description}) failed (Win32 error {Marshal.GetLastWin32Error()}).");
        return bytes;
    }

    private const uint CONTEXT_DEBUG_REGISTERS = 0x00010010;
    private const uint CREATE_SUSPENDED = 0x00000004;
    private const uint MEM_COMMIT = 0x1000;
    private const uint MEM_RESERVE = 0x2000;
    private const uint PAGE_READWRITE = 0x04;
    private const uint PAGE_EXECUTE_READ = 0x20;
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
    private static partial bool VirtualProtectEx(
        nint hProcess,
        nint lpAddress,
        nuint dwSize,
        uint flNewProtect,
        out uint lpflOldProtect);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteProcessMemory(nint hProcess, nint lpBaseAddress, byte[] lpBuffer, nuint nSize, out nuint lpNumberOfBytesWritten);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlushInstructionCache(
        nint hProcess,
        nint lpBaseAddress,
        nuint dwSize);

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
    private static partial bool ReadProcessMemory(nint hProcess, nint lpBaseAddress,
        byte[] lpBuffer, nuint nSize, out nuint lpNumberOfBytesRead);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetThreadContext(nint hThread, ref CONTEXT_X86 lpContext);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetThreadContext(nint hThread, ref CONTEXT_X86 lpContext);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(nint process, int informationClass,
        ref PROCESS_BASIC_INFORMATION information, int informationLength, out int returnLength);

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
