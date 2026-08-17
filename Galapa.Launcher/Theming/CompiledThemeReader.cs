using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Avalonia.Media;

namespace Galapa.Launcher.Theming;

public sealed class CompiledThemeReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<ThemePackage> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            throw new ThemePackageException($"Compiled theme not found: {path}");

        await using var stream = File.OpenRead(path);
        CompiledTheme theme;
        try
        {
            theme = await JsonSerializer.DeserializeAsync<CompiledTheme>(stream, JsonOptions, cancellationToken)
                    ?? throw new ThemePackageException("Compiled theme is empty.");
        }
        catch (JsonException ex)
        {
            throw new ThemePackageException($"Compiled theme JSON is malformed: {ex.Message}");
        }

        Validate(theme);
        var id = Path.GetFileName(path).Replace(".compiled.json", string.Empty, StringComparison.OrdinalIgnoreCase);
        if (!IsSafeId(id))
            throw new ThemePackageException($"Compiled theme filename does not contain a safe ID: {Path.GetFileName(path)}");

        var manifest = new ThemeManifest
        {
            Id = id,
            DisplayName = theme.Label,
            Author = theme.Meta?.Maintainer ?? "Galapa Project",
            PackageVersion = theme.Meta?.Version ?? "compiled",
            BaseVariant = theme.Mode == "dark" ? ThemeBaseVariant.Dark : ThemeBaseVariant.Light,
            Colors = CompatibilityColors(theme),
            Fonts = CompatibilityFonts(theme),
            Typography = [],
            Geometry = [],
            Assets = []
        };
        return new ThemePackage(manifest, path, string.Empty, new Dictionary<string, string>(), theme);
    }

    public static void Validate(CompiledTheme theme)
    {
        if (string.IsNullOrWhiteSpace(theme.Label))
            throw new ThemePackageException("Compiled theme label is required.");
        if (theme.Mode is not ("light" or "dark"))
            throw new ThemePackageException("Compiled theme mode must be 'light' or 'dark'.");
        if (theme.FocusRing is not null)
        {
            ValidateColor(theme.FocusRing.Color, "focus ring");
            if (!Finite(theme.FocusRing.Width, 0, 16) || !Finite(theme.FocusRing.Offset, -32, 32))
                throw new ThemePackageException("Focus ring metrics are out of range.");
        }

        foreach (var id in CompiledThemeContract.ControlIds)
            if (!theme.Controls.ContainsKey(id))
                throw new ThemePackageException($"Compiled theme is missing control '{id}'.");
        foreach (var id in theme.Controls.Keys)
            if (!CompiledThemeContract.ControlIds.Contains(id, StringComparer.Ordinal))
                throw new ThemePackageException($"Compiled theme contains unknown control '{id}'.");

        foreach (var (id, control) in theme.Controls)
            ValidateControl(id, control);
    }

    private static void ValidateControl(string id, CompiledControl control)
    {
        var expected = id == "window" ? "Window" : id is "input.label" or "news-item.date" or "news-item.gem" or
            "titlebar.wordmark" or "tab-bar" or "settings.heading" or "setting-help.title" or
            "setting-help.body" or "input.placeholder" or "input.caret" or "play-row" ? "Text" : null;
        if (control.Shape is not ("Window" or "Path" or "Asset" or "Text") || expected is not null && control.Shape != expected)
            throw new ThemePackageException($"Control '{id}' has invalid shape '{control.Shape}'.");
        if (expected is null && control.Shape is "Window" or "Text")
            throw new ThemePackageException($"Control '{id}' cannot use shape '{control.Shape}'.");

        ValidateOptionalColor(control.Fill, $"{id}.fill", allowInherit: false);
        ValidateOptionalColor(control.Content, $"{id}.content", allowInherit: true);
        ValidateOptionalColor(control.BorderColor, $"{id}.borderColor", allowInherit: false);
        ValidateEdges(control.BorderThickness, $"{id}.borderThickness", 0, 32);
        ValidateEdges(control.Padding, $"{id}.padding", 0, 256);
        ValidateRadius(control.Radius, id);
        if (control.Corner is not null && control.Corner is not ("round" or "bevel" or "scoop" or "notch" or "squircle"))
            throw new ThemePackageException($"Control '{id}' has invalid corner shape '{control.Corner}'.");
        if (control.Opacity is { } opacity && !Finite(opacity, 0, 1))
            throw new ThemePackageException($"Control '{id}' has invalid opacity.");
        if (control.Size?.Width is { } width && !Finite(width, 0, 4096) || control.Size?.Height is { } height && !Finite(height, 0, 4096))
            throw new ThemePackageException($"Control '{id}' has invalid size.");
        ValidateText(control.Text, id);

        if (control.Shape == "Asset")
        {
            if (string.IsNullOrWhiteSpace(control.Art))
                throw new ThemePackageException($"Asset control '{id}' has no art.");
            ValidateSvg(control.Art, $"{id}.art", nineSlice: true);
        }
        else if (control.Art is not null)
            throw new ThemePackageException($"Only Asset controls may declare art ('{id}').");

        if (control.Image is not null) ValidateSvg(control.Image, $"{id}.image", nineSlice: false);
        if (control.Images is not null)
            foreach (var (variant, svg) in control.Images)
                ValidateSvg(svg, $"{id}.images.{variant}", nineSlice: false);

        if (control.States is null) return;
        foreach (var (state, value) in control.States)
        {
            if (!CompiledThemeContract.States.Contains(state))
                throw new ThemePackageException($"Control '{id}' has unknown state '{state}'.");
            ValidateOptionalColor(value.Fill, $"{id}.{state}.fill", false);
            ValidateOptionalColor(value.Content, $"{id}.{state}.content", true);
            ValidateOptionalColor(value.BorderColor, $"{id}.{state}.borderColor", false);
            ValidateEdges(value.BorderThickness, $"{id}.{state}.borderThickness", 0, 32);
            if (value.Opacity is { } stateOpacity && !Finite(stateOpacity, 0, 1))
                throw new ThemePackageException($"Control '{id}' state '{state}' has invalid opacity.");
            if (value.Image is not null) ValidateSvg(value.Image, $"{id}.{state}.image", false);
            if (value.Art is not null) ValidateSvg(value.Art, $"{id}.{state}.art", true);
        }
    }

    private static void ValidateText(CompiledTextStyle? text, string id)
    {
        if (text is null) return;
        if (text.Family is { Length: > 200 } || text.Fallback is not null and not ("serif" or "sans-serif") ||
            text.Weight is { } weight && weight is < 1 or > 1000 ||
            text.Style is not null and not ("normal" or "italic" or "oblique") ||
            text.Size is { } size && !Finite(size, 1, 256) ||
            text.Case is not null and not ("uppercase" or "lowercase" or "capitalize" or "none"))
            throw new ThemePackageException($"Control '{id}' contains invalid typography.");
    }

    private static void ValidateSvg(string svg, string label, bool nineSlice)
    {
        if (svg.Length > 2_000_000) throw new ThemePackageException($"SVG '{label}' is too large.");
        XDocument document;
        try { document = XDocument.Parse(svg, LoadOptions.None); }
        catch (Exception ex) { throw new ThemePackageException($"SVG '{label}' is malformed: {ex.Message}"); }
        var root = document.Root;
        if (root?.Name.LocalName != "svg") throw new ThemePackageException($"'{label}' is not an SVG document.");
        foreach (var element in root.DescendantsAndSelf())
        {
            if (element.Name.LocalName is "script" or "foreignObject")
                throw new ThemePackageException($"SVG '{label}' contains forbidden content.");
            foreach (var attribute in element.Attributes())
                if ((attribute.Name.LocalName is "href" or "src") && !attribute.Value.StartsWith('#'))
                    throw new ThemePackageException($"SVG '{label}' contains an external reference.");
        }
        if (nineSlice && !root.Descendants().Any(x => x.Name.LocalName == "svg" &&
                System.Text.RegularExpressions.Regex.IsMatch(x.Attribute("id")?.Value ?? string.Empty, "^\\d+_\\d+$")))
            throw new ThemePackageException($"Nine-slice SVG '{label}' has no slice viewports.");
    }

    private static void ValidateOptionalColor(string? value, string label, bool allowInherit)
    {
        if (value is null) return;
        if (allowInherit && value == "inherit") return;
        ValidateColor(value, label);
    }

    private static void ValidateColor(string value, string label)
    {
        try { _ = ThemePaint.ParseColor(value); }
        catch { throw new ThemePackageException($"'{label}' contains invalid color '{value}'."); }
    }

    private static void ValidateEdges(JsonElement value, string label, double min, double max)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return;
        var edges = ThemeMetrics.ReadEdges(value);
        if (edges.Any(x => !Finite(x, min, max))) throw new ThemePackageException($"'{label}' is out of range.");
    }

    private static void ValidateRadius(JsonElement radius, string id)
    {
        if (radius.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return;
        if (radius.ValueKind == JsonValueKind.String && radius.GetString() == "pill") return;
        if (radius.ValueKind != JsonValueKind.Number || !Finite(radius.GetDouble(), 0, 4096))
            throw new ThemePackageException($"Control '{id}' has invalid radius.");
    }

    private static bool Finite(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;
    private static bool IsSafeId(string id) => !string.IsNullOrWhiteSpace(id) && id.All(c => char.IsLower(c) || char.IsDigit(c) || c is '-' or '.');

    private static ThemeColors CompatibilityColors(CompiledTheme theme)
    {
        var window = theme.Controls["window"];
        var panel = theme.Controls["panel"];
        var titlebar = theme.Controls["titlebar"];
        var tab = theme.Controls["tab"];
        var selected = tab.States?.GetValueOrDefault("selected");
        var close = theme.Controls["titlebar.close"].States?.GetValueOrDefault("hover");
        var accent = selected?.Content ?? selected?.BorderColor ?? theme.FocusRing?.Color ?? window.Content ?? "#0078d4";
        var panelSurface = panel.Fill ?? AssetCenterFill(panel.Art);
        return new ThemeColors
        {
            Background = window.Fill ?? "transparent",
            Surface = panelSurface ?? titlebar.Fill ?? window.Fill ?? "transparent",
            SecondarySurface = titlebar.Fill ?? panel.Fill ?? window.Fill ?? "transparent",
            Border = panel.BorderColor ?? window.BorderColor ?? "transparent",
            Text = window.Content is null or "inherit" ? "#000000" : window.Content,
            Muted = tab.Content is null or "inherit" ? window.Content ?? "#666666" : tab.Content,
            Accent = accent is "inherit" ? window.Content ?? "#0078d4" : accent,
            Success = accent is "inherit" ? "#178343" : accent,
            Danger = close?.Fill ?? "#c42b1c"
        };
    }

    private static string? AssetCenterFill(string? art)
    {
        if (string.IsNullOrWhiteSpace(art)) return null;
        try
        {
            var root = XDocument.Parse(art).Root;
            var center = root?.Descendants().FirstOrDefault(x => x.Name.LocalName == "svg" &&
                x.Attribute("id")?.Value == "1_1");
            return center?.Descendants().Select(x => x.Attribute("fill")?.Value)
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x) && x != "none");
        }
        catch
        {
            return null;
        }
    }

    private static ThemeFonts CompatibilityFonts(CompiledTheme theme)
    {
        var heading = theme.Controls["titlebar.wordmark"].Text?.Family ?? "Inter";
        var body = theme.Controls["input"].Text?.Family ?? "Inter";
        return new ThemeFonts
        {
            Heading = new ThemeFont { File = string.Empty, Family = heading },
            Body = new ThemeFont { File = string.Empty, Family = body },
            HeadingBaseSize = 16,
            BodyBaseSize = 14
        };
    }
}

