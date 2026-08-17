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
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select the Dragon Quest X folder",
            AllowMultiple = false
        });
        if (folders.Count > 0 && DataContext is OnboardingFrameViewModel vm)
            vm.GameFolderPath = folders[0].TryGetLocalPath();
    }
}
