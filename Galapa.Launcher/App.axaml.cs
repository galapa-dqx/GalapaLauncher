using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using DryIoc;
using Galapa.Launcher.Views;
using Galapa.Launcher.Theming;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
        // this callback returns, so initialization must finish synchronously. Run compiled
        // theme I/O on the pool to avoid blocking its awaits on Avalonia's UI context.
        Task.Run(() => catalog.LoadAsync()).GetAwaiter().GetResult();
        catalog.PrepareRenderAssets();
        var settings = Program.Services.Resolve<Galapa.Core.Configuration.Settings>();
        var manager = Program.Services.Resolve<IThemeManager>();
        if (!manager.ApplyAsync(settings.ThemeId, persist: false).GetAwaiter().GetResult())
        {
            if (!manager.ApplyAsync(Galapa.Core.Configuration.Settings.DefaultThemeId, persist: false).GetAwaiter().GetResult())
                throw new ThemePackageException("The embedded Estella recovery theme could not be applied.");
            settings.ThemeId = Galapa.Core.Configuration.Settings.DefaultThemeId;
            try { settings.Save(); }
            catch (Exception ex)
            {
                Program.Services.Resolve<ILogger<App>>().LogWarning(ex,
                    "The recovery theme was applied, but its selection could not be persisted");
            }
        }

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
