using Galapa.Launcher.Input;
using Galapa.Launcher.ViewModels.SettingsFrame;

namespace Galapa.Launcher.ViewModels.AppFrame;

public class SettingsPageViewModel(SettingsFrameViewModel settingsFrame) : AppPageViewModel
{
    public SettingsFrameViewModel SettingsFrame { get; } = settingsFrame;

    public override NavigationContext NavigationContext => this.SettingsFrame.NavigationContext;
}
