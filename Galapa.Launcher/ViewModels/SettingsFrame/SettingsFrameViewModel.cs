using CommunityToolkit.Mvvm.ComponentModel;

namespace Galapa.Launcher.ViewModels.SettingsFrame;

public sealed class SettingsFrameTab(Lazy<SettingsFramePageViewModel> viewModel)
{
    public Lazy<SettingsFramePageViewModel> ViewModel { get; } = viewModel;
    public string Title => ViewModel.Value.Title;
    public string Icon => ViewModel.Value.Icon;
}

public partial class SettingsFrameViewModel : ObservableObject
{
    [ObservableProperty] private SettingsFrameTab? _selectedPage;

    public SettingsFrameViewModel(Lazy<GeneralSettingsPageViewModel> launcher, Lazy<GameSettingsPageViewModel> game,
        Lazy<AboutPageViewModel> about)
    {
        const string settingsIcon = "/Assets/Icons/solar--settings-bold-duotone.svg";
        Pages =
        [
            new(new Lazy<SettingsFramePageViewModel>(() => launcher.Value)),
            new(new Lazy<SettingsFramePageViewModel>(() => game.Value)),
            ComingSoon("Players", "Player management is planned. Existing saved players remain available from the launcher.", "/Assets/Icons/solar--user-rounded-bold-duotone.svg"),
            new(new Lazy<SettingsFramePageViewModel>(() => new GraphicsSettingsPageViewModel())),
            ComingSoon("Controls", "Controller and keyboard mapping will live here once the safe configuration backend is ready.", settingsIcon),
            ComingSoon("Sound", "Sound configuration is not available in this version.", settingsIcon),
            ComingSoon("Clarity", "Accessibility and clarity options are being designed for a later release.", settingsIcon),
            new(new Lazy<SettingsFramePageViewModel>(() => about.Value))
        ];
        SelectedPage = Pages[0];
    }

    public List<SettingsFrameTab> Pages { get; }

    private static SettingsFrameTab ComingSoon(string title, string description, string icon) =>
        new(new Lazy<SettingsFramePageViewModel>(() => new ComingSoonPageViewModel(title, description, icon)));
}
