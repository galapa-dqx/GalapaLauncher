using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using DryIoc;
using Galapa.Launcher.Views;
using Galapa.Launcher.Theming;
using Microsoft.Extensions.DependencyInjection;

namespace Galapa.Launcher;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        ThemeFontRegistrar.RegisterBuiltIns();

        // Set up ViewLocator with dependency injection support
        this.DataTemplates.Add(new ViewLocator(Program.Services));
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var catalog = Program.Services.Resolve<IThemeCatalog>();
        // Avalonia's classic desktop lifetime decides whether to show MainWindow as soon as
        // this callback returns, so initialization must finish synchronously. Run archive I/O
        // on the pool to avoid blocking its awaits on Avalonia's UI synchronization context.
        Task.Run(() => catalog.LoadAsync()).GetAwaiter().GetResult();
        var settings = Program.Services.Resolve<Galapa.Core.Configuration.Settings>();
        var manager = Program.Services.Resolve<IThemeManager>();
        if (!manager.ApplyAsync(settings.ThemeId, persist: false).GetAwaiter().GetResult())
            manager.ApplyAsync("estella", persist: true).GetAwaiter().GetResult();

        if (this.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit.
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            this.DisableAvaloniaDataAnnotationValidation();
            desktop.MainWindow = Program.Services.GetRequiredService<MainWindow>();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void DisableAvaloniaDataAnnotationValidation()
    {
        // Get an array of plugins to remove
        var dataValidationPluginsToRemove =
            BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

        // remove each entry found
        foreach (var plugin in dataValidationPluginsToRemove) BindingPlugins.DataValidators.Remove(plugin);
    }
}
