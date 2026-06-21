namespace Galapa.Core.Patcher.ZiPatch.Util;

/// <summary>
/// A file inside the game install, addressed by a path relative to the game root.
/// SqpkFile (the 'F' command) targets these directly with a verbatim path; the
/// SqPack-triple commands (AddData/Header/Index) derive their path via
/// <see cref="SqpackFile"/>.
/// </summary>
public class SqexFile
{
    public string RelativePath { get; set; } = string.Empty;

    protected SqexFile() { }

    public SqexFile(string relativePath)
    {
        RelativePath = relativePath;
    }

    public SqexFileStream OpenStream(string basePath, FileMode mode, int tries = 5, int sleeptime = 1) =>
        SqexFileStream.WaitForStream($@"{basePath}/{RelativePath}", mode, tries, sleeptime);

    public SqexFileStream OpenStream(SqexFileStreamStore store, string basePath, FileMode mode,
                                     int tries = 5, int sleeptime = 1) =>
        store.GetStream($@"{basePath}/{RelativePath}", mode, tries, sleeptime);

    public void Delete(SqexFileStreamStore? store, string basePath, int tries = 5, int sleeptime = 1)
    {
        var path = $"{basePath}/{RelativePath}";

        while (File.Exists(path))
        {
            store?.CloseStream($"{basePath}/{RelativePath}");

            try
            {
                File.Delete(path);
            }
            catch (IOException ioe)
            {
                if (ioe is FileNotFoundException or DirectoryNotFoundException)
                    break;

                if (tries-- <= 0)
                    throw;

                Thread.Sleep(sleeptime * 1000);
            }
        }
    }

    public void CreateDirectoryTree(string basePath)
    {
        var dirName = System.IO.Path.GetDirectoryName($@"{basePath}/{RelativePath}");
        if (dirName != null)
            Directory.CreateDirectory(dirName);
    }

    public override string ToString() => RelativePath;

    // NOTE: the expansion-folder layout below mirrors FFXIV (ffxiv/exN under
    // sqpack/ and movie/). DQX lays its content out differently (Game/Content/Data,
    // Ex2000..Ex7000), but none of the available DQX sample patches contain a
    // SqpkFile 'R' (RemoveAll) command, so this path has never been exercised
    // against ground truth. Revisit if a patch that uses 'F'/'R' turns up.
    public static string GetExpansionFolder(byte expansionId) =>
        expansionId == 0 ? "ffxiv" : $"ex{expansionId}";

    public static IEnumerable<string> GetAllExpansionFiles(string fullPath, ushort expansionId)
    {
        var xpacPath = GetExpansionFolder((byte)expansionId);

        var sqpack = $@"{fullPath}\sqpack\{xpacPath}";
        var movie = $@"{fullPath}\movie\{xpacPath}";

        var files = Enumerable.Empty<string>();

        if (Directory.Exists(sqpack))
            files = files.Concat(Directory.GetFiles(sqpack));

        if (Directory.Exists(movie))
            files = files.Concat(Directory.GetFiles(movie));

        return files;
    }
}
