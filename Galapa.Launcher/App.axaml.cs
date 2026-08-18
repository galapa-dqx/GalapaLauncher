using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using DryIoc;
using Galapa.Launcher.Views;
using Galapa.Launcher.Theming;
using Galapa.Launcher.Views.Controls;
using Galapa.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Galapa.Launcher;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // Set up ViewLocator with dependency injection support
        this.DataTemplates.Add(new ViewLocator(Program.Services));
    }

    public override void OnFrameworkInitializationCompleted()
    {
        var catalog = Program.Services.Resolve<IThemeCatalog>();
        // Avalonia's classic desktop lifetime decides whether to show MainWindow as soon as
        // this callback returns, so initialization must finish synchronously. Run compiled
        // theme I/O on the pool to avoid blocking its awaits on Avalonia's UI context.
        var settings = Program.Services.Resolve<Settings>();
        Task.Run(() => catalog.LoadInitialAsync(settings.ThemeId)).GetAwaiter().GetResult();
        var initialPackage = catalog.Find(settings.ThemeId) ?? catalog.Find(Settings.DefaultThemeId)
            ?? throw new ThemePackageException("The Estella recovery theme is missing from the catalog.");
        try { Task.Run(() => ThemeRenderAssets.Prepare(initialPackage)).GetAwaiter().GetResult(); }
        catch when (!string.Equals(initialPackage.Manifest.Id, Settings.DefaultThemeId, StringComparison.Ordinal))
        {
            var recovery = catalog.Find(Settings.DefaultThemeId)
                ?? throw new ThemePackageException("The Estella recovery theme is missing from the catalog.");
            Task.Run(() => ThemeRenderAssets.Prepare(recovery)).GetAwaiter().GetResult();
            settings.ThemeId = Settings.DefaultThemeId;
            try { settings.Save(); }
            catch (Exception ex)
            {
                Program.Services.Resolve<ILogger<App>>().LogWarning(ex,
                    "The invalid initial theme was replaced by Estella, but the recovery choice could not be persisted");
            }
        }
        var manager = Program.Services.Resolve<IThemeManager>();
        if (!manager.ApplyInitialAsync(settings.ThemeId).GetAwaiter().GetResult())
            throw new ThemePackageException("The embedded Estella recovery theme could not be applied.");

        if (this.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit.
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            this.DisableAvaloniaDataAnnotationValidation();
            desktop.MainWindow = Program.Services.GetRequiredService<MainWindow>();
            desktop.MainWindow.Opened += async (_, _) =>
            {
                try { await Task.Run(() => catalog.LoadRemainingAsync()); }
                catch (Exception ex)
                {
                    Program.Services.Resolve<ILogger<App>>().LogError(ex,
                        "The remaining built-in themes could not be loaded");
                }
            };
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
