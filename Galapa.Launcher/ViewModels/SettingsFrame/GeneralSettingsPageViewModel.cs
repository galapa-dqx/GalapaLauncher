using CommunityToolkit.Mvvm.ComponentModel;
using Galapa.Core.Configuration;

namespace Galapa.Launcher.ViewModels.SettingsFrame;

/// <summary>
/// ViewModel for the general settings page.
/// </summary>
public partial class GeneralSettingsPageViewModel(Settings settings) : SettingsFramePageViewModel
{
    public override string Title => "General";
    public override string Icon => "/Assets/Icons/solar--settings-bold-duotone.svg";

    [ObservableProperty] private Settings _settings = settings;
}
