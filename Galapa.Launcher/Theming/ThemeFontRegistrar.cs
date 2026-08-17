using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace Galapa.Launcher.Theming;

public static class ThemeFontRegistrar
{
    private static readonly Dictionary<string, IFontCollection> Collections = new(StringComparer.Ordinal);
    private static bool _registered;

    public static void RegisterBuiltIns()
    {
        if (_registered)
            return;

        var themeDirectory = Path.Combine(AppContext.BaseDirectory, "Assets", "Themes");
        IEnumerable<string> ids = Directory.Exists(themeDirectory)
            ? Directory.EnumerateFiles(themeDirectory, "*.compiled.json")
                .Select(path => Path.GetFileName(path)[..^".compiled.json".Length])
                .Append(Galapa.Core.Configuration.Settings.DefaultThemeId)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(id => id, StringComparer.Ordinal)
            : [Galapa.Core.Configuration.Settings.DefaultThemeId];

        foreach (var id in ids)
        {
            var collection = new EmbeddedFontCollection(
                new Uri($"fonts:{id}", UriKind.Absolute),
                new Uri($"avares://Galapa.Launcher/Assets/ThemeSources/{id}/fonts", UriKind.Absolute));
            FontManager.Current.AddFontCollection(collection);
            Collections.Add(id, collection);
        }
        _registered = true;
    }

    public static void Validate(ThemePackage package)
    {
        if (!Collections.TryGetValue(package.Manifest.Id, out var collection))
            throw new ThemePackageException($"Theme '{package.Manifest.Id}' has no independent font collection.");

        var requests = package.Compiled.Controls.Values
            .Select(control => control.Text)
            .Where(text => text?.Family is not null)
            .Select(text => new
            {
                Family = text!.Family!,
                Weight = (FontWeight)(text.Weight ?? 400),
                Style = ThemeTypography.ToFontStyle(text.Style)
            })
            .Distinct();

        foreach (var request in requests)
        {
            if (!collection.TryGetGlyphTypeface(request.Family, request.Style, request.Weight,
                    FontStretch.Normal, out var typeface))
                throw new ThemePackageException(
                    $"Theme '{package.Manifest.Id}' does not bundle font family '{request.Family}'.");
            typeface.Dispose();
        }
    }
}
