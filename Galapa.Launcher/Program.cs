using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using DryIoc;
using Galapa.Core.Configuration;
using Galapa.Core.Game;
using Galapa.Core.Models;
using Galapa.Launcher.Services;
using Galapa.Launcher.ViewModels;
using Galapa.Launcher.ViewModels.AppFrame;
using Galapa.Launcher.ViewModels.LoginFrame;
using Galapa.Launcher.ViewModels.SettingsFrame;
using Galapa.Launcher.Views;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;
using Velopack;
using Velopack.Logging;
using ILogger = Serilog.ILogger;

namespace Galapa.Launcher;

internal sealed class Program
{
    public static IContainer Services;

    public static ConsoleVelopackLogger Log { get; } = new();

    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static void Main(string[] args)
    {
        // Log unobserved task exceptions
        TaskScheduler.UnobservedTaskException += (sender, e) =>
        {
            Console.WriteLine($"UNOBSERVED TASK EXCEPTION: {e.Exception}");
            foreach (var ex in e.Exception.InnerExceptions) Console.WriteLine($"  Inner: {ex}");
        };

        // TODO: Unfuck this whole startup sequence
        VelopackApp.Build().SetLogger(Log).Run();

        // Boot the application
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    private static IContainer CreateServiceProvider()
    {
        var container = new Container();

        // Register singletons
        container.Register<MainWindow>(Reuse.Singleton);
        container.Register<MainWindowViewModel>(Reuse.Singleton);
        container.Register<AppFrameViewModel>(Reuse.Singleton);
        container.Register<HomePageViewModel>(Reuse.Singleton);
        container.Register<SettingsPageViewModel>(Reuse.Singleton);
        container.Register<SettingsFrameViewModel>(Reuse.Singleton);
        container.Register<GeneralSettingsPageViewModel>(Reuse.Singleton);
        container.Register<GameSettingsPageViewModel>(Reuse.Singleton);
        container.Register<AboutPageViewModel>(Reuse.Singleton);
        container.Register<LoginFlowState>(Reuse.Singleton);
        container.Register<LoginNavigationService>(Reuse.Singleton);
        container.Register<IPlayerCredentialFactory, WindowsCredentialManagerFactory>(Reuse.Singleton);
        container.Register<PlayerList>(Reuse.Singleton);

        // Controller input services
        container.Register<ControllerListService>(Reuse.Singleton);
        container.Register<ControllerPollingService>(Reuse.Singleton);
        container.Register<ControllerConfigService>(Reuse.Singleton);
        container.Register<ControllerActionSource>(Reuse.Singleton);
        container.Register<ControllerInputRouter>(Reuse.Singleton);
        container.Register<ActiveControllerService>(Reuse.Singleton);

        // Register transients
        container.Register<LoginFrameViewModel>(Reuse.Transient);
        container.Register<LoginPageViewModel>(Reuse.Transient);
        container.Register<PlayerSelectPageViewModel>(Reuse.Transient);
        container.Register<AskUsernamePasswordPageViewModel>(Reuse.Transient);
        container.Register<AskPasswordPageViewModel>(Reuse.Transient);
        container.Register<LoginCompletedPageViewModel>(Reuse.Transient);
        container.Register<GameProcess>(Reuse.Transient);

        // Register Settings with factory
        container.RegisterDelegate<Settings>(_ => Settings.Load(), Reuse.Singleton);

        // Register logging
        var logger = BuildLogger();
        var loggerFactory = new SerilogLoggerFactory(logger, true);
        container.RegisterInstance<ILoggerFactory>(loggerFactory);

        // Register ILogger<T> using the generic CreateLogger<T> extension method
        var createLoggerMethod = typeof(LoggerFactoryExtensions)
            .GetMethods()
            .First(m => m.Name == "CreateLogger" &&
                        m.IsGenericMethodDefinition &&
                        m.GetParameters().Length == 1);

        container.Register(
            typeof(ILogger<>),
            made: Made.Of(FactoryMethod.Of(createLoggerMethod), Parameters.Of.Type<ILoggerFactory>()),
            reuse: Reuse.Transient);

        return container;
    }

    private static ILogger BuildLogger()
    {
        var cfg = new LoggerConfiguration();
        cfg.WriteTo.Console();
        cfg.Enrich.FromLogContext();

        return cfg.CreateLogger();
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        // Set up Paths for Galapa.Core
        Paths.Initialize();

        // We can't create our service provider until the Paths are configured to allow the Settings to be loaded
        // TODO: move Paths.AppData into the launcher itself (it's not core-related)
        Services = CreateServiceProvider();
        var settings = Services.Resolve<Settings>();
        ConfigFile.RootDirectory = settings.SaveFolderPath;

        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithDeveloperTools()
            .WithInterFont()
            .LogToTrace();
    }
}