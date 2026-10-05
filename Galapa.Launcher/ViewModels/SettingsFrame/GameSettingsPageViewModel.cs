using Galapa.Core.Configuration;
using Galapa.Launcher.Services;
using Galapa.Launcher.ViewModels.Editing;

namespace Galapa.Launcher.ViewModels.SettingsFrame;

/// <summary>
/// ViewModel for the game-related settings page. Edits apply live; there is no Save button.
/// </summary>
public class GameSettingsPageViewModel(
    Settings settings,
    ISettingsPersistence persistence,
    IFolderPicker folderPicker) : SettingsFramePageViewModel
{
    public override string Title => "Game";
    public override string Icon => "/Assets/Icons/solar--rocket-bold-duotone.svg";

    /// <summary>
    /// The game's install folder.
    /// </summary>
    public InstallFolderEditor InstallFolder { get; } = new(settings, persistence, folderPicker);
}
