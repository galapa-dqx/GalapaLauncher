using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Galapa.Core.Configuration;
using Galapa.Launcher.Theming;

namespace Galapa.Launcher.ViewModels.SettingsFrame;

public partial class ThemeChoice(string id, string name, string author, ThemeBaseVariant variant, bool isActive) : ObservableObject
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public string Author { get; } = author;
    public ThemeBaseVariant Variant { get; } = variant;
    [ObservableProperty] private bool _isActive = isActive;
}

public partial class GeneralSettingsPageViewModel : SettingsFramePageViewModel
{
    private readonly IThemeManager _themeManager;
    [ObservableProperty] private Settings _settings;
    [ObservableProperty] private string? _themeError;

    public GeneralSettingsPageViewModel(Settings settings, IThemeCatalog catalog, IThemeManager themeManager)
    {
        _settings = settings;
        _themeManager = themeManager;
        SyncThemes(catalog);
        catalog.ThemesChanged += (_, _) => Dispatcher.UIThread.Post(() => SyncThemes(catalog));
    }

    public ObservableCollection<ThemeChoice> Themes { get; } = [];

    [RelayCommand]
    private async Task SelectTheme(ThemeChoice theme)
    {
        if (await _themeManager.ApplyAsync(theme.Id))
        {
            foreach (var choice in Themes) choice.IsActive = choice.Id == theme.Id;
            ThemeError = null;
        }
        else ThemeError = $"Could not apply {theme.Name}. The current theme was kept.";
    }

    private void SyncThemes(IThemeCatalog catalog)
    {
        var selectedId = _themeManager.ActiveTheme?.Manifest.Id ?? Settings.ThemeId;
        var existing = Themes.ToDictionary(theme => theme.Id, StringComparer.Ordinal);
        foreach (var package in catalog.Themes)
        {
            if (existing.TryGetValue(package.Manifest.Id, out var choice))
            {
                choice.IsActive = string.Equals(package.Manifest.Id, selectedId, StringComparison.OrdinalIgnoreCase);
                continue;
            }
            Themes.Add(new ThemeChoice(
                package.Manifest.Id,
                package.Manifest.DisplayName,
                package.Manifest.Author,
                package.Manifest.BaseVariant,
                string.Equals(package.Manifest.Id, selectedId, StringComparison.OrdinalIgnoreCase)));
        }
    }
}
