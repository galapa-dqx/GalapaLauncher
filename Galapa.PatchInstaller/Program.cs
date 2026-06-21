using Galapa.Core.Patcher;
using Galapa.Core.Patcher.ZiPatch;

namespace Galapa.PatchInstaller;

/// <summary>
/// CLI front-end for the DQX ZiPatch applier in <see cref="Galapa.Core.Patcher"/>.
/// Modelled on XIVLauncher.PatchInstaller, trimmed to what DQX needs.
///
/// Commands:
///   install &lt;game-dir&gt; &lt;patch...&gt;      apply patch(es), in order, into game-dir
///   list    &lt;patch&gt;                     print the chunk stream of a patch
///   verify  &lt;patch&gt; &lt;expected-dir&gt; [base-dir]
///                                        apply patch to a temp dir (optionally seeded
///                                        from base-dir) and diff against expected-dir
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            PrintUsage();
            return 2;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "install" => Install(args[1..]),
                "list" => List(args[1..]),
                "verify" => Verify(args[1..]),
                "-h" or "--help" or "help" => PrintUsageAnd(0),
                _ => Fail($"Unknown command '{args[0]}'."),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }

    private static int Install(string[] args)
    {
        if (args.Length < 2)
            return Fail("usage: install <game-dir> <patch...>");

        var gameDir = args[0];
        var patches = args[1..];

        foreach (var patch in patches)
        {
            var fi = new FileInfo(patch);
            if (!fi.Exists)
                return Fail($"patch not found: {patch}");
            if (fi.Length == 0)
                return Fail($"patch is empty: {patch}");
        }

        Directory.CreateDirectory(gameDir);

        foreach (var patch in patches)
        {
            Console.Error.WriteLine($"[install] {patch} -> {gameDir}");
            var applied = 0;
            ZiPatchInstaller.InstallPatch(patch, gameDir, _ => applied++);
            Console.Error.WriteLine($"[install] {Path.GetFileName(patch)}: {applied} chunk(s) applied");
        }

        Console.Error.WriteLine("[install] done");
        return 0;
    }

    private static int List(string[] args)
    {
        if (args.Length != 1)
            return Fail("usage: list <patch>");

        using var patch = ZiPatchFile.FromFileName(args[0]);
        var index = 0;
        foreach (var chunk in patch.GetChunks())
            Console.WriteLine($"{index++,4}  {chunk}");

        return 0;
    }

    private static int Verify(string[] args)
    {
        if (args.Length is < 2 or > 3)
            return Fail("usage: verify <patch> <expected-dir> [base-dir]");

        var patch = args[0];
        var expectedDir = args[1];
        var baseDir = args.Length == 3 ? args[2] : null;

        var workDir = Path.Combine(Path.GetTempPath(), "galapa-verify-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(workDir);
            if (baseDir != null)
                CopyDirectory(baseDir, workDir);

            Console.Error.WriteLine($"[verify] applying {Path.GetFileName(patch)} -> (temp){(baseDir != null ? " seeded from base" : string.Empty)}");
            ZiPatchInstaller.InstallPatch(patch, workDir);

            var diffs = DirectoryComparer.Compare(expectedDir, workDir).ToList();
            if (diffs.Count == 0)
            {
                var n = Directory.EnumerateFiles(workDir, "*", SearchOption.AllDirectories).Count(p => !DirectoryComparer.IsIgnored(p));
                Console.WriteLine($"OK — output matches {expectedDir} ({n} file(s), byte-for-byte).");
                return 0;
            }

            Console.WriteLine($"MISMATCH — {diffs.Count} difference(s) vs {expectedDir}:");
            foreach (var d in diffs)
                Console.WriteLine($"  {d}");
            return 1;
        }
        finally
        {
            try { Directory.Delete(workDir, true); } catch { /* best effort */ }
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        foreach (var dir in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, true);
        }
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 2;
    }

    private static int PrintUsageAnd(int code)
    {
        PrintUsage();
        return code;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine(
            """
            Galapa.PatchInstaller — applies Dragon Quest X ZiPatch (.patch) files.

            usage:
              install <game-dir> <patch...>            apply patch(es), in order, into game-dir
              list    <patch>                          print the chunk stream of a patch
              verify  <patch> <expected-dir> [base-dir]
                                                       apply patch to a temp dir (optionally
                                                       seeded from base-dir) and diff against
                                                       expected-dir (e.g. the oracle output)
            """);
    }
}
