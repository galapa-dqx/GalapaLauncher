using System.Reflection;

namespace Galapa.Launcher.Theming;

internal sealed class LooseBuiltInThemeSource(string path) : IThemeSource
{
    public string SourceId { get; } = ThemeLocations.IdFromPath(path);
    public string FallbackDisplayName { get; } = ThemeLocations.IdFromPath(path);
    public string Description { get; } = path;
    public IReadOnlyList<ThemeFontSourceDescriptor> Fonts { get; } =
        ThemeId.TryParse(ThemeLocations.IdFromPath(path), out var id)
            ? [new ThemeFontSourceDescriptor(ThemeLocations.FontCollectionUri(id), ThemeLocations.BuiltInFontAssetsUri(id))]
            : [];

    public ValueTask<Stream> OpenCompiledJsonAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<Stream>(File.OpenRead(Description));
    }
}

internal sealed class EmbeddedRecoveryThemeSource : IThemeSource
{
    private const string ResourceName = "Galapa.Launcher.Recovery.estella.compiled.json";
    private static readonly ThemeId Estella = new("estella");

    public string SourceId => Estella.Value;
    public string FallbackDisplayName => "Estella";
    public string Description => "embedded Estella recovery theme";
    public IReadOnlyList<ThemeFontSourceDescriptor> Fonts { get; } =
        [new ThemeFontSourceDescriptor(ThemeLocations.FontCollectionUri(Estella), ThemeLocations.BuiltInFontAssetsUri(Estella))];

    public ValueTask<Stream> OpenCompiledJsonAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
                     ?? throw new FileNotFoundException("The embedded Estella recovery theme is missing.", ResourceName);
        return ValueTask.FromResult(stream);
    }
}

internal sealed class MissingThemeSource(ThemeId id) : IThemeSource
{
    public string SourceId => id.Value;
    public string FallbackDisplayName => id.Value;
    public string Description => $"missing saved theme '{id.Value}'";
    public IReadOnlyList<ThemeFontSourceDescriptor> Fonts => [];

    public ValueTask<Stream> OpenCompiledJsonAsync(CancellationToken cancellationToken = default) =>
        ValueTask.FromException<Stream>(new FileNotFoundException($"Saved theme '{id.Value}' is not installed."));
}

internal static class ThemeSourceDiscovery
{
    public static IReadOnlyList<IThemeSource> BuiltIns() =>
        ThemeLocations.EnumerateCompiledPaths().Select(path => (IThemeSource)new LooseBuiltInThemeSource(path)).ToArray();

    public static IThemeSource Recovery() => new EmbeddedRecoveryThemeSource();
}
