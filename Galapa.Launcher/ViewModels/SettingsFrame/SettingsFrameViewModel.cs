using System;
using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

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
public partial class SettingsFrameViewModel(
    Lazy<GeneralSettingsPageViewModel> generalSettingsPage,
    Lazy<GameSettingsPageViewModel> gameSettingsPage,
    Lazy<AboutPageViewModel> aboutPage
) : ObservableObject
{
    [ObservableProperty] private SettingsFrameTab? _selectedPage;

    public List<SettingsFrameTab> Pages { get; } =
    [
        new(new Lazy<SettingsFramePageViewModel>(() => generalSettingsPage.Value)),
        new(new Lazy<SettingsFramePageViewModel>(() => gameSettingsPage.Value)),
        new(new Lazy<SettingsFramePageViewModel>(() => aboutPage.Value))
    ];
}