using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Galapa.Core.Configuration;
using Galapa.Launcher.Theming;

namespace Galapa.Launcher.ViewModels.SettingsFrame;

/// <summary>
/// ViewModel for the game-related settings page.
/// </summary>
public partial class GameSettingsPageViewModel(Settings settings, ISettingsPersistence persistence) : SettingsFramePageViewModel
{
    [ObservableProperty] private Settings _settings = settings;
    [ObservableProperty] private string? _saveError;
    public string GameExecutableHelpText =>
        $"The install folder must contain {Galapa.Core.Configuration.Settings.GameExecutableDisplayPath}.";

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            await persistence.SaveAsync(Settings);
            SaveError = null;
        }
        catch (Exception ex)
        {
            SaveError = $"Galapa could not save the folder settings: {ex.Message}";
        }
    }
}
