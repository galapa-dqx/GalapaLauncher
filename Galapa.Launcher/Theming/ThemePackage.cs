namespace Galapa.Launcher.Theming;

public sealed record ThemePackage(
    ThemeManifest Manifest,
    string ArchivePath,
    string CacheDirectory,
    IReadOnlyDictionary<string, string> Assets,
    CompiledTheme? Compiled = null)
{
    public string Resolve(string relativePath) => Path.Combine(
        CacheDirectory,
        relativePath.Replace('/', Path.DirectorySeparatorChar));
}

public interface IThemeCatalog
{
    IReadOnlyList<ThemePackage> Themes { get; }
    ThemePackage? Find(string id);
    Task LoadAsync(CancellationToken cancellationToken = default);
}

public interface IThemeManager
{
    ThemePackage? ActiveTheme { get; }
    event EventHandler<ThemePackage>? ThemeChanged;
    Task<bool> ApplyAsync(string themeId, bool persist = true, CancellationToken cancellationToken = default);
}
