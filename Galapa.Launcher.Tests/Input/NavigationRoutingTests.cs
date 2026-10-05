using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.VisualTree;
using Galapa.Core.Configuration;
using Galapa.Launcher.Input;
using Galapa.Launcher.Models;
using Galapa.Launcher.Services;
using Galapa.Launcher.ViewModels;
using Galapa.Launcher.ViewModels.AppFrame;
using Galapa.Launcher.ViewModels.LoginFrame;
using Galapa.Launcher.ViewModels.OnboardingFrame;
using Galapa.Launcher.ViewModels.SettingsFrame;
using Galapa.Launcher.Views;
using Galapa.Launcher.Views.AppFrame;
using Microsoft.Extensions.Logging.Abstractions;

namespace Galapa.Launcher.Tests.Input;

/// <summary>
///     End-to-end routing through the real <see cref="MainWindow"/> XAML: title-bar tabs outside
///     the AppFrame, page content, nested settings content, local handlers, and onboarding.
/// </summary>
[Collection(HeadlessCollection.Name)]
public sealed class NavigationRoutingTests(HeadlessFixture headless)
{
    private sealed class Harness : IDisposable
    {
        public Harness()
        {
            var settings = new Settings();
            this.Navigation = new NavigationContextService();
            this.Settings = new SettingsFrameViewModel(
                new Lazy<GeneralSettingsPageViewModel>(() => new GeneralSettingsPageViewModel(settings)),
                new Lazy<GameSettingsPageViewModel>(() => new GameSettingsPageViewModel(settings)),
                new Lazy<AboutPageViewModel>(() => new AboutPageViewModel()));

            // The Home page is rendered by a test template, so the login frame never materialises.
            var loginFrame = new LoginFrameViewModel(new LoginNavigationService(),
                () => null!, () => null!, () => null!, () => null!);
            this.App = new AppFrameViewModel(
                settings,
                new Lazy<HomePageViewModel>(() => new HomePageViewModel(loginFrame)),
                new Lazy<SettingsPageViewModel>(() => new SettingsPageViewModel(this.Settings)),
                new OnboardingFrameViewModel(settings),
                this.Navigation);

            this.Source = new FakeActionSource();
            this.Router = new ControllerInputRouter(NullLogger<ControllerInputRouter>.Instance, this.Source,
                this.Navigation);
            this.Window = new MainWindow(new MainWindowViewModel(this.App), this.Router);
            this.Window.Show();
            this.Window.UpdateLayout();
        }

        public MainWindow Window { get; }
        public AppFrameViewModel App { get; }
        public SettingsFrameViewModel Settings { get; }
        public NavigationContextService Navigation { get; }
        public FakeActionSource Source { get; }
        public ControllerInputRouter Router { get; }

        public NavigationContext AppContext => this.App.NavigationContext;
        public NavigationContext SettingsContext => this.Settings.NavigationContext;
        public NavigationContext OnboardingContext => this.App.Onboarding.NavigationContext;

        public IInputElement? Focused => this.Window.FocusManager?.GetFocusedElement();

        /// <summary>The Launcher/Settings tab strip in the title row, outside the AppFrame's tree.</summary>
        public TabStrip TitleTabs => this.Window.GetVisualDescendants().OfType<TabStrip>()
            .Single(strip => strip.FindAncestorOfType<AppFrame>() == null);

        public TabStripItem TitleTab(int index)
        {
            this.Window.UpdateLayout();
            return (TabStripItem)this.TitleTabs.ContainerFromIndex(index)!;
        }

        public T Control<T>(string name) where T : Control
        {
            this.Window.UpdateLayout();
            return this.Window.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);
        }

