using System.Diagnostics;
using System.Text;

namespace Talon;

internal static class Log
{
    private static readonly object Sync = new();
    private static string? path;

    public static void Open()
    {
        lock (Sync)
            path ??= Path.Combine(Path.GetTempPath(), "talon-managed.log");
    }

    public static void Info(string message) => Write("info", message);
    public static void Warning(string message) => Write("warn", message);
    public static void Error(string message, Exception? exception = null) =>
        Write("error", exception is null ? message : $"{message}: {exception}");

    private static void Write(string level, string message)
    {
        var line = $"[{DateTime.UtcNow:O}] [{level}] {message}{Environment.NewLine}";
        try
        {
            Debug.Write(line);
            lock (Sync)
            {
                path ??= Path.Combine(Path.GetTempPath(), "talon-managed.log");
                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch
        {
            // Diagnostics must never escape into a game detour.
        }
    }
}
