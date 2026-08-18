using System.Runtime.InteropServices;

namespace Talon;

// Reports the first managed startup failure without blocking a game or hook thread.
internal static partial class FailureNotifier
{
    private static int shown;

    public static void ShowOnce(string subsystem, Exception exception)
    {
        if (Interlocked.Exchange(ref shown, 1) != 0) return;
        var message =
            $"Talon could not initialize {subsystem}. DQX will continue without that " +
            $"Talon feature.\n\n{exception.Message}\n\nSee talon-managed.log for details.";
        _ = Task.Run(() => MessageBox(
            0,
            message,
            "Talon hook failure",
            MessageBoxOk | MessageBoxIconWarning | MessageBoxSetForeground));
    }

    private const uint MessageBoxOk = 0;
    private const uint MessageBoxIconWarning = 0x30;
    private const uint MessageBoxSetForeground = 0x00010000;

    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(nint window, string text, string caption, uint type);
}
