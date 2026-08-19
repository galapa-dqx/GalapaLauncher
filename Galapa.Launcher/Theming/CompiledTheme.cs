using System.Text.Json;
using System.Text.Json.Serialization;

namespace Galapa.Launcher.Theming;

/// <summary>
/// The resolved, renderer-facing theme contract emitted by themes.galapa.app.
/// There are deliberately no palette references or executable resources here:
/// every paint and every SVG is already literal and self-contained.
/// </summary>
public sealed record CompiledTheme
{
    public required string Label { get; init; }
    public CompiledThemeMeta? Meta { get; init; }
    public required string Mode { get; init; }
    public required CompiledFocusRing FocusRing { get; init; }
    public required Dictionary<string, CompiledControl> Controls { get; init; }
}

public sealed record CompiledThemeMeta
{
    public string? Description { get; init; }
    public string? Maintainer { get; init; }
    public string? Version { get; init; }
    public string? UpdateUrl { get; init; }
    public string? PreviewImage { get; init; }
}

public sealed record CompiledFocusRing
{
    public required string Color { get; init; }
    public double Width { get; init; }
    public double Offset { get; init; }
}

/// <summary>
/// A deliberately flat representation of the compiled control union. Fields
/// that are not valid for a control's Shape remain null/undefined and are
/// rejected by <see cref="CompiledThemeReader"/>.
/// </summary>
public sealed record CompiledControl
{
    [JsonIgnore]
    public string ThemeId { get; internal set; } = string.Empty;
    public string? Shape { get; init; }
    public string? Fill { get; init; }
    public string? Content { get; init; }
    public string? BorderColor { get; init; }
    public JsonElement BorderThickness { get; init; }
    public JsonElement Radius { get; init; }
    public string? Corner { get; init; }
    public JsonElement Padding { get; init; }
    public double? Opacity { get; init; }
    public CompiledTextStyle? Text { get; init; }
    public CompiledSize? Size { get; init; }
    public string? Image { get; init; }
    public string? Art { get; init; }
    public double? LeftInset { get; init; }
    public Dictionary<string, string>? Images { get; init; }
    public Dictionary<string, CompiledControl>? States { get; init; }
    public bool? ShowRing { get; init; }
}

public sealed record CompiledTextStyle
{
    public string? Family { get; init; }
    public string? Fallback { get; init; }
    public int? Weight { get; init; }
    public string? Style { get; init; }
    public double? Size { get; init; }
    public double? LetterSpacing { get; init; }
    public string? Case { get; init; }
}

public sealed record CompiledSize
{
    public double? Width { get; init; }
    public double? Height { get; init; }
}

public enum ThemePartState
{
    Normal,
    Hover,
    Pressed,
    Focused,
    Disabled,
    Selected,
    Checked
}

public static class CompiledThemeContract
{
    public static readonly IReadOnlyDictionary<string, CompiledControlSpec> Controls =
        new Dictionary<string, CompiledControlSpec>(StringComparer.Ordinal)
        {
            ["window"] = new("Window"),
            ["panel"] = new(),
            ["button"] = new(Typography: Heading(16, 600)),
            ["input"] = new(Typography: Body(16, 400)),
            ["tab"] = new(Typography: Heading(16, 600)),
            ["carousel"] = new(),
            ["pip"] = new(),
            ["switch.track"] = new(),
            ["switch.thumb"] = new(),
            ["carousel.nav"] = new(),
            ["titlebar.caption"] = new(),
            ["titlebar.close"] = new(),
            ["news-item"] = new(Typography: Heading(16, 600)),
            ["setting-row"] = new(Typography: Heading(14, 600)),
            ["titlebar"] = new(),
            ["subtabs"] = new(),
            ["scrollbar.track"] = new(),
            ["scrollbar.thumb"] = new(),
            ["progress.track"] = new(),
            ["progress.indicator"] = new(),
            ["play-ornament"] = new(),
            ["input.label"] = new("Text", Heading(12, 600)),
            ["news-item.date"] = new("Text", Body(13, 400)),
            ["news-item.gem"] = new("Text", Heading(14, 600)),
            ["titlebar.wordmark"] = new("Text", Heading(20, 600)),
            ["tab-bar"] = new("Text", Heading(16, 600)),
            ["settings.heading"] = new("Text", Heading(16, 600)),
            ["setting-help.title"] = new("Text", Heading(16, 600)),
            ["setting-help.body"] = new("Text", Body(14, 400)),
            ["input.placeholder"] = new("Text", Body(16, 400)),
            ["input.caret"] = new("Text", Body(16, 400)),
            ["input.error"] = new("Text", Body(13, 400)),
            ["play-row"] = new("Text", Heading(16, 600))
        };

