using System.Security.Cryptography;

namespace Galapa.Core.Patcher;

/// <summary>
/// Byte-for-byte comparison of an applied install against a reference tree. Used to
/// diff our patcher's output against the DQX updater oracle (and usable by the
/// launcher to verify an install). Housekeeping files that aren't part of the game
/// (<c>.DS_Store</c>, <c>Thumbs.db</c>, the updater's <c>__deletelist.xml</c>) are
/// ignored by default.
/// </summary>
public static class DirectoryComparer
{
    private static readonly string[] IgnoredNames = [".DS_Store", "Thumbs.db", "__deletelist.xml"];

    public static bool IsIgnored(string path) =>
        IgnoredNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns a human-readable line for every difference between
    /// <paramref name="expectedDir"/> and <paramref name="actualDir"/>. An empty
    /// sequence means the trees are identical (ignoring housekeeping files).
    /// </summary>
    public static IEnumerable<string> Compare(string expectedDir, string actualDir)
    {
        var expected = RelativeFiles(expectedDir);
        var actual = RelativeFiles(actualDir);

        foreach (var rel in expected.Keys.Union(actual.Keys, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var inExpected = expected.TryGetValue(rel, out var expPath);
            var inActual = actual.TryGetValue(rel, out var actPath);

            if (inExpected && !inActual)
            {
                yield return $"missing: {rel}";
            }
            else if (!inExpected && inActual)
            {
                yield return $"unexpected: {rel}";
            }
            else
            {
                var expLen = new FileInfo(expPath!).Length;
                var actLen = new FileInfo(actPath!).Length;
                if (expLen != actLen)
                    yield return $"size differs: {rel} (expected {expLen}, got {actLen})";
                else if (!Hash(expPath!).SequenceEqual(Hash(actPath!)))
                    yield return $"content differs: {rel} ({expLen} bytes)";
            }
        }
    }

    private static Dictionary<string, string> RelativeFiles(string root)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(root))
            return map;

        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            if (IsIgnored(path))
                continue;

            // Normalise separators so a Windows-produced tree compares to a *nix one.
            var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
            map[rel] = path;
        }

        return map;
    }

    private static byte[] Hash(string path)
    {
        using var stream = File.OpenRead(path);
        return SHA256.HashData(stream);
    }
}
