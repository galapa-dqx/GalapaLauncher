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
    public CompiledFocusRing? FocusRing { get; init; }
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
}

public sealed record CompiledTextStyle
{
    public string? Family { get; init; }
    public string? Fallback { get; init; }
    public int? Weight { get; init; }
    public string? Style { get; init; }
    public double? Size { get; init; }
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
            ["button"] = new(DefaultFont: ThemeFontRole.Heading),
            ["input"] = new(DefaultFont: ThemeFontRole.Body),
            ["tab"] = new(DefaultFont: ThemeFontRole.Heading),
            ["carousel"] = new(),
            ["pip"] = new(),
            ["switch.track"] = new(),
            ["switch.thumb"] = new(),
            ["carousel.nav"] = new(),
            ["titlebar.caption"] = new(),
            ["titlebar.close"] = new(),
            ["news-item"] = new(DefaultFont: ThemeFontRole.Heading),
            ["setting-row"] = new(DefaultFont: ThemeFontRole.Heading),
            ["titlebar"] = new(),
            ["subtabs"] = new(),
            ["scrollbar.track"] = new(),
            ["scrollbar.thumb"] = new(),
            ["progress.track"] = new(),
            ["progress.indicator"] = new(),
            ["play-ornament"] = new(),
            ["input.label"] = new("Text", ThemeFontRole.Heading),
            ["news-item.date"] = new("Text", ThemeFontRole.Body),
            ["news-item.gem"] = new("Text", ThemeFontRole.Heading),
            ["titlebar.wordmark"] = new("Text", ThemeFontRole.Heading),
            ["tab-bar"] = new("Text", ThemeFontRole.Heading),
            ["settings.heading"] = new("Text", ThemeFontRole.Heading),
            ["setting-help.title"] = new("Text", ThemeFontRole.Heading),
            ["setting-help.body"] = new("Text", ThemeFontRole.Body),
            ["input.placeholder"] = new("Text", ThemeFontRole.Body),
            ["input.caret"] = new("Text", ThemeFontRole.Heading),
            ["input.error"] = new("Text", ThemeFontRole.Body),
            ["play-row"] = new("Text", ThemeFontRole.Heading)
        };

    public static readonly IReadOnlyDictionary<string, CompiledTypographyRole> TypographyRoles =
        new Dictionary<string, CompiledTypographyRole>(StringComparer.Ordinal)
        {
            ["brand"] = new("titlebar.wordmark", 20, ThemeFontRole.Heading, 600),
            ["navigation"] = new("tab", 16, ThemeFontRole.Heading, 600),
            ["sectionHeading"] = new("settings.heading", 16, ThemeFontRole.Heading, 600),
            ["fieldLabel"] = new("input.label", 12, ThemeFontRole.Heading, 600),
            ["button"] = new("button", 16, ThemeFontRole.Heading, 600),
            ["cardTitle"] = new("news-item", 16, ThemeFontRole.Heading, 600),
            ["body"] = new("setting-help.body", 14, ThemeFontRole.Body, 400),
            ["input"] = new("input", 16, ThemeFontRole.Body, 400),
            ["settingValue"] = new("setting-row", 14, ThemeFontRole.Heading, 600),
            ["metadata"] = new("news-item.date", 13, ThemeFontRole.Body, 400)
        };

    public static readonly string[] ControlIds = [.. Controls.Keys];
    public static readonly HashSet<string> States = Enum.GetValues<ThemePartState>()
        .Where(value => value != ThemePartState.Normal)
        .Select(value => value.ToString().ToLowerInvariant())
        .ToHashSet(StringComparer.Ordinal);

    public static string? StateName(ThemePartState state) =>
        state == ThemePartState.Normal ? null : state.ToString().ToLowerInvariant();
}

public enum ThemeFontRole { Heading, Body }
public sealed record CompiledControlSpec(string? RequiredShape = null, ThemeFontRole DefaultFont = ThemeFontRole.Heading);
public sealed record CompiledTypographyRole(string ControlId, double FloorSize, ThemeFontRole FontRole, int FloorWeight);
