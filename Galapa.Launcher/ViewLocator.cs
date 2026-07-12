using System;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using CommunityToolkit.Mvvm.ComponentModel;
using DryIoc;
using Galapa.Launcher.ViewModels;
using Galapa.Launcher.ViewModels.AppFrame;
using Galapa.Launcher.ViewModels.LoginFrame;
using Galapa.Launcher.ViewModels.SettingsFrame;
using Galapa.Launcher.Views;
using Galapa.Launcher.Views.AppFrame;
using Galapa.Launcher.Views.LoginFrame;
using Galapa.Launcher.Views.SettingsFrame;
using Microsoft.Extensions.Logging;

namespace Galapa.Launcher;

/// <summary>
///     Given a view model, returns the corresponding view using pattern matching and dependency injection.
///     This implementation is typesafe and doesn't use reflection.
/// </summary>
public class ViewLocator : IDataTemplate
{
    private readonly IContainer _container;

    public ViewLocator(IContainer container)
    {
        this._container = container ?? throw new ArgumentNullException(nameof(container));
    }

    public Control? Build(object? param)
    {
        var logger = this._container.Resolve<ILogger<ViewLocator>>();
        logger.LogWarning($"Building view for {param?.GetType().Name ?? "null"}");
        return param switch
        {
            null => null,

            // Pattern match each ViewModel to its corresponding View
            MainWindowViewModel => this.ResolveView<MainWindow>(),
            // AppFrame
            AppFrameViewModel => this.ResolveView<AppFrame>(),
            HomePageViewModel => this.ResolveView<HomePage>(),
            SettingsPageViewModel => this.ResolveView<SettingsPage>(),
            // LoginFrame
            LoginFrameViewModel => this.ResolveView<LoginFrame>(),
            AskUsernamePasswordPageViewModel => this.ResolveView<AskUsernamePasswordPage>(),
            AskPasswordPageViewModel => this.ResolveView<AskPasswordPage>(),
            PlayerSelectPageViewModel => this.ResolveView<PlayerSelectPage>(),
            LoginCompletedPageViewModel => this.ResolveView<LoginCompletedPage>(),
            // SettingsFrame
            SettingsFrameViewModel => this.ResolveView<SettingsFrame>(),
            GeneralSettingsPageViewModel => this.ResolveView<GeneralSettingsPage>(),
            GameSettingsPageViewModel => this.ResolveView<GameSettingsPage>(),
            AboutPageViewModel => this.ResolveView<AboutPage>(),

            // Fallback for unknown ViewModels
            _ => new TextBlock { Text = $"View not found for: {param.GetType().Name}" }
        };
    }

    public bool Match(object? data)
    {
        // Only act as a template for view models. Returning true for everything would intercept
        // plain content like strings (e.g. a Button's string Content), routing them through this
        // locator and producing "View not found" placeholders. All view models derive from
        // ObservableObject (CommunityToolkit.Mvvm).
        return data is ObservableObject;
    }

    /// <summary>
    ///     Resolves a view from the container with full dependency injection support.
    ///     Falls back to Activator.CreateInstance if the view isn't registered in the container.
    /// </summary>
    private Control ResolveView<TView>() where TView : Control
    {
        // Try to get the view from DI first
        var view = this._container.Resolve<TView>(IfUnresolved.ReturnDefault);

        // Fallback to Activator if not registered in DI
        return view ?? Activator.CreateInstance<TView>();
    }
}