using CommunityToolkit.Mvvm.ComponentModel;
using Galapa.Core.Configuration;
using Galapa.Launcher.ViewModels.OnboardingFrame;

namespace Galapa.Launcher.ViewModels.AppFrame;

public sealed class AppFrameTab(Lazy<AppPageViewModel> viewModel, string title, string icon)
{
    public Lazy<AppPageViewModel> ViewModel { get; } = viewModel;
    public string Title { get; } = title;
    public string Icon { get; } = icon;
}

public partial class AppFrameViewModel : ObservableObject
{
    private readonly Settings _settings;
    [ObservableProperty] private AppFrameTab? _selectedPage;
    [ObservableProperty] private bool _isOnboarding;

    public AppFrameViewModel(Settings settings, Lazy<HomePageViewModel> homePage,
        Lazy<SettingsPageViewModel> settingsPage, OnboardingFrameViewModel onboarding)
    {
        _settings = settings;
        Onboarding = onboarding;
        Pages =
        [
            new(new Lazy<AppPageViewModel>(() => homePage.Value), "Launcher", "/Assets/Icons/solar--rocket-bold-duotone.svg"),
            new(new Lazy<AppPageViewModel>(() => settingsPage.Value), "Settings", "/Assets/Icons/solar--settings-bold-duotone.svg")
        ];
        SelectedPage = Pages[0];
        IsOnboarding = !IsValidGameFolder(settings.GameFolderPath);
        onboarding.Completed += (_, _) => IsOnboarding = false;
    }

    public List<AppFrameTab> Pages { get; }
    public OnboardingFrameViewModel Onboarding { get; }

    public static bool IsValidGameFolder(string? folder) =>
        !string.IsNullOrWhiteSpace(folder) && Directory.Exists(folder) && File.Exists(Path.Combine(folder, "Game", "DQXGame.exe"));
}
