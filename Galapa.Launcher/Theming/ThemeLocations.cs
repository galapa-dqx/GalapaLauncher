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

    public static string PathFor(string id) => Path.Combine(BuiltInFolder, $"{id}{CompiledSuffix}");

    public static string IdFromPath(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(CompiledSuffix, StringComparison.Ordinal)
            ? name[..^CompiledSuffix.Length]
            : Path.GetFileNameWithoutExtension(path);
    }

    public static Uri FontCollectionUri(string id) => new($"{FontScheme}:{id}", UriKind.Absolute);

    public static Uri BuiltInFontAssetsUri(string id) =>
        new($"avares://Galapa.Launcher/Assets/ThemeSources/{id}/fonts", UriKind.Absolute);

    public static string FontFamilyName(string id, string family) => $"{FontScheme}:{id}#{family}";
}
