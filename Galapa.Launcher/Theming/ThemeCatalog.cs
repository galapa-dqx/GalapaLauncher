using Galapa.Core.Configuration;
using Galapa.Launcher.Views.Controls;
using Microsoft.Extensions.Logging;
using System.Reflection;

namespace Galapa.Launcher.Theming;

public sealed class ThemeCatalog(CompiledThemeReader compiledReader, ILogger<ThemeCatalog> logger) : IThemeCatalog
{
    private readonly List<ThemePackage> _themes = [];
    public IReadOnlyList<ThemePackage> Themes => _themes;

    public ThemePackage? Find(string id) => _themes.FirstOrDefault(x => string.Equals(x.Manifest.Id, id, StringComparison.OrdinalIgnoreCase));

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        _themes.Clear();
        var folder = Path.Combine(AppContext.BaseDirectory, "Assets", "Themes");
        var compiledPaths = Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.compiled.json").OrderBy(x => x).ToArray()
            : [];
        if (compiledPaths.Length == 0)
            logger.LogWarning("No deployed compiled themes were found in {ThemeFolder}; loading the embedded recovery theme", folder);
        foreach (var path in compiledPaths)
        {
            try
            {
                var package = await compiledReader.ReadAsync(path, cancellationToken);
                ThemeFontRegistrar.Validate(package);
                if (_themes.Any(x => x.Manifest.Id == package.Manifest.Id)) throw new ThemePackageException($"Duplicate theme ID: {package.Manifest.Id}");
                _themes.Add(package);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not load theme package {ThemePath}", path);
            }
        }

        if (Find(Settings.DefaultThemeId) is null)
            _themes.Insert(0, await LoadRecoveryAsync(cancellationToken));
    }

    /// <summary>
    /// Completes package admission on Avalonia's UI thread. Geometry objects
    /// are dispatcher-affine even though JSON, XML, and font validation are
    /// not, so this phase cannot be folded into the background I/O pass.
    /// </summary>
    public void PrepareRenderAssets()
    {
        foreach (var package in _themes.ToArray())
        {
            try { ThemeRenderAssets.Prepare(package); }
            catch (Exception ex)
            {
                _themes.Remove(package);
                logger.LogError(ex, "Theme {ThemeId} contains render assets Avalonia cannot prepare", package.Manifest.Id);
            }
        }

        if (Find(Settings.DefaultThemeId) is not null) return;
        var recovery = Task.Run(() => LoadRecoveryAsync(CancellationToken.None)).GetAwaiter().GetResult();
        ThemeRenderAssets.Prepare(recovery);
        _themes.Insert(0, recovery);
    }

    private async Task<ThemePackage> LoadRecoveryAsync(CancellationToken cancellationToken)
    {
        const string resourceName = "Galapa.Launcher.Recovery.estella.compiled.json";
        await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
                                 ?? throw new ThemePackageException("The embedded Estella recovery theme is missing.");
        var recovery = await compiledReader.ReadAsync(stream, Settings.DefaultThemeId, "embedded recovery theme", cancellationToken);
        ThemeFontRegistrar.Validate(recovery);
        logger.LogWarning("The deployed Estella theme was unavailable; the embedded recovery copy was loaded.");
        return recovery;
    }
}
