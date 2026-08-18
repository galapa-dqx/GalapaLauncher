using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Talon.Vfs;

// Opens game-relative override files and verifies their final on-disk paths.
internal sealed partial class LooseFileAssetProvider
{
    private readonly string rootWithSeparator;
    private readonly string canonicalRootWithSeparator;

    public LooseFileAssetProvider(string root)
    {
        var fullRoot = Path.GetFullPath(root);
        rootWithSeparator = Path.EndsInDirectorySeparator(fullRoot)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;
        using var rootHandle = CreateFile(
            fullRoot,
            0,
            FileShareRead | FileShareWrite | FileShareDelete,
            0,
            OpenExisting,
            FileFlagBackupSemantics,
            0);
        if (rootHandle.IsInvalid)
            throw new DirectoryNotFoundException($"Override root is unavailable: {fullRoot}");
        canonicalRootWithSeparator = EnsureTrailingSeparator(GetFinalPath(rootHandle));
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
        if (!IsSafeGamePath(gamePath)) return false;

        SafeFileHandle? openedHandle = null;
        FileStream? opened = null;
        try
        {
            if (Path.IsPathRooted(gamePath)) return false;
            var relative = gamePath.Replace('/', Path.DirectorySeparatorChar);
            var candidate = Path.GetFullPath(Path.Combine(rootWithSeparator, relative));
            if (!IsPathWithinRoot(candidate, rootWithSeparator))
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
            var finalFile = GetFinalPath(opened.SafeFileHandle);
            if (!IsPathWithinRoot(finalFile, canonicalRootWithSeparator))
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

    // Validate before CreateFile so Win32 cannot reinterpret a game path as a
    // DOS device, alternate data stream, or normalized traversal component.
    internal static bool IsSafeGamePath(string gamePath)
    {
        if (string.IsNullOrWhiteSpace(gamePath) || Path.IsPathRooted(gamePath)) return false;
        foreach (var component in gamePath.Split(['/', '\\']))
        {
            if (component.Length == 0 || component is "." or ".." ||
                component.EndsWith(' ') || component.EndsWith('.') ||
                component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return false;

            var stem = component.Split('.')[0].TrimEnd(' ', '.');
            if (IsDosDeviceName(stem)) return false;
        }
        return true;
    }

    private static bool IsDosDeviceName(string stem)
    {
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase))
            return true;

        if (stem.Length == 4 &&
            (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
             stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)))
            return stem[3] is >= '1' and <= '9' or '\u00B9' or '\u00B2' or '\u00B3';
        return false;
    }

    internal static bool IsPathWithinRoot(string path, string rootWithSeparator) =>
        path.StartsWith(rootWithSeparator, StringComparison.Ordinal);

    private static unsafe string GetFinalPath(SafeFileHandle handle)
    {
        var required = GetFinalPathNameByHandle(handle, null, 0, 0);
        if (required == 0)
            throw new IOException(
                $"GetFinalPathNameByHandle failed ({Marshal.GetLastWin32Error()}).");
        var buffer = new char[required];
        uint length;
        fixed (char* bufferPointer = buffer)
            length = GetFinalPathNameByHandle(handle, bufferPointer, (uint)buffer.Length, 0);
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
    private static unsafe partial uint GetFinalPathNameByHandle(
        SafeFileHandle file,
        char* path,
        uint pathLength,
        uint flags);
}
