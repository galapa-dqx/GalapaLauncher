namespace Talon.Vfs;

// Maps game-relative VFS paths to files that remain below one override root.
internal sealed class LooseFileAssetProvider
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
        if (string.IsNullOrWhiteSpace(gamePath) || gamePath.Contains(':'))
            return false;

        try
        {
            if (Path.IsPathRooted(gamePath))
                return false;

            var relative = gamePath.Replace('/', Path.DirectorySeparatorChar);
            var candidate = Path.GetFullPath(Path.Combine(rootWithSeparator, relative));
            if (!candidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(candidate))
                return false;

            filePath = candidate;
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }
}
