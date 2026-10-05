using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using DryIoc;
using Galapa.Launcher.Input;
using Galapa.Launcher.Models;
using Galapa.Launcher.ViewModels.AppFrame;
using Galapa.Launcher.ViewModels.SettingsFrame;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Galapa.Launcher.Tests.Input;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HeadlessCollection : ICollectionFixture<HeadlessFixture>
{
    public const string Name = "Headless UI";
}

/// <summary>
///     Runs a single headless Avalonia session for the UI routing tests.
/// </summary>
public sealed class HeadlessFixture : IDisposable
{
    private readonly HeadlessUnitTestSession _session =
        HeadlessUnitTestSession.StartNew(typeof(HeadlessTestApplication));

    public Task Dispatch(Action action) => this._session.Dispatch(action, CancellationToken.None);

    public Task<T> Dispatch<T>(Func<T> action) => this._session.Dispatch(action, CancellationToken.None);

    public void Dispose() => this._session.Dispose();
}

public static class HeadlessTestApplication
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Application>()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions())
        .AfterSetup(builder =>
        {
            var app = builder.Instance!;
            app.Styles.Add(new FluentTheme());

            // Page transitions keep the outgoing view alive asynchronously; disable them so the
            // visual tree is deterministic after a page switch.
            app.Styles.Add(new Style(x => x.OfType<TransitioningContentControl>())
            {
                Setters = { new Setter(TransitioningContentControl.PageTransitionProperty, null) }
            });

            // Stand-ins for page content so tests have known focusable controls, then the real
            // ViewLocator for everything else (AppFrame -> SettingsPage -> SettingsFrame).
            app.DataTemplates.Add(new FuncDataTemplate<HomePageViewModel>((_, _) => new StackPanel
            {
                Children =
                {
                    new Button { Name = "HomeButton", Content = "Home" },
                    new ConsumingButton { Name = "ConsumingButton", Content = "Consumes R1" }
                }
            }));
            app.DataTemplates.Add(new FuncDataTemplate<GeneralSettingsPageViewModel>((_, _) =>
                new Button { Name = "GeneralSettingsButton", Content = "General" }));

            var container = new DryIoc.Container();
            container.Register(typeof(ILogger<>), typeof(NullLogger<>), Reuse.Singleton);
            app.DataTemplates.Add(new ViewLocator(container));
        });
}

/// <summary>
///     A control-local handler that consumes R1 only, used to prove local handlers run before
///     navigation contexts.
/// </summary>
public sealed class ConsumingButton : Button, IControllerInputHandler
{
    public List<ControllerAction> Received { get; } = [];

    public bool HandleControllerInput(ControllerAction action, bool isRepeat)
    {
        this.Received.Add(action);
        return action == ControllerAction.BumperRight;
    }
}

/// <summary>
///     Manually triggered stand-in for the DirectInput-backed action source.
/// </summary>
public sealed class FakeActionSource : IControllerActionSource
{
    private static readonly Controller Controller = null!;

    public event EventHandler<ControllerActionEventArgs>? ActionTriggered;
    public event EventHandler<ControllerActionEventArgs>? ActionRepeated;

    public void Trigger(ControllerAction action) =>
        this.ActionTriggered?.Invoke(this, new ControllerActionEventArgs
        {
            Controller = Controller, Action = action, IsRepeat = false
        });

    public void Repeat(ControllerAction action) =>
        this.ActionRepeated?.Invoke(this, new ControllerActionEventArgs
        {
            Controller = Controller, Action = action, IsRepeat = true
        });
}
