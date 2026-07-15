using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Galapa.Core.Configuration;

namespace Galapa.Core.Game;

public partial class GameProcess(Settings settings)
{
    private const uint CREATE_SUSPENDED = 0x00000004;

    private static readonly char[] SqEx = "SqEx".ToCharArray();
    private Process? _process;
    private nint _suspendedThreadHandle;

    public string? SessionId { get; set; }

    public int? PlayerNumber { get; set; }

    // I don't know what the fuck this means and it's not in DQXGame.exe at all
    // But I'm scared to remove it
    public bool UseApartmentThreaded { get; set; } = true;

    public bool HasExited => this._process?.HasExited ?? true;

    public event EventHandler? Exited;

    public void Start()
    {
        if (this.SessionId is null) throw new InvalidOperationException("SessionId is null");
        if (settings.GameFolderPath is null) throw new InvalidOperationException("GameFolderPath is null");

        var gamePath = Path.Combine(settings.GameFolderPath, "game", "DQXGame.exe");

        this._process = new Process();
        this._process.StartInfo.WorkingDirectory = Path.Combine(settings.GameFolderPath, "game");
        this._process.StartInfo.UseShellExecute = false;
        this._process.StartInfo.FileName = gamePath;
        this._process.StartInfo.Arguments = this.GetArguments();
        this._process.EnableRaisingEvents = true;
        this._process.Exited += this.OnProcessExited;
        this._process.Start();
    }

    /// <summary>
    ///     Starts the game process in a suspended state for debugger attachment.
    ///     Call <see cref="Resume" /> after attaching a debugger to continue execution.
    /// </summary>
    public void StartSuspended()
    {
        if (this.SessionId is null) throw new InvalidOperationException("SessionId is null");
        if (settings.GameFolderPath is null) throw new InvalidOperationException("GameFolderPath is null");

        var gamePath = Path.Combine(settings.GameFolderPath, "game", "DQXGame.exe");
        var workingDir = Path.Combine(settings.GameFolderPath, "game");
        var commandLine = $"\"{gamePath}\" {this.GetArguments()}";

        var startupInfo = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };

        var success = CreateProcess(
            null,
            commandLine,
            nint.Zero,
            nint.Zero,
            false,
            CREATE_SUSPENDED,
            nint.Zero,
            workingDir,
            ref startupInfo,
            out var processInfo);

        if (!success)
        {
            var error = Marshal.GetLastWin32Error();
            throw new InvalidOperationException($"Failed to create suspended process. Error code: {error}");
        }

        this._suspendedThreadHandle = processInfo.hThread;
        this._process = Process.GetProcessById((int)processInfo.dwProcessId);
        this._process.EnableRaisingEvents = true;
        this._process.Exited += this.OnProcessExited;

        // Close the process handle since we have a Process object now
        CloseHandle(processInfo.hProcess);
    }

    /// <summary>
    ///     Resumes a process that was started with <see cref="StartSuspended" />.
    /// </summary>
    public void Resume()
    {
        if (this._suspendedThreadHandle == nint.Zero)
            throw new InvalidOperationException("No suspended thread to resume. Did you call StartSuspended?");

        ResumeThread(this._suspendedThreadHandle);
        CloseHandle(this._suspendedThreadHandle);
        this._suspendedThreadHandle = nint.Zero;
    }

    /// <summary>
    ///     Gets whether the process is currently suspended.
    /// </summary>
    public bool IsSuspended => this._suspendedThreadHandle != nint.Zero;

    /// <summary>
    ///     Gets the process ID if the process has been started.
    /// </summary>
    public int? ProcessId => this._process?.Id;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
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

    [LibraryImport("kernel32.dll")]
    private static partial uint ResumeThread(nint hThread);

    [LibraryImport("kernel32.dll")]
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

    public async Task WaitForExitAsync()
    {
        if (this._process is null) return;
        await this._process.WaitForExitAsync();
    }

    public void Kill()
    {
        if (this._process is null || this._process.HasExited) return;
        this._process.Kill();
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        this.Exited?.Invoke(this, EventArgs.Empty);
    }

    private string GetArguments()
    {
        var args = new StringBuilder();

        args.Append($"-StartupToken={GetStartupToken()} ");
        if (this.SessionId is not null) args.Append($"-SessionID={this.EncodeSessionId(this.SessionId)} ");
        if (this.PlayerNumber is not null) args.Append($"-PlayerNumber={this.PlayerNumber} ");
        args.Append("-USE_APARTMENTTHREADED");

        return args.ToString();
    }

    private string EncodeSessionId(string sid)
    {
        if (!this.IsValidHex(sid)) throw new ArgumentException("Input must be a 56-character hex string.");

        var timeStr = (DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60).ToString();

        var input = $"DQUEST10{sid}";
        var md5 = MD5.Create().ComputeHash(Encoding.UTF8.GetBytes($"{timeStr}DraqonQuestX"));

        var output = new byte[64];
        // Encoding loop
        for (var i = 0; i < 64; i++)
        {
            int ecx = md5[i % 16];
            var eax = i < input.Length ? input[i] : 0;
            ecx -= 48;
            eax += ecx;
            eax %= 78;
            eax += 48;
            output[i] = (byte)eax;
        }

        return Encoding.UTF8.GetString(output, 0, 64);
    }

    [DllImport("winmm.dll", EntryPoint = "timeGetTime")]
    private static extern uint GetTime();

    private static string GetStartupToken()
    {
        // The official version of this function actually uses an MT RNG seeded from the Windows "true" RNG to generate
        // these 4 chars, but because that makes it *actually random*, they can't check it and we just stuff 0000 in.
        var baseString = "0000" + (GetTime() >>> 1);

        return new string(baseString
            .ToCharArray()
            .Select((person, index) => (char)(person ^ SqEx[index & 3]))
            .ToArray());
    }

    private bool IsValidHex(string str)
    {
        return Regex.IsMatch(str, "^[0-9a-fA-F]{56}$");
    }
}