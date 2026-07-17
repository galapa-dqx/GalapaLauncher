using System;
using Avalonia;

namespace Galapa.Toolbox;

internal sealed class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Headless CLI path: if the first argument is a known command, run it and exit instead
        // of starting the GUI. Anything else (including no args) launches the Avalonia app.
        if (args.Length > 0 && Cli.IsCommand(args[0]))
            return Cli.Run(args);

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }
}