using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Media.Fonts;

namespace Galapa.Launcher.Theming;

public static class ThemeFontRegistrar
{
    // FontManager.Current belongs to the active Avalonia application. Keeping a
    // process-wide theme-id map can therefore claim a collection is registered
    // after a headless/test application (or future application host) replaces
    // the manager that actually owned it.
    private static readonly ConditionalWeakTable<FontManager, HashSet<string>> Collections = new();
    private static readonly object Sync = new();

    public static void EnsureRegistered(string themeId)
    {
        lock (Sync)
        {
            var manager = FontManager.Current;
            var collections = Collections.GetValue(manager,
                _ => new HashSet<string>(StringComparer.Ordinal));
            if (collections.Contains(themeId)) return;
            var collection = new EmbeddedFontCollection(
                ThemeLocations.FontCollectionUri(themeId),
                ThemeLocations.BuiltInFontAssetsUri(themeId));
            manager.AddFontCollection(collection);
            collections.Add(themeId);
        }
    }

    public static void Validate(ThemePackage package)
    {
        EnsureRegistered(package.Manifest.Id);

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
            // Validate through the same URI-qualified Typeface the controls use.
            // Querying the collection directly can succeed while a bad manager
            // registration/key remains invisible to Avalonia's text formatter.
            var typeface = new Typeface(
                new FontFamily(ThemeLocations.FontFamilyName(package.Manifest.Id, request.Family)),
                request.Style,
                request.Weight);
            if (!FontManager.Current.TryGetGlyphTypeface(typeface, out _))
                throw new ThemePackageException(
                    $"Theme '{package.Manifest.Id}' does not bundle font family '{request.Family}'.");
        }
    }
}
