using Galapa.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Galapa.Launcher.Theming;

public sealed class ThemeCatalog(ThemePackageReader reader, CompiledThemeReader compiledReader, ILogger<ThemeCatalog> logger) : IThemeCatalog
{
    private readonly List<ThemePackage> _themes = [];
    public IReadOnlyList<ThemePackage> Themes => _themes;

    public ThemePackage? Find(string id) => _themes.FirstOrDefault(x => string.Equals(x.Manifest.Id, id, StringComparison.OrdinalIgnoreCase));

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        _themes.Clear();
        var folder = Path.Combine(AppContext.BaseDirectory, "Assets", "Themes");
        if (!Directory.Exists(folder)) throw new ThemePackageException($"Built-in theme folder was not deployed: {folder}");

        var compiledPaths = Directory.EnumerateFiles(folder, "*.compiled.json").OrderBy(x => x).ToArray();
        foreach (var path in compiledPaths)
        {
            try
            {
                var package = await compiledReader.ReadAsync(path, cancellationToken);
                if (_themes.Any(x => x.Manifest.Id == package.Manifest.Id)) throw new ThemePackageException($"Duplicate theme ID: {package.Manifest.Id}");
                _themes.Add(package);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not load theme package {ThemePath}", path);
            }
        }

        // Developer/source checkouts created before the compiled contract may
        // still contain only the v1 archives. Keep that path as a recovery aid;
        // deployed builds always take the compiled files above.
        if (compiledPaths.Length == 0)
            foreach (var path in Directory.EnumerateFiles(folder, "*.galapatheme").OrderBy(x => x))
            {
                try
                {
                    var package = await reader.ReadAsync(path, Path.Combine(Paths.Cache, "Themes"), cancellationToken);
                    if (_themes.Any(x => x.Manifest.Id == package.Manifest.Id)) throw new ThemePackageException($"Duplicate theme ID: {package.Manifest.Id}");
                    _themes.Add(package);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Could not load legacy theme package {ThemePath}", path);
                }
            }

        if (Find("estella") is null) throw new ThemePackageException("The required Estella recovery theme could not be loaded.");
    }
}
