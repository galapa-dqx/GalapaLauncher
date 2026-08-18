using System.Reflection;
using Galapa.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Galapa.Launcher.Theming;

public sealed class ThemeCatalog(CompiledThemeReader compiledReader, ILogger<ThemeCatalog> logger) : IThemeCatalog
{
    private readonly List<ThemePackage> _themes = [];
    private readonly object _sync = new();

    public IReadOnlyList<ThemePackage> Themes
    {
        get { lock (_sync) return _themes.ToArray(); }
    }

    public event EventHandler? ThemesChanged;

    public ThemePackage? Find(string id)
    {
        lock (_sync)
            return _themes.FirstOrDefault(theme =>
                string.Equals(theme.Manifest.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Admits only the selected package and Estella recovery before first
    /// paint. Remaining built-ins are intentionally deferred.
    /// </summary>
    public async Task LoadInitialAsync(string preferredThemeId, CancellationToken cancellationToken = default)
    {
        lock (_sync) _themes.Clear();
        var preferred = SafeThemeId(preferredThemeId) ? preferredThemeId : Settings.DefaultThemeId;

        if (!string.Equals(preferred, Settings.DefaultThemeId, StringComparison.Ordinal))
        {
            var selected = await TryLoadDeployedAsync(preferred, cancellationToken);
            if (selected is not null) Add(selected);
        }

        var deployedDefault = await TryLoadDeployedAsync(Settings.DefaultThemeId, cancellationToken);
        Add(deployedDefault ?? await LoadRecoveryAsync(cancellationToken), first: true);
        ThemesChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task LoadRemainingAsync(CancellationToken cancellationToken = default)
    {
        var known = Themes.Select(theme => theme.Manifest.Id).ToHashSet(StringComparer.Ordinal);
        var candidates = ThemeLocations.EnumerateCompiledPaths()
            .Where(path => !known.Contains(ThemeLocations.IdFromPath(path)))
            .Select(path => TryLoadPathAsync(path, cancellationToken))
            .ToArray();
        var loaded = await Task.WhenAll(candidates);
        var changed = false;
        foreach (var package in loaded.OfType<ThemePackage>().OrderBy(theme => theme.Manifest.Id, StringComparer.Ordinal))
            changed |= Add(package);
        if (changed) ThemesChanged?.Invoke(this, EventArgs.Empty);
    }

    private async Task<ThemePackage?> TryLoadDeployedAsync(string id, CancellationToken cancellationToken)
    {
        var path = ThemeLocations.PathFor(id);
        return File.Exists(path) ? await TryLoadPathAsync(path, cancellationToken) : null;
    }

    private async Task<ThemePackage?> TryLoadPathAsync(string path, CancellationToken cancellationToken)
    {
        try { return await compiledReader.ReadAsync(path, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not load theme package {ThemePath}", path);
            return null;
        }
    }

    private bool Add(ThemePackage package, bool first = false)
    {
        lock (_sync)
        {
            if (_themes.Any(theme => string.Equals(theme.Manifest.Id, package.Manifest.Id, StringComparison.Ordinal)))
            {
                logger.LogError("Could not load duplicate theme ID {ThemeId} from {ThemePath}",
                    package.Manifest.Id, package.SourcePath);
                return false;
            }
            if (first) _themes.Insert(0, package);
            else _themes.Add(package);
            return true;
        }
    }

    private async Task<ThemePackage> LoadRecoveryAsync(CancellationToken cancellationToken)
    {
        const string resourceName = "Galapa.Launcher.Recovery.estella.compiled.json";
        await using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
                                 ?? throw new ThemePackageException("The embedded Estella recovery theme is missing.");
        var recovery = await compiledReader.ReadAsync(stream, Settings.DefaultThemeId,
            "embedded recovery theme", cancellationToken);
        logger.LogWarning("The deployed Estella theme was unavailable; the embedded recovery copy was loaded.");
        return recovery;
    }

    private static bool SafeThemeId(string? id) => id is { Length: > 0 and <= 100 } &&
        id.All(character => char.IsLower(character) || char.IsDigit(character) || character == '-');
}
