using System.Text.Json.Serialization;

namespace Galapa.Launcher.Theming;

public enum ThemeBaseVariant { Light, Dark }
public enum ThemeFontSource { Heading, Body }
public enum ThemeFontStyle { Normal, Italic, Oblique }
public enum ThemeTextTransform { Original, Upper, Lower, Title }
public enum ThemeShapeVariant { Rounded, Angular, Pill, Circle, Asset }

public sealed record ThemeManifest
{
    public int SchemaVersion { get; init; } = 1;
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Author { get; init; }
    public required string PackageVersion { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ThemeBaseVariant BaseVariant { get; init; }
    public required ThemeColors Colors { get; init; }
    public required ThemeFonts Fonts { get; init; }
    public required Dictionary<string, TypographyToken> Typography { get; init; }
    public required Dictionary<string, GeometryToken> Geometry { get; init; }
    public required Dictionary<string, string> Assets { get; init; }
}

public sealed record ThemeColors
{
    public required string Background { get; init; }
    public required string Surface { get; init; }
    public required string SecondarySurface { get; init; }
    public required string Border { get; init; }
    public required string Text { get; init; }
    public required string Muted { get; init; }
    public required string Accent { get; init; }
    public required string Success { get; init; }
    public required string Danger { get; init; }
}

public sealed record ThemeFonts
{
    public required ThemeFont Heading { get; init; }
    public required ThemeFont Body { get; init; }
    public double HeadingBaseSize { get; init; } = 12;
    public double BodyBaseSize { get; init; } = 11;
}

public sealed record ThemeFont
{
    public required string File { get; init; }
    public required string Family { get; init; }
    public string? License { get; init; }
}

public sealed record TypographyToken
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ThemeFontSource Family { get; init; }
    public double SizeScale { get; init; } = 1;
    public int Weight { get; init; } = 400;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ThemeFontStyle Style { get; init; }
    public double LetterSpacing { get; init; }
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ThemeTextTransform Transform { get; init; }
}

public sealed record GeometryToken
{
    public double Radius { get; init; }
    public double BorderThickness { get; init; } = 1;
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ThemeShapeVariant Shape { get; init; }
}
