using CommunityToolkit.Mvvm.ComponentModel;
using Galapa.Core.Configuration;

namespace Galapa.Launcher.ViewModels.SettingsFrame;

/// <summary>
/// ViewModel for the game-related settings page.
/// </summary>
public partial class GameSettingsPageViewModel(Settings settings) : SettingsFramePageViewModel
{
    public override string Title => "Game";
    public override string Icon => "/Assets/Icons/solar--rocket-bold-duotone.svg";

    [ObservableProperty] private Settings _settings = settings;
}
