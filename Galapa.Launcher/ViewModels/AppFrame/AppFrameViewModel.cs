using CommunityToolkit.Mvvm.ComponentModel;
using Galapa.Core.Configuration;
using Galapa.Launcher.ViewModels.OnboardingFrame;

namespace Galapa.Launcher.ViewModels.AppFrame;

public sealed class AppFrameTab(Lazy<AppPageViewModel> viewModel, string title)
{
    public Lazy<AppPageViewModel> ViewModel { get; } = viewModel;
    public string Title { get; } = title;
}

public partial class AppFrameViewModel : ObservableObject
{
    [ObservableProperty] private AppFrameTab? _selectedPage;
    [ObservableProperty] private bool _isOnboarding;

    public AppFrameViewModel(Settings settings, Lazy<HomePageViewModel> homePage,
        Lazy<SettingsPageViewModel> settingsPage, OnboardingFrameViewModel onboarding)
    {
        Onboarding = onboarding;
        Pages =
        [
            new(new Lazy<AppPageViewModel>(() => homePage.Value), "Launcher"),
            new(new Lazy<AppPageViewModel>(() => settingsPage.Value), "Settings")
        ];
        SelectedPage = Pages[0];
        IsOnboarding = !Settings.IsValidGameFolder(settings.GameFolderPath);
        onboarding.Completed += (_, _) => IsOnboarding = false;
    }

    public List<AppFrameTab> Pages { get; }
    public OnboardingFrameViewModel Onboarding { get; }
}
