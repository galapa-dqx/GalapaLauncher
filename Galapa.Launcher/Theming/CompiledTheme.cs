using System.Text.Json;

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
    public static readonly string[] ControlIds =
    [
        "window", "panel", "button", "input", "tab", "carousel", "pip",
        "switch.track", "switch.thumb", "carousel.nav", "titlebar.caption",
        "titlebar.close", "news-item", "setting-row", "titlebar", "subtabs",
        "scrollbar.track", "scrollbar.thumb", "play-ornament", "input.label",
        "news-item.date", "news-item.gem", "titlebar.wordmark", "tab-bar",
        "settings.heading", "setting-help.title", "setting-help.body",
        "input.placeholder", "input.caret", "play-row"
    ];

    public static readonly HashSet<string> States =
    ["hover", "pressed", "focused", "disabled", "selected", "checked"];
}
