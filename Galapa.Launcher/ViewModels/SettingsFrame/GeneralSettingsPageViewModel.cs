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
        Themes = catalog.Themes.Select(x => new ThemeChoice(
            x.Manifest.Id,
            x.Manifest.DisplayName,
            x.Manifest.Author,
            x.Manifest.BaseVariant,
            string.Equals(x.Manifest.Id, settings.ThemeId, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    public override string Title => "Launcher";
    public override string Icon => "/Assets/Icons/solar--settings-bold-duotone.svg";
    public IReadOnlyList<ThemeChoice> Themes { get; }

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
}