        public void Dispose() => this.Window.Close();
    }

    [Fact]
    public async Task TitleBarTabFocused_BumpersSwitchPages()
    {
        await headless.Dispatch(() =>
        {
            using var h = new Harness();
            var tab = h.TitleTab(0);
            Assert.Null(tab.FindAncestorOfType<AppFrame>());
            Assert.True(tab.Focus());

            Assert.Same(h.AppContext, h.Navigation.FocusedContext);

            h.Source.Trigger(ControllerAction.BumperRight);
            Assert.Same(h.App.Pages[1], h.App.SelectedPage);

            h.Source.Trigger(ControllerAction.BumperLeft);
            Assert.Same(h.App.Pages[0], h.App.SelectedPage);

            // Focus stays where the user put it; routing never steals it.
            Assert.Same(tab, h.Focused);
        });
    }

    [Fact]
    public async Task PageContentFocused_BumpersSwitchPages_EvenAfterFocusIsLost()
    {
        await headless.Dispatch(() =>
        {
            using var h = new Harness();
            Assert.True(h.Control<Button>("HomeButton").Focus());
            Assert.Same(h.AppContext, h.Navigation.FocusedContext);

            h.Source.Trigger(ControllerAction.BumperRight);
            Assert.Same(h.App.Pages[1], h.App.SelectedPage);

            // The focused control left the tree with its page; the logical chain still routes.
            h.Window.UpdateLayout();
            Assert.Null(h.Focused);
            Assert.Null(h.Navigation.FocusedContext);
            Assert.Same(h.SettingsContext, h.Navigation.ActiveContext);
            h.Source.Repeat(ControllerAction.BumperRight);
            Assert.Same(h.App.Pages[0], h.App.SelectedPage);
        });
    }

    [Fact]
    public async Task NestedSettingsControlFocused_TriggersSwitchTabs_AndBumpersBubble()
    {
        await headless.Dispatch(() =>
        {
            using var h = new Harness();
            h.App.SelectedPage = h.App.Pages[1];
            Assert.True(h.Control<Button>("GeneralSettingsButton").Focus());

            Assert.Same(h.SettingsContext, h.Navigation.FocusedContext);
            Assert.Same(h.SettingsContext, h.Navigation.ActiveContext);
            Assert.Same(h.AppContext, h.SettingsContext.Parent);

            h.Source.Trigger(ControllerAction.TriggerRight);
            Assert.Same(h.Settings.Pages[1], h.Settings.SelectedPage);
            Assert.Same(h.App.Pages[1], h.App.SelectedPage);

            h.Source.Trigger(ControllerAction.BumperLeft);
            Assert.Same(h.App.Pages[0], h.App.SelectedPage);
            Assert.Same(h.AppContext, h.Navigation.ActiveContext);
        });
    }

    [Fact]
    public async Task TitleBarTabFocused_WhileSettingsSelected_TriggersStillReachSettings()
    {
        await headless.Dispatch(() =>
        {
            using var h = new Harness();
            h.App.SelectedPage = h.App.Pages[1];
            Assert.True(h.TitleTab(1).Focus());

            // The focused scope is the App, whose active child is the Settings context.
            Assert.Same(h.AppContext, h.Navigation.FocusedContext);
            Assert.Same(h.SettingsContext, h.Navigation.ActiveContext);

            h.Source.Trigger(ControllerAction.TriggerRight);
            Assert.Same(h.Settings.Pages[1], h.Settings.SelectedPage);
        });
    }

    [Fact]
    public async Task LocalHandlers_ConsumeBeforeContexts()
    {
        await headless.Dispatch(() =>
        {
            using var h = new Harness();
            var consuming = h.Control<ConsumingButton>("ConsumingButton");
            Assert.True(consuming.Focus());

            h.Source.Trigger(ControllerAction.BumperRight);
            Assert.Same(h.App.Pages[0], h.App.SelectedPage);

            h.Source.Trigger(ControllerAction.BumperLeft);
            Assert.Same(h.App.Pages[1], h.App.SelectedPage);

            Assert.Equal([ControllerAction.BumperRight, ControllerAction.BumperLeft], consuming.Received);
        });
    }

    [Fact]
    public async Task Onboarding_HidesTitleTabsAndSuppressesPageNavigation()
    {
        await headless.Dispatch(() =>
        {
            using var h = new Harness();
            h.App.IsOnboarding = true;
            h.Window.UpdateLayout();

            Assert.False(h.TitleTabs.IsVisible);
            Assert.False(h.TitleTab(1).Focus());
            Assert.Same(h.OnboardingContext, h.Navigation.ActiveContext);

            Assert.True(h.Control<Button>("HomeButton").Focus());
            h.Source.Trigger(ControllerAction.BumperRight);
            Assert.Same(h.App.Pages[0], h.App.SelectedPage);
            Assert.Same(h.OnboardingContext, h.Navigation.ActiveContext);

            h.Window.FocusManager!.ClearFocus();
            h.Source.Trigger(ControllerAction.BumperRight);
            Assert.Same(h.App.Pages[0], h.App.SelectedPage);

            h.App.IsOnboarding = false;
            h.Window.UpdateLayout();
            Assert.True(h.TitleTabs.IsVisible);
            h.Source.Trigger(ControllerAction.BumperRight);
            Assert.Same(h.App.Pages[1], h.App.SelectedPage);
        });
    }

    [Fact]
    public async Task FocusTransitions_ActivateContextsDeterministically()
    {
        await headless.Dispatch(() =>
        {
            using var h = new Harness();
            var changes = new List<string>();
            h.Navigation.ActiveContextChanged += (_, e) => changes.Add($"{e.Previous.Name}->{e.Current.Name}");

            Assert.True(h.Control<Button>("HomeButton").Focus());
            Assert.Same(h.AppContext, h.Navigation.ActiveContext);

            h.App.SelectedPage = h.App.Pages[1];
            Assert.True(h.Control<Button>("GeneralSettingsButton").Focus());
            Assert.Same(h.SettingsContext, h.Navigation.FocusedContext);
            Assert.Same(h.SettingsContext, h.Navigation.ActiveContext);

            Assert.True(h.TitleTab(1).Focus());
            Assert.Same(h.AppContext, h.Navigation.FocusedContext);
            Assert.Same(h.SettingsContext, h.Navigation.ActiveContext);

            h.App.IsOnboarding = true;
            Assert.Same(h.OnboardingContext, h.Navigation.ActiveContext);

            h.App.IsOnboarding = false;
            Assert.Same(h.SettingsContext, h.Navigation.ActiveContext);

            Assert.Equal(
            [
                "App->Settings",
                "Settings->Onboarding",
                "Onboarding->Settings"
            ], changes);
        });
    }

    [Fact]
    public async Task DirectionalAndConfirmActions_FallThroughToDefaults()
    {
        await headless.Dispatch(() =>
        {
            using var h = new Harness();
            Assert.True(h.Control<Button>("HomeButton").Focus());

            Assert.False(h.Router.Route(ControllerAction.Down, false));
            Assert.False(h.Router.Route(ControllerAction.Confirm, false));
            Assert.Same(h.App.Pages[0], h.App.SelectedPage);
            Assert.NotNull(h.Focused);
        });
    }
}
