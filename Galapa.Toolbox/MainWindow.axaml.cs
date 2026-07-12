using Avalonia.Controls;
using Galapa.Toolbox.Views;

namespace Galapa.Toolbox;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        this.InitializeComponent();
        this.ContentArea.Content = new HomePage();
    }

    private void NavList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (this.ContentArea is null) return;
        if (this.NavList.SelectedItem is ListBoxItem selectedItem)
            this.ContentArea.Content = selectedItem.Tag switch
            {
                "Home" => new HomePage(),
                "Launcher" => new LauncherPage(),
                "SaveExplorer" => new SaveExplorerPage(),
                "GameExplorer" => new GameExplorerPage(),
                "Obfuscator" => new ObfuscatorToolPage(),
                "Settings" => new SettingsPage(),
                _ => this.ContentArea.Content
            };
    }
}