using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Galapa.Launcher.Theming;

namespace Galapa.Launcher.ViewModels.SettingsFrame;

public partial class GeneralSettingsPageViewModel : SettingsFramePageViewModel
{
    private readonly IThemeManager _themeManager;
    [ObservableProperty] private string? _themeError;

    public GeneralSettingsPageViewModel(IThemeCatalog catalog, IThemeManager themeManager)
    {
        _themeManager = themeManager;
        SyncThemes(catalog);
        catalog.ThemesChanged += (_, _) => Dispatcher.UIThread.Post(() => SyncThemes(catalog));
    }

    public ObservableCollection<ThemeCatalogEntry> Themes { get; } = [];

    [RelayCommand]
    private async Task SelectTheme(ThemeCatalogEntry theme)
    {
        var result = await _themeManager.ApplyAsync(theme);
        if (result.Succeeded)
        {
            ThemeError = null;
            return;
        }
        var details = result.Diagnostics.Count == 0
            ? "The current theme was kept."
            : string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message));
        ThemeError = $"Could not apply {theme.DisplayName}. {details}";
    }

    private void SyncThemes(IThemeCatalog catalog)
    {
        if (Themes.SequenceEqual(catalog.Themes)) return;
        Themes.Clear();
        foreach (var entry in catalog.Themes) Themes.Add(entry);
    }
}
