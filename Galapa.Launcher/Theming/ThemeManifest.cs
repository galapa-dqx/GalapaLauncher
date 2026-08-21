namespace Galapa.Launcher.Theming;

public enum ThemeBaseVariant { Light, Dark }

/// <summary>Catalog metadata derived from the renderer-facing compiled theme.</summary>
public sealed record ThemeManifest
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Author { get; init; }
    public required string PackageVersion { get; init; }
    public ThemeBaseVariant BaseVariant { get; init; }
}
