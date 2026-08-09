using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Talon.Vfs;

// Opens game-relative override files and verifies their final on-disk paths.
internal sealed partial class LooseFileAssetProvider
{
    private readonly string rootWithSeparator;

    public LooseFileAssetProvider(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        rootWithSeparator = Path.EndsInDirectorySeparator(fullRoot)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
    }

    public bool TryResolve(string gamePath, out string filePath)
    {
        filePath = string.Empty;
        if (!TryOpen(gamePath, out var stream)) return false;
        using (stream) filePath = stream.Name;
        return true;
    }

    public bool TryOpen(string gamePath, out FileStream stream)
    {
        stream = null!;
        if (string.IsNullOrWhiteSpace(gamePath) || gamePath.Contains(':')) return false;

        FileStream? opened = null;
        try
        {
            if (Path.IsPathRooted(gamePath)) return false;
            var relative = gamePath.Replace('/', Path.DirectorySeparatorChar);
            var candidate = Path.GetFullPath(Path.Combine(rootWithSeparator, relative));
            if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                return false;

            opened = new FileStream(
                candidate,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.SequentialScan);
            using var rootHandle = CreateFile(
                Path.TrimEndingDirectorySeparator(rootWithSeparator),
                0,
                FileShareRead | FileShareWrite | FileShareDelete,
                0,
                OpenExisting,
                FileFlagBackupSemantics,
                0);
            if (rootHandle.IsInvalid) return false;

            var finalRoot = EnsureTrailingSeparator(GetFinalPath(rootHandle));
            var finalFile = GetFinalPath(opened.SafeFileHandle);
            if (!finalFile.StartsWith(finalRoot, StringComparison.OrdinalIgnoreCase))
                return false;

            stream = opened;
            opened = null;
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException or
            NotSupportedException)
        {
            return false;
        }
        finally { opened?.Dispose(); }
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        var buffer = new char[32768];
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Length, 0);
        if (length == 0 || length >= buffer.Length)
            throw new IOException(
                $"GetFinalPathNameByHandle failed ({Marshal.GetLastWin32Error()}).");
        var path = new string(buffer, 0, checked((int)length));
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[8..];
        return path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)
            ? path[4..]
            : path;
    }

    private static string EnsureTrailingSeparator(string path) =>
        Path.EndsInDirectorySeparator(path) ? path : path + Path.DirectorySeparatorChar;

    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint FileShareDelete = 0x4;
    private const uint OpenExisting = 3;
    private const uint FileFlagBackupSemantics = 0x02000000;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        [Out] char[] path,
        uint pathLength,
        uint flags);
}
