using System.ComponentModel.DataAnnotations;
using Galapa.Core.Configuration;
using Galapa.Launcher.Services;
using Galapa.Launcher.ViewModels.Editing;

namespace Galapa.Launcher.ViewModels.OnboardingFrame;

public class GameFolderPageViewModel(
    Settings settings,
    ISettingsPersistence persistence,
    IFolderPicker folderPicker) : OnboardingPageViewModel
{
    public override string Title { get; } = "Game Location";

    /// <summary>
    ///     The game's install folder, edited the same way as on the Game settings page.
    /// </summary>
    public InstallFolderEditor InstallFolder { get; } = new(settings, persistence, folderPicker);

    public override bool CanContinue =>
        InstallRoot.Validate(settings.GameFolderPath) == ValidationResult.Success;
}
