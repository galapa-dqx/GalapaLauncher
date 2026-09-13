using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;
using Galapa.Launcher.Input;
using Galapa.Launcher.Models;

namespace Galapa.Launcher.ViewModels.SettingsFrame;

/// <summary>
///     Represents a tab in the SettingsFrame navigation.
/// </summary>
public class SettingsFrameTab(Lazy<SettingsFramePageViewModel> viewModel)
{
    public Lazy<SettingsFramePageViewModel> ViewModel { get; } = viewModel;
    public string Title => this.ViewModel.Value.Title;
    public string Icon => this.ViewModel.Value.Icon;
}

/// <summary>
///     ViewModel for the SettingsFrame, managing navigation between settings pages.
/// </summary>
public partial class SettingsFrameViewModel : ObservableObject
{
    [ObservableProperty] private SettingsFrameTab? _selectedPage;

    public SettingsFrameViewModel(
        Lazy<GeneralSettingsPageViewModel> generalSettingsPage,
        Lazy<GameSettingsPageViewModel> gameSettingsPage,
        Lazy<AboutPageViewModel> aboutPage)
    {
        this.Pages =
        [
            new(new Lazy<SettingsFramePageViewModel>(() => generalSettingsPage.Value)),
            new(new Lazy<SettingsFramePageViewModel>(() => gameSettingsPage.Value)),
            new(new Lazy<SettingsFramePageViewModel>(() => aboutPage.Value))
        ];

        this.NavigationContext = new NavigationContext("Settings", this.HandleNavigationAction);
        this.SelectedPage = this.Pages[0];
    }

    public List<SettingsFrameTab> Pages { get; }

    /// <summary>
    ///     The navigation context for the settings frame. Handles L2/R2 settings-tab switching
    ///     and is nested under the app shell's context while the Settings page is selected.
    /// </summary>
    public NavigationContext NavigationContext { get; }

    /// <summary>
    ///     Selects the settings tab <paramref name="offset"/> positions away from the current
    ///     one, wrapping around.
    /// </summary>
    /// <returns>True if the selected tab changed.</returns>
    public bool SelectAdjacentPage(int offset)
    {
        if (this.Pages.Count == 0)
            return false;

        var count = this.Pages.Count;
        var currentIndex = this.SelectedPage != null ? this.Pages.IndexOf(this.SelectedPage) : 0;
        var newIndex = ((currentIndex + offset) % count + count) % count;
        if (newIndex == currentIndex)
            return false;

        this.SelectedPage = this.Pages[newIndex];
        return true;
    }

    private bool HandleNavigationAction(ControllerAction action, bool isRepeat)
    {
        return action switch
        {
            ControllerAction.TriggerLeft => this.SelectAdjacentPage(-1),
            ControllerAction.TriggerRight => this.SelectAdjacentPage(1),
            _ => false
        };
    }
}
