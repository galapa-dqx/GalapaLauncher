using CommunityToolkit.Mvvm.ComponentModel;

namespace Galapa.Launcher.ViewModels.SettingsFrame;

public sealed class SettingsFrameTab(string title, Lazy<SettingsFramePageViewModel> viewModel)
{
    public string Title { get; } = title;
    public Lazy<SettingsFramePageViewModel> ViewModel { get; } = viewModel;
}

public partial class SettingsFrameViewModel : ObservableObject
{
    [ObservableProperty] private SettingsFrameTab? _selectedPage;

    public SettingsFrameViewModel(Lazy<GeneralSettingsPageViewModel> launcher, Lazy<GameSettingsPageViewModel> game,
        Lazy<GraphicsSettingsPageViewModel> graphics, Lazy<AboutPageViewModel> about)
    {
        Pages =
        [
            new("Launcher", new Lazy<SettingsFramePageViewModel>(() => launcher.Value)),
            new("Game", new Lazy<SettingsFramePageViewModel>(() => game.Value)),
            ComingSoon("Players", "Player management is planned. Existing saved players remain available from the launcher."),
            new("Graphics", new Lazy<SettingsFramePageViewModel>(() => graphics.Value)),
            ComingSoon("Controls", "Controller and keyboard mapping will live here once the safe configuration backend is ready."),
            ComingSoon("Sound", "Sound configuration is not available in this version."),
            ComingSoon("Clarity", "Accessibility and clarity options are being designed for a later release."),
            new("About", new Lazy<SettingsFramePageViewModel>(() => about.Value))
        ];
        SelectedPage = Pages[0];
    }

    public List<SettingsFrameTab> Pages { get; }

    private static SettingsFrameTab ComingSoon(string title, string description) =>
        new(title, new Lazy<SettingsFramePageViewModel>(() => new ComingSoonPageViewModel(title, description)));
}
