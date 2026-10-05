using System;
using Avalonia.Controls;
using Galapa.Launcher.Services;
using Galapa.Launcher.ViewModels;

namespace Galapa.Launcher.Views;

public partial class MainWindow : Window
{
    private readonly ControllerPollingService _pollingService;
    private readonly ControllerActionSource _actionSource;
    private readonly ControllerInputRouter _inputRouter;
    private readonly ActiveControllerService _activeControllerService;

    public MainWindow(
        MainWindowViewModel mainWindowViewModel,
        ControllerPollingService pollingService,
        ControllerActionSource actionSource,
        ControllerInputRouter inputRouter,
        ActiveControllerService activeControllerService)
    {
        DataContext = mainWindowViewModel;
        this._pollingService = pollingService;
        this._actionSource = actionSource;
        this._inputRouter = inputRouter;
        this._activeControllerService = activeControllerService;

        InitializeComponent();
        ExtendClientAreaToDecorationsHint = true;

        // Start controller services
        this._pollingService.Start();
        this._actionSource.Start();
        this._activeControllerService.Start();
        this._inputRouter.Attach(this);

        Closed += this.OnClosed;
    }

    /// <summary>
    ///     Test-only constructor: loads the compiled window XAML and attaches the input router
    ///     without starting DirectInput polling or the controller service graph.
    /// </summary>
    internal MainWindow(MainWindowViewModel mainWindowViewModel, ControllerInputRouter inputRouter)
    {
        DataContext = mainWindowViewModel;
        this._pollingService = null!;
        this._actionSource = null!;
        this._activeControllerService = null!;
        this._inputRouter = inputRouter;

        InitializeComponent();
        this._inputRouter.Attach(this);

        Closed += (_, _) => this._inputRouter.Detach();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        this._inputRouter.Detach();
        this._activeControllerService.Stop();
        this._actionSource.Stop();
        this._pollingService.Stop();
    }
}
