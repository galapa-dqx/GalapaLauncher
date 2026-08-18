using System;
using System.IO;
using System.Runtime.InteropServices;
using Galapa.Core.Game;

namespace Galapa.Toolbox;

/// <summary>
///     Minimal headless command-line interface for the Toolbox. <see cref="Program.Main" /> runs
///     this instead of the Avalonia GUI when the first argument is a recognised command
///     (see <see cref="IsCommand" />); with no command, the GUI launches as before.
/// </summary>
internal static class Cli
{
    /// <summary>Whether <paramref name="arg" /> is a CLI command (so we should skip the GUI).</summary>
    public static bool IsCommand(string arg) => arg.ToLowerInvariant() switch
    {
        "token" or "help" or "-h" or "--help" => true,
        _ => false,
    };

    public static int Run(string[] args)
    {
        // Toolbox is a WinExe, so it has no console of its own. Best-effort attach to the parent
        // console so output is visible when run interactively. When stdout is redirected (e.g. a
        // script capturing the token) the std handle is already set and this is a harmless no-op.
        AttachConsole(ATTACH_PARENT_PROCESS);

        switch (args[0].ToLowerInvariant())
        {
            case "token":
                // A fresh DQX -StartupToken on its own line, for splicing into a launch command.
                Console.Out.WriteLine(GameProcess.GenerateStartupToken());
                return 0;

            case "help" or "-h" or "--help":
                PrintUsage(Console.Out);
                return 0;

            default:
                PrintUsage(Console.Error);
                return 2;
        }
    }

    private static void PrintUsage(TextWriter w)
    {
        w.WriteLine("Galapa.Toolbox — headless CLI");
        w.WriteLine("usage: Galapa.Toolbox.exe <command>");
        w.WriteLine();
        w.WriteLine("commands:");
        w.WriteLine("  token     print a freshly generated DQX -StartupToken value");
        w.WriteLine("  help      show this help");
        w.WriteLine();
        w.WriteLine("With no command, the graphical Toolbox launches.");
    }

    private const uint ATTACH_PARENT_PROCESS = 0xFFFFFFFF;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint dwProcessId);
}
