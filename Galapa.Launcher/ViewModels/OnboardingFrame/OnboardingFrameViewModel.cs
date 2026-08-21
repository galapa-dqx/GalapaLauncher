using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Galapa.Core.Configuration;
using Galapa.Launcher.Theming;

namespace Galapa.Launcher.ViewModels.OnboardingFrame;

public partial class OnboardingFrameViewModel(Settings settings, ISettingsPersistence persistence) : ObservableObject
{
    [ObservableProperty] private string? _gameFolderPath = settings.GameFolderPath;
    [ObservableProperty] private string? _validationMessage;
    public event EventHandler? Completed;

    [RelayCommand]
    private async Task CompleteAsync()
    {
        if (!Settings.IsValidGameFolder(GameFolderPath))
        {
            ValidationMessage = $"Choose the Dragon Quest X folder containing {Settings.GameExecutableDisplayPath}.";
            return;
        }

        var previous = settings.GameFolderPath;
        try
        {
            settings.GameFolderPath = GameFolderPath;
            await persistence.SaveAsync(settings);
            ValidationMessage = null;
            Completed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            settings.GameFolderPath = previous;
            ValidationMessage = $"Galapa could not save this folder: {ex.Message}";
        }
    }
}
