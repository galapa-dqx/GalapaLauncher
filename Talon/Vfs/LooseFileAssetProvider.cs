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
        using (stream) filePath = GetFinalPath(stream.SafeFileHandle);
        return true;
    }

    public bool TryOpen(string gamePath, out FileStream stream)
    {
        stream = null!;
        if (string.IsNullOrWhiteSpace(gamePath) || gamePath.Contains(':')) return false;

        SafeFileHandle? openedHandle = null;
        FileStream? opened = null;
        try
        {
            if (Path.IsPathRooted(gamePath)) return false;
            var relative = gamePath.Replace('/', Path.DirectorySeparatorChar);
            var candidate = Path.GetFullPath(Path.Combine(rootWithSeparator, relative));
            if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                return false;

            openedHandle = CreateFile(
                candidate,
                GenericRead,
                FileShareRead,
                0,
                OpenExisting,
                FileAttributeNormal | FileFlagSequentialScan,
                0);
            if (openedHandle.IsInvalid) return false;

            // Construct the stream from the handle we validate below. Missing
            // overrides are the normal case, so CreateFile's invalid-handle result
            // avoids allocating and catching a FileNotFoundException per VFS read.
            opened = new FileStream(openedHandle, FileAccess.Read, 4096, isAsync: false);
            openedHandle = null; // FileStream now owns the SafeFileHandle.
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
        finally
        {
            opened?.Dispose();
            openedHandle?.Dispose();
        }
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

    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint FileShareDelete = 0x4;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagSequentialScan = 0x08000000;
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
