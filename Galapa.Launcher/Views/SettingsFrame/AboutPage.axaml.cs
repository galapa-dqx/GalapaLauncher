using Avalonia.Controls;
using Avalonia.Interactivity;
using Galapa.Launcher.ViewModels.SettingsFrame;

namespace Galapa.Launcher.Views.SettingsFrame;

public partial class AboutPage : UserControl
{
    public AboutPage()
    {
        this.InitializeComponent();
    }

    private async void OnCopyVersionClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AboutPageViewModel vm)
            return;

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
            await clipboard.SetTextAsync(vm.CopyText);
    }
}