    public static readonly HashSet<string> States =
    [
        "hover", "pressed", "focused", "disabled", "selected", "checked"
    ];

    public static string? StateName(ThemePartState state) => state switch
    {
        ThemePartState.Normal => null,
        ThemePartState.Hover => "hover",
        ThemePartState.Pressed => "pressed",
        ThemePartState.Focused => "focused",
        ThemePartState.Disabled => "disabled",
        ThemePartState.Selected => "selected",
        ThemePartState.Checked => "checked",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
    };

    public static ThemePartState PartState(string state) => state switch
    {
        "hover" => ThemePartState.Hover,
        "pressed" => ThemePartState.Pressed,
        "focused" => ThemePartState.Focused,
        "disabled" => ThemePartState.Disabled,
        "selected" => ThemePartState.Selected,
        "checked" => ThemePartState.Checked,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
    };

    public static ResolvedControlVisual ResolveVisual(CompiledControl control, CompiledControl? state = null) =>
        new(
            state?.Fill ?? control.Fill,
            state?.Content ?? control.Content,
            state?.BorderColor ?? control.BorderColor,
            ThemeMetrics.HasValue(state?.BorderThickness ?? default) ? state!.BorderThickness : control.BorderThickness,
            state?.Opacity ?? control.Opacity ?? 1,
            state?.Image ?? control.Image,
            state?.Art ?? control.Art);

    private static TypographyDefaults Heading(double size, int weight) =>
        new("titlebar.wordmark", size, weight);

    private static TypographyDefaults Body(double size, int weight) =>
        new("input", size, weight);
}

public sealed record TypographyDefaults(string FamilySourceControlId, double Size, int Weight);
public sealed record CompiledControlSpec(string? RequiredShape = null, TypographyDefaults? Typography = null);
public readonly record struct ResolvedControlVisual(
    string? Fill,
    string? Content,
    string? BorderColor,
    JsonElement BorderThickness,
    double Opacity,
    string? Image,
    string? Art);

public readonly record struct ThemeEdges(double Top, double Right, double Bottom, double Left)
{
    public bool HasAny => Top > 0 || Right > 0 || Bottom > 0 || Left > 0;
    public bool IsUniform => Math.Abs(Right - Top) < .001 && Math.Abs(Bottom - Top) < .001 &&
                             Math.Abs(Left - Top) < .001;
}

public readonly record struct ThemeRadius(bool IsPill, double Value)
{
    public double Resolve(double width, double height) => IsPill ? Math.Min(width, height) / 2 : Value;
}

public sealed record NormalizedControlVisual(
    ThemeColor? Fill,
    ThemeColor? Content,
    bool ContentInherited,
    ThemeColor? BorderColor,
    ThemeEdges Border,
    double Opacity,
    string? Image,
    string? Art);

public sealed record NormalizedCompiledControl(
    CompiledControl Source,
    ThemeEdges Padding,
    ThemeRadius Radius,
    NormalizedControlVisual Normal,
    IReadOnlyDictionary<string, NormalizedControlVisual> States);

public static class CompiledThemeNormalizer
{
    public static IReadOnlyDictionary<string, NormalizedCompiledControl> Normalize(CompiledTheme theme) =>
        theme.Controls.ToDictionary(pair => pair.Key, pair => Normalize(pair.Value), StringComparer.Ordinal);

    public static NormalizedCompiledControl Normalize(CompiledControl source)
    {
        var normal = NormalizeVisual(CompiledThemeContract.ResolveVisual(source));
        var states = source.States?.ToDictionary(
            state => state.Key,
            state => NormalizeVisual(CompiledThemeContract.ResolveVisual(source, state.Value)),
            StringComparer.Ordinal) ?? new Dictionary<string, NormalizedControlVisual>(StringComparer.Ordinal);
        var radius = source.Radius.ValueKind == JsonValueKind.String
            ? new ThemeRadius(true, 0)
            : new ThemeRadius(false, source.Radius.ValueKind == JsonValueKind.Number ? source.Radius.GetDouble() : 0);
        return new NormalizedCompiledControl(source, Edges(source.Padding), radius, normal, states);
    }

    private static NormalizedControlVisual NormalizeVisual(ResolvedControlVisual visual) => new(
        Color(visual.Fill), visual.Content == "inherit" ? null : Color(visual.Content),
        visual.Content == "inherit", Color(visual.BorderColor), Edges(visual.BorderThickness), visual.Opacity,
        visual.Image, visual.Art);

    private static ThemeColor? Color(string? value) => value is null ? null : ThemeColor.Parse(value);

    private static ThemeEdges Edges(JsonElement value)
    {
        var edges = ThemeMetrics.ReadEdges(value);
        return new ThemeEdges(edges[0], edges[1], edges[2], edges[3]);
    }
}