public static class ThemeMetrics
{
    public static double[] ReadEdges(JsonElement value, double fallback = 0)
    {
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return [fallback, fallback, fallback, fallback];
        if (value.ValueKind == JsonValueKind.Number)
        {
            var number = value.GetDouble();
            return [number, number, number, number];
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            var values = value.EnumerateArray().Select(x => x.GetDouble()).ToArray();
            if (values.Length == 4) return values;
        }
        throw new ThemePackageException("Expected a number or four-element edge array.");
    }

    public static double ReadRadius(JsonElement value, double width, double height)
    {
        if (value.ValueKind == JsonValueKind.String && value.GetString() == "pill") return Math.Min(width, height) / 2;
        return value.ValueKind == JsonValueKind.Number ? value.GetDouble() : 0;
    }
}

public static class ThemePaint
{
    public static Color ParseColor(string value)
    {
        if (value.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return Colors.Transparent;
        if (Color.TryParse(value, out var color)) return color;
        var match = System.Text.RegularExpressions.Regex.Match(value,
            @"^rgba?\(\s*(\d+(?:\.\d+)?)\s*[, ]\s*(\d+(?:\.\d+)?)\s*[, ]\s*(\d+(?:\.\d+)?)(?:\s*[,/]\s*(\d*\.?\d+))?\s*\)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success) throw new FormatException($"Invalid color: {value}");
        var r = byte.CreateSaturating(double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture));
        var g = byte.CreateSaturating(double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
        var b = byte.CreateSaturating(double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture));
        var alpha = match.Groups[4].Success ? double.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) : 1;
        return Color.FromArgb(byte.CreateSaturating(alpha * 255), r, g, b);
    }

    public static IBrush? Brush(string? value, IBrush? inherited = null) => value switch
    {
        null => null,
        "inherit" => inherited,
        _ => new SolidColorBrush(ParseColor(value))
    };
}
