using Talon.Injector;

// Talon.Injector — Phase 0 CLI.
//
// Usage:
//   Talon.Injector.exe [--working-dir <dir>] [--boot-dll <path>]
//                      [--override-dir <dir>] -- <game command line...>
//
// Everything after the "--" marker is the target's command line, taken VERBATIM from the
// raw process command line (not the parsed argv) and passed straight to CreateProcessW.
// This is deliberate: DQX's -StartupToken can contain spaces/quotes, and round-tripping
// through a split-then-rejoined argv would corrupt it. See the plan's handoff section.
//
// --override-dir points Talon.Boot at a folder of loose files to serve in place of the
// game's packed .dat archive contents. Talon.Boot hooks the game's own VFS resource
// loader, so it needs only this folder — the game reads its .idx/.dat archives itself.
// The folder is handed to the injected DLL through the TALON_OVERRIDE_DIR environment
// variable: we set it here and the launched game inherits our environment (CreateProcessW
// is called with a null environment block).

const string marker = " -- ";
var raw = Environment.CommandLine;

var markerIndex = raw.IndexOf(marker, StringComparison.Ordinal);
if (markerIndex < 0)
{
    Console.Error.WriteLine("error: missing '--' separator; nothing to launch.");
    PrintUsage();
    return 2;
}

// Raw, verbatim game command line (everything after the first " -- ").
var gameCommandLine = raw[(markerIndex + marker.Length)..].TrimStart();
if (gameCommandLine.Length == 0)
{
    Console.Error.WriteLine("error: empty game command line after '--'.");
    PrintUsage();
    return 2;
}

// Injector's own flags live in the parsed argv, before the "--" element. These are simple
// paths, safe to take from argv.
var injectorArgs = new List<string>();
foreach (var a in args)
{
    if (a == "--") break;
    injectorArgs.Add(a);
}

string? workingDir = null;
string? bootDll = null;
string? overrideDir = null;
uint? armExecBpRva = null;
var unpackBarrier = false;
for (var i = 0; i < injectorArgs.Count; i++)
{
    switch (injectorArgs[i])
    {
        case "--working-dir" when i + 1 < injectorArgs.Count:
            workingDir = injectorArgs[++i];
            break;
        case "--boot-dll" when i + 1 < injectorArgs.Count:
            bootDll = injectorArgs[++i];
            break;
        case "--override-dir" when i + 1 < injectorArgs.Count:
            overrideDir = injectorArgs[++i];
            break;
        case "--unpack-barrier":
            unpackBarrier = true;
            break;
        case "--arm-exec-bp" when i + 1 < injectorArgs.Count:
            var rvaText = injectorArgs[++i];
            var rvaSpan = rvaText.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? rvaText[2..] : rvaText;
            if (!uint.TryParse(rvaSpan, System.Globalization.NumberStyles.HexNumber, null, out var rvaVal))
            {
                Console.Error.WriteLine($"error: --arm-exec-bp expects a hex RVA, got '{rvaText}'.");
                return 2;
            }
            armExecBpRva = rvaVal;
            break;
        case "-h" or "--help":
            PrintUsage();
            return 0;
        default:
            Console.Error.WriteLine($"error: unknown or malformed option '{injectorArgs[i]}'.");
            PrintUsage();
            return 2;
    }
}

// Default the boot DLL to the copy sitting next to this exe.
bootDll ??= Path.Combine(AppContext.BaseDirectory, "Talon.Boot.dll");
bootDll = Path.GetFullPath(bootDll);

// Default the working directory to the game exe's folder (first token of the cmdline).
var gameExe = FirstToken(gameCommandLine);
workingDir ??= Path.GetDirectoryName(gameExe) is { Length: > 0 } dir ? dir : Environment.CurrentDirectory;

// Hand the override/data directories to the injected boot DLL via environment
// variables. This works precisely because LaunchAndInject calls CreateProcessW
// with a null environment block, so the launched game inherits our environment.
if (overrideDir is { Length: > 0 })
{
    overrideDir = Path.GetFullPath(overrideDir);
    Environment.SetEnvironmentVariable("TALON_OVERRIDE_DIR", overrideDir);
    if (!Directory.Exists(overrideDir))
        Console.Error.WriteLine($"[talon] warning: override dir does not exist yet: {overrideDir}");
}

try
{
    if (unpackBarrier)
    {
        if (armExecBpRva is not null)
            throw new ArgumentException("--unpack-barrier and --arm-exec-bp are mutually exclusive.");
        armExecBpRva = Injector.FindKonnStageEntryRva(gameExe);
        Environment.SetEnvironmentVariable("TALON_UNPACK_STAGE_RVA", armExecBpRva.Value.ToString("X"));
    }

    Console.WriteLine($"[talon] boot dll   : {bootDll}");
    Console.WriteLine($"[talon] working dir: {workingDir}");
    if (overrideDir is { Length: > 0 })
        Console.WriteLine($"[talon] override   : {overrideDir}");
    if (armExecBpRva is { } bp)
        Console.WriteLine($"[talon] arm exec-BP : imageBase+0x{bp:X}");
    Console.WriteLine($"[talon] launching  : {gameCommandLine}");

    var result = Injector.LaunchAndInject(gameCommandLine, workingDir, bootDll, armExecBpRva);

    Console.WriteLine($"[talon] injected. pid={result.ProcessId}. APC queued; process resumed.");
    Console.WriteLine($"[talon] check %TEMP%\\talon-boot.log for the boot proof-of-life line.");
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[talon] injection failed: {ex.Message}");
    return 1;
}

static string FirstToken(string commandLine)
{
    commandLine = commandLine.TrimStart();
    if (commandLine.StartsWith('"'))
    {
        var end = commandLine.IndexOf('"', 1);
        return end > 0 ? commandLine[1..end] : commandLine[1..];
    }

    var space = commandLine.IndexOf(' ');
    return space > 0 ? commandLine[..space] : commandLine;
}

static void PrintUsage()
{
    Console.Error.WriteLine(
        "usage: Talon.Injector.exe [--working-dir <dir>] [--boot-dll <path>]");
    Console.Error.WriteLine(
        "                          [--override-dir <dir>] [--unpack-barrier] -- <game command line...>");
    Console.Error.WriteLine(
        "  everything after '--' is the target command line, passed verbatim to CreateProcessW.");
    Console.Error.WriteLine(
        "  --override-dir  folder of loose files to serve in place of packed .dat contents.");
    Console.Error.WriteLine(
        "  --unpack-barrier decode KONN metadata and arm the universal post-unpack barrier.");
    Console.Error.WriteLine(
        "  --arm-exec-bp   hex RVA at which to arm a HW execute-BP before resume (low-level diagnostics).");
}
