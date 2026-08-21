using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Galapa.Launcher.ViewModels.OnboardingFrame;

namespace Galapa.Launcher.Views.OnboardingFrame;

public partial class OnboardingFrame : UserControl
{
    public OnboardingFrame() => InitializeComponent();

    private async void Browse_Click(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top?.StorageProvider is null) return;
        if (DataContext is not OnboardingFrameViewModel vm) return;
        try
        {
            var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Select the Dragon Quest X folder",
                AllowMultiple = false
            });
            if (folders.Count == 0) return;
            var localPath = folders[0].TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(localPath))
            {
                vm.ValidationMessage = "The selected folder is not available as a local Windows path.";
                return;
            }
            vm.GameFolderPath = localPath;
            vm.ValidationMessage = null;
        }
        catch (Exception ex)
        {
            vm.ValidationMessage = $"Galapa could not open the folder picker: {ex.Message}";
        }
    }
}
