using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Galapa.Core.Configuration;
using Galapa.Launcher.Input;
using Galapa.Launcher.Models;
using Galapa.Launcher.ViewModels.OnboardingFrame;

namespace Galapa.Launcher.ViewModels.AppFrame;

public class AppFrameTabBase<TViewModel>(Lazy<TViewModel> viewModel, string icon, string title)
{
    public Lazy<TViewModel> ViewModel { get; } = viewModel;
    public string Icon { get; } = icon;
    public string Title { get; } = title;
}

public class AppFrameTab(Lazy<AppPageViewModel> viewModel, string icon, string title)
    : AppFrameTabBase<AppPageViewModel>(viewModel, icon, title);

/// <summary>
///     Owns top-level application navigation: which page (Launcher/Settings) is shown, and
///     whether onboarding is currently replacing normal navigation.
/// </summary>
public partial class AppFrameViewModel : ObservableObject
{
    [ObservableProperty] private AppFrameTab? _selectedPage;
    [ObservableProperty] private Settings? _settings;

    /// <summary>
    ///     While true, normal Launcher/Settings navigation is hidden and suppressed and the
    ///     onboarding navigation context is active instead.
    /// </summary>
    [ObservableProperty] private bool _isOnboarding;

    public AppFrameViewModel(
        Settings settings,
        Lazy<HomePageViewModel> homePage,
        Lazy<SettingsPageViewModel> settingsPage,
        OnboardingFrameViewModel onboarding,
        NavigationContextService navigation)
    {
        this._settings = settings;
        this.Onboarding = onboarding;
        this.Pages =
        [
            new(new Lazy<AppPageViewModel>(() => homePage.Value),
                "/Assets/Icons/solar--rocket-bold-duotone.svg", "Launcher"),

            new(new Lazy<AppPageViewModel>(() => settingsPage.Value),
                "/Assets/Icons/solar--settings-bold-duotone.svg", "Settings")
        ];

        this.NavigationContext = new NavigationContext("App", this.HandleNavigationAction);
        navigation.Root.AttachChild(this.NavigationContext);
        navigation.Root.AttachChild(onboarding.NavigationContext);
        this.ApplyOnboardingState();

        this.SelectedPage = this.Pages[0];
    }

    public List<AppFrameTab> Pages { get; }

    public OnboardingFrameViewModel Onboarding { get; }

    /// <summary>
    ///     The navigation context for the application shell. Handles L1/R1 page switching and
    ///     hosts the selected page's context (if any) as its active child.
    /// </summary>
    public NavigationContext NavigationContext { get; }

    /// <summary>
    ///     Selects the page <paramref name="offset"/> positions away from the current one,
    ///     wrapping around. Does nothing during onboarding.
    /// </summary>
    /// <returns>True if the selected page changed.</returns>
    public bool SelectAdjacentPage(int offset)
    {
        if (this.IsOnboarding || this.Pages.Count == 0)
            return false;

        var count = this.Pages.Count;
        var currentIndex = this.SelectedPage != null ? this.Pages.IndexOf(this.SelectedPage) : 0;
        var newIndex = ((currentIndex + offset) % count + count) % count;
        if (newIndex == currentIndex)
            return false;

        this.SelectedPage = this.Pages[newIndex];
        return true;
    }

    partial void OnSelectedPageChanged(AppFrameTab? value)
    {
        // The selected page's context (e.g. the settings frame) becomes the active child so
        // its handlers run before ours, regardless of where focus physically is.
        this.NavigationContext.ActiveChild = value?.ViewModel.Value?.NavigationContext;
    }

    partial void OnIsOnboardingChanged(bool value)
    {
        this.ApplyOnboardingState();
    }

    private void ApplyOnboardingState()
    {
        // Enable and activate the incoming context before disabling the outgoing one, so the
        // active context moves directly between the two without passing through the root.
        if (this.IsOnboarding)
        {
            this.Onboarding.NavigationContext.IsEnabled = true;
            this.Onboarding.NavigationContext.Activate();
            this.NavigationContext.IsEnabled = false;
        }
        else
        {
            this.NavigationContext.IsEnabled = true;
            this.NavigationContext.Activate();
            this.Onboarding.NavigationContext.IsEnabled = false;
        }
    }

    private bool HandleNavigationAction(ControllerAction action, bool isRepeat)
    {
        return action switch
        {
            ControllerAction.BumperLeft => this.SelectAdjacentPage(-1),
            ControllerAction.BumperRight => this.SelectAdjacentPage(1),
            _ => false
        };
    }
}
