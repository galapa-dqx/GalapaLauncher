namespace Galapa.Launcher.Theming;

/// <summary>
/// Owns the current loose built-in deployment layout. The compiled contract
/// and renderer deliberately do not depend on these locations, allowing a
/// future archive-backed catalog to replace this boundary.
/// </summary>
internal static class ThemeLocations
{
    public const string CompiledSuffix = ".compiled.json";
    public const string FontScheme = "fonts";

    public static string BuiltInFolder => Path.Combine(AppContext.BaseDirectory, "Assets", "Themes");

    public static IEnumerable<string> EnumerateCompiledPaths() =>
        Directory.Exists(BuiltInFolder)
            ? Directory.EnumerateFiles(BuiltInFolder, $"*{CompiledSuffix}").OrderBy(path => path, StringComparer.Ordinal)
            : [];

    public static string IdFromPath(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(CompiledSuffix, StringComparison.Ordinal)
            ? name[..^CompiledSuffix.Length]
            : Path.GetFileNameWithoutExtension(path);
    }

    public static Uri FontCollectionUri(ThemeId id) => new($"{FontScheme}:{id.Value}", UriKind.Absolute);

    public static Uri BuiltInFontAssetsUri(ThemeId id) =>
        new($"avares://Galapa.Launcher/Assets/ThemeSources/{id.Value}/fonts", UriKind.Absolute);

    public static string FontFamilyName(ThemeId id, string family) => $"{FontScheme}:{id.Value}#{family}";
}
