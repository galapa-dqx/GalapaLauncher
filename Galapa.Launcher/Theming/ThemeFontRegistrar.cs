using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace Galapa.Launcher.Theming;

public static class ThemeFontRegistrar
{
    private static bool _registered;

    public static void RegisterBuiltIns()
    {
        if (_registered) return;
        foreach (var id in new[] { "estella", "duston", "kyururu" })
        {
            var collection = new EmbeddedFontCollection(
                new Uri($"fonts:{id}", UriKind.Absolute),
                new Uri($"avares://Galapa.Launcher/Assets/ThemeSources/{id}/fonts", UriKind.Absolute));
            FontManager.Current.AddFontCollection(collection);
        }
        _registered = true;
    }
}
