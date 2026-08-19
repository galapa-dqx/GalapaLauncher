using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using DryIoc;
using Galapa.Launcher.Views;
using Galapa.Launcher.Theming;
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
        var settings = Program.Services.Resolve<Settings>();
        var preferredThemeId = ThemeId.TryParse(settings.ThemeId, out var parsedThemeId)
            ? parsedThemeId
            : new ThemeId(Settings.DefaultThemeId);
        var manager = Program.Services.Resolve<IThemeManager>();
        if (!manager.ApplyInitialAsync(preferredThemeId).GetAwaiter().GetResult().Succeeded)
            throw new ThemePackageException("The embedded Estella recovery theme could not be applied.");

        if (this.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Avoid duplicate validations from both Avalonia and the CommunityToolkit.
            // More info: https://docs.avaloniaui.net/docs/guides/development-guides/data-validation#manage-validationplugins
            this.DisableAvaloniaDataAnnotationValidation();
            desktop.MainWindow = Program.Services.GetRequiredService<MainWindow>();
            desktop.MainWindow.Opened += async (_, _) =>
            {
                try { await Task.Run(() => catalog.ValidateRemainingAsync()); }
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
