namespace Galapa.Launcher.Theming;

public sealed record ThemePackage(
    ThemeManifest Manifest,
    string SourcePath,
    CompiledTheme Compiled);

public interface IThemeCatalog
{
    IReadOnlyList<ThemePackage> Themes { get; }
    event EventHandler? ThemesChanged;
    ThemePackage? Find(string id);
    Task LoadInitialAsync(string preferredThemeId, CancellationToken cancellationToken = default);
    Task LoadRemainingAsync(CancellationToken cancellationToken = default);
}

public interface IThemeManager
{
    ThemePackage? ActiveTheme { get; }
    Task<bool> ApplyAsync(string themeId, bool persist = true, CancellationToken cancellationToken = default);
    Task<bool> ApplyInitialAsync(string themeId, CancellationToken cancellationToken = default);
}

public sealed class ThemePackageException(string message) : Exception(message);
