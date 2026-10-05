using System;
using CommunityToolkit.Mvvm.ComponentModel;
using Galapa.Core.Configuration;
using Galapa.Launcher.Input;

namespace Galapa.Launcher.ViewModels.OnboardingFrame;

public partial class OnboardingFrameViewModel : ObservableObject
{
    [ObservableProperty] private Type? _currentPage;
    [ObservableProperty] private Settings _settings;

    public OnboardingFrameViewModel(Settings settings)
    {
        this.Settings = settings;
    }

    /// <summary>
    ///     The navigation context that is active while onboarding is required. The app shell
    ///     enables it (and disables its own context) so controller navigation cannot reach the
    ///     hidden Launcher/Settings pages.
    /// </summary>
    public NavigationContext NavigationContext { get; } = new("Onboarding");

    public Type? NextPage
    {
        get
        {
            // if (this.Settings.GameFolderPath is null) return typeof(SelectGameFolderPage);

            if (this.Settings.GameFolderPath is null)
            {
            }

            return null;
        }
    }
}
