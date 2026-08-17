using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Galapa.Core.Configuration;

namespace Galapa.Launcher.ViewModels.SettingsFrame;

/// <summary>
/// ViewModel for the game-related settings page.
/// </summary>
public partial class GameSettingsPageViewModel(Settings settings) : SettingsFramePageViewModel
{
    [ObservableProperty] private Settings _settings = settings;

    [RelayCommand]
    private void Save() => Settings.Save();
}
