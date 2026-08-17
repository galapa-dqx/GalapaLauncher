namespace Galapa.Launcher.Theming;

public sealed record ThemePackage(
    ThemeManifest Manifest,
    string SourcePath,
    CompiledTheme Compiled);

public interface IThemeCatalog
{
    IReadOnlyList<ThemePackage> Themes { get; }
    ThemePackage? Find(string id);
    Task LoadAsync(CancellationToken cancellationToken = default);
    void PrepareRenderAssets();
}

public interface IThemeManager
{
    ThemePackage? ActiveTheme { get; }
    Task<bool> ApplyAsync(string themeId, bool persist = true, CancellationToken cancellationToken = default);
}

public sealed class ThemePackageException(string message) : Exception(message);
