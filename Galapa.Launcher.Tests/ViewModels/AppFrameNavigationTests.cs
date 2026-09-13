using Galapa.Core.Configuration;
using Galapa.Launcher.Input;
using Galapa.Launcher.Models;
using Galapa.Launcher.ViewModels.AppFrame;
using Galapa.Launcher.ViewModels.OnboardingFrame;
using Galapa.Launcher.ViewModels.SettingsFrame;

namespace Galapa.Launcher.Tests.ViewModels;

public class AppFrameNavigationTests
{
    private static (AppFrameViewModel App, SettingsFrameViewModel Settings, NavigationContextService Navigation)
        Create()
    {
        var settings = new Settings();
        var navigation = new NavigationContextService();
        var settingsFrame = new SettingsFrameViewModel(
            new Lazy<GeneralSettingsPageViewModel>(() => new GeneralSettingsPageViewModel(settings)),
            new Lazy<GameSettingsPageViewModel>(() => new GameSettingsPageViewModel(settings)),
            new Lazy<AboutPageViewModel>(() => new AboutPageViewModel()));
        var app = new AppFrameViewModel(
            settings,
            new Lazy<HomePageViewModel>(() => null!),
            new Lazy<SettingsPageViewModel>(() => new SettingsPageViewModel(settingsFrame)),
            new OnboardingFrameViewModel(settings),
            navigation);
        return (app, settingsFrame, navigation);
    }

    [Fact]
    public void Construction_RegistersAppAndOnboardingUnderTheRoot()
    {
        var (app, _, navigation) = Create();

        Assert.Equal([app.NavigationContext, app.Onboarding.NavigationContext], navigation.Root.Children);
        Assert.Same(app.NavigationContext, navigation.ActiveContext);
        Assert.True(app.NavigationContext.IsEnabled);
        Assert.False(app.Onboarding.NavigationContext.IsEnabled);
        Assert.Same(app.Pages[0], app.SelectedPage);
    }

    [Fact]
    public void Bumpers_CyclePagesWithWrapAround()
    {
        var (app, _, navigation) = Create();

        Assert.True(navigation.Dispatch(ControllerAction.BumperRight, false));
        Assert.Same(app.Pages[1], app.SelectedPage);

        Assert.True(navigation.Dispatch(ControllerAction.BumperRight, false));
        Assert.Same(app.Pages[0], app.SelectedPage);

        Assert.True(navigation.Dispatch(ControllerAction.BumperLeft, false));
        Assert.Same(app.Pages[1], app.SelectedPage);
    }

    [Fact]
    public void SelectingSettings_NestsTheSettingsContextUnderTheAppContext()
    {
        var (app, settingsFrame, navigation) = Create();

        Assert.Null(app.NavigationContext.ActiveChild);

        app.SelectedPage = app.Pages[1];

        Assert.Same(app.NavigationContext, settingsFrame.NavigationContext.Parent);
        Assert.Same(settingsFrame.NavigationContext, app.NavigationContext.ActiveChild);
        Assert.Same(settingsFrame.NavigationContext, navigation.ActiveContext);

        app.SelectedPage = app.Pages[0];

        Assert.Null(app.NavigationContext.ActiveChild);
        Assert.Same(app.NavigationContext, navigation.ActiveContext);
    }

    [Fact]
    public void Triggers_CycleSettingsTabs_AndBumpersStillBubbleToTheApp()
    {
        var (app, settingsFrame, navigation) = Create();
        app.SelectedPage = app.Pages[1];

        Assert.True(navigation.Dispatch(ControllerAction.TriggerRight, false));
        Assert.Same(settingsFrame.Pages[1], settingsFrame.SelectedPage);

        Assert.True(navigation.Dispatch(ControllerAction.TriggerLeft, false));
        Assert.True(navigation.Dispatch(ControllerAction.TriggerLeft, false));
        Assert.Same(settingsFrame.Pages[^1], settingsFrame.SelectedPage);

        Assert.True(navigation.Dispatch(ControllerAction.BumperRight, false));
        Assert.Same(app.Pages[0], app.SelectedPage);

        // Triggers do nothing on the Launcher page (no nested context).
        Assert.False(navigation.Dispatch(ControllerAction.TriggerRight, false));
        Assert.Same(settingsFrame.Pages[^1], settingsFrame.SelectedPage);
    }

    [Fact]
    public void Onboarding_SuppressesAppNavigation()
    {
        var (app, _, navigation) = Create();
        app.SelectedPage = app.Pages[1];

        app.IsOnboarding = true;

        Assert.False(app.NavigationContext.IsEnabled);
        Assert.True(app.Onboarding.NavigationContext.IsEnabled);
        Assert.Same(app.Onboarding.NavigationContext, navigation.ActiveContext);

        Assert.False(navigation.Dispatch(ControllerAction.BumperRight, false));
        Assert.False(navigation.Dispatch(ControllerAction.TriggerRight, false));
        Assert.False(app.SelectAdjacentPage(1));
        Assert.Same(app.Pages[1], app.SelectedPage);

        app.IsOnboarding = false;

        Assert.True(app.NavigationContext.IsEnabled);
        Assert.False(app.Onboarding.NavigationContext.IsEnabled);
        Assert.True(navigation.Dispatch(ControllerAction.BumperRight, false));
        Assert.Same(app.Pages[0], app.SelectedPage);
    }

    [Fact]
    public void UnrelatedActions_AreNotConsumedByContexts()
    {
        var (_, _, navigation) = Create();

        Assert.False(navigation.Dispatch(ControllerAction.Confirm, false));
        Assert.False(navigation.Dispatch(ControllerAction.Down, false));
    }
}
