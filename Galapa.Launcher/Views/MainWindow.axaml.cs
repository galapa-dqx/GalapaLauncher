using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Galapa.Launcher.Services;
using Galapa.Launcher.ViewModels;

namespace Galapa.Launcher.Views;

public partial class MainWindow : Window
{
    private readonly ControllerPollingService _pollingService;
    private readonly ControllerActionSource _actionSource;
    private readonly ControllerInputRouter _inputRouter;
    private readonly ActiveControllerService _activeControllerService;

    // Lets the headless contract test execute the actual compiled XAML without
    // starting DirectInput or constructing the application service graph.
    internal MainWindow(bool _)
    {
        _pollingService = null!;
        _actionSource = null!;
        _inputRouter = null!;
        _activeControllerService = null!;
        InitializeComponent();
    }

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
        PART_TitleBar.AddHandler(PointerPressedEvent, TitleBar_PointerPressed, RoutingStrategies.Bubble);

        // Start controller services
        this._pollingService.Start();
        this._actionSource.Start();
        this._activeControllerService.Start();
        this._inputRouter.Attach(this);

        Closed += this.OnClosed;
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        this._inputRouter.Detach();
        this._activeControllerService.Stop();
        this._actionSource.Stop();
        this._pollingService.Stop();
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            return;
        }

        e.Handled = true;
        BeginMoveDrag(e);
    }
}
