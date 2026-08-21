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

    public static void EnsureRegistered(ValidatedTheme theme)
    {
        lock (Sync)
        {
            var manager = FontManager.Current;
            var collections = Collections.GetValue(manager,
                _ => new HashSet<string>(StringComparer.Ordinal));
            var key = theme.EffectiveSource.Description;
            if (collections.Contains(key)) return;
            foreach (var source in theme.EffectiveSource.Fonts)
                manager.AddFontCollection(new EmbeddedFontCollection(source.CollectionUri, source.AssetsUri));
            collections.Add(key);
        }
    }

    public static void Validate(ValidatedTheme package)
    {
        EnsureRegistered(package);

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
                new FontFamily(ThemeLocations.FontFamilyName(new ThemeId(package.Manifest.Id), request.Family)),
                request.Style,
                request.Weight);
            if (!FontManager.Current.TryGetGlyphTypeface(typeface, out _))
                throw new ThemePackageException(
                    $"Theme '{package.Manifest.Id}' does not bundle font family '{request.Family}'.");
        }
    }
}
