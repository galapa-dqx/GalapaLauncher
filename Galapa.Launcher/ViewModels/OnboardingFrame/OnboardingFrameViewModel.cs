using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Galapa.Core.Configuration;
using Galapa.Launcher.ViewModels.AppFrame;

namespace Galapa.Launcher.ViewModels.OnboardingFrame;

public partial class OnboardingFrameViewModel(Settings settings) : ObservableObject
{
    [ObservableProperty] private string? _gameFolderPath = settings.GameFolderPath;
    [ObservableProperty] private string? _validationMessage;
    public event EventHandler? Completed;

    [RelayCommand]
    private void Complete()
    {
        if (!AppFrameViewModel.IsValidGameFolder(GameFolderPath))
        {
            ValidationMessage = "Choose the Dragon Quest X folder containing Game\\DQXGame.exe.";
            return;
        }

        settings.GameFolderPath = GameFolderPath;
        settings.Save();
        ValidationMessage = null;
        Completed?.Invoke(this, EventArgs.Empty);
    }
}
