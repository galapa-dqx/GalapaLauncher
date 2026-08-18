using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Media;
using Galapa.Launcher.Views.Controls;

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

        var fileName = Path.GetFileName(path);
        if (!fileName.EndsWith(".compiled.json", StringComparison.OrdinalIgnoreCase))
            throw new ThemePackageException($"Compiled theme must use the '.compiled.json' suffix: {fileName}");
        var id = fileName[..^".compiled.json".Length];
        await using var stream = File.OpenRead(path);
        return await ReadAsync(stream, id, path, cancellationToken);
    }

    public async Task<ThemePackage> ReadAsync(Stream stream, string id, string source,
        CancellationToken cancellationToken = default)
    {
        if (!stream.CanRead)
            throw new ThemePackageException($"Compiled theme source is not readable: {source}");
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
        if (!IsSafeId(id))
            throw new ThemePackageException($"Compiled theme does not contain a safe ID: {id}");
        foreach (var control in theme.Controls.Values)
            AssignThemeId(control, id);

        var manifest = new ThemeManifest
        {
            Id = id,
            DisplayName = theme.Label,
            Author = theme.Meta?.Maintainer ?? "Galapa Project",
            PackageVersion = theme.Meta?.Version ?? "compiled",
            BaseVariant = theme.Mode == "dark" ? ThemeBaseVariant.Dark : ThemeBaseVariant.Light,
        };
        return new ThemePackage(manifest, source, theme);
    }

    public static void Validate(CompiledTheme theme)
    {
        if (string.IsNullOrWhiteSpace(theme.Label) || theme.Label.Length > 100)
            throw new ThemePackageException("Compiled theme label is required.");
        if (theme.Mode is not ("light" or "dark"))
            throw new ThemePackageException("Compiled theme mode must be 'light' or 'dark'.");
        if (theme.FocusRing is not null)
        {
            if (string.IsNullOrWhiteSpace(theme.FocusRing.Color))
                throw new ThemePackageException("Focus ring color is required when a focus ring is declared.");
            ValidateColor(theme.FocusRing.Color, "focus ring");
            if (!Finite(theme.FocusRing.Width, 0, 16) || !Finite(theme.FocusRing.Offset, -32, 32))
                throw new ThemePackageException("Focus ring metrics are out of range.");
        }

        if (theme.Controls is null)
            throw new ThemePackageException("Compiled theme controls are required.");

        foreach (var id in CompiledThemeContract.Controls.Keys)
            if (!theme.Controls.ContainsKey(id))
                throw new ThemePackageException($"Compiled theme is missing control '{id}'.");
        foreach (var id in theme.Controls.Keys)
            if (!CompiledThemeContract.Controls.ContainsKey(id))
                throw new ThemePackageException($"Compiled theme contains unknown control '{id}'.");

        foreach (var (id, control) in theme.Controls)
            ValidateControl(id, control);
    }

    private static void ValidateControl(string id, CompiledControl control)
    {
        var expected = CompiledThemeContract.Controls[id].RequiredShape;
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

        ValidateFieldsForShape(id, control);

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
            ValidateStateFields(id, control.Shape, state, value);
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

    private static void ValidateStateFields(string id, string? parentShape, string state, CompiledControl value)
    {
        if (value.Shape is not null || ThemeMetrics.HasValue(value.Radius) || value.Corner is not null || ThemeMetrics.HasValue(value.Padding) ||
            value.Text is not null || value.Size is not null || value.LeftInset is not null ||
            value.Images is not null || value.States is not null || value.Art is not null && parentShape != "Asset")
            throw new ThemePackageException($"Control '{id}' state '{state}' declares fields that states cannot override.");
    }

    private static void ValidateText(CompiledTextStyle? text, string id)
    {
        if (text is null) return;
        if (text.Family is { Length: > 200 } || text.Fallback is not null and not ("serif" or "sans-serif") ||
            text.Weight is { } weight && weight is < 1 or > 1000 ||
            text.Style is not null and not ("normal" or "italic" or "oblique") ||
            text.Size is { } size && !Finite(size, 1, 256) ||
            text.LetterSpacing is { } spacing && !Finite(spacing, -32, 128) ||
            text.Case is not null and not ("uppercase" or "lowercase" or "capitalize" or "none"))
            throw new ThemePackageException($"Control '{id}' contains invalid typography.");
    }

    private static void ValidateSvg(string svg, string label, bool nineSlice)
    {
        if (svg.Length > 2_000_000) throw new ThemePackageException($"SVG '{label}' is too large.");
        XElement root;
        try { root = ThemeSvgCache.Root(svg); }
        catch (Exception ex) { throw new ThemePackageException($"SVG '{label}' is malformed: {ex.Message}"); }
        if (root.Name.LocalName != "svg") throw new ThemePackageException($"'{label}' is not an SVG document.");
        foreach (var element in root.DescendantsAndSelf())
        {
            if (element.Name.LocalName is "script" or "foreignObject")
                throw new ThemePackageException($"SVG '{label}' contains forbidden content.");
            foreach (var attribute in element.Attributes())
            {
                if (attribute.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                    throw new ThemePackageException($"SVG '{label}' contains an event handler.");
                if ((attribute.Name.LocalName is "href" or "src") && !attribute.Value.StartsWith('#'))
                    throw new ThemePackageException($"SVG '{label}' contains an external reference.");
                if (attribute.Value.Contains("url(", StringComparison.OrdinalIgnoreCase))
                    throw new ThemePackageException($"SVG '{label}' contains a URL paint or reference.");
            }
        }
        try { ThemeSvgValidator.Validate(root, nineSlice); }
        catch (Exception ex) when (ex is not ThemePackageException)
        { throw new ThemePackageException($"SVG '{label}' cannot be rendered: {ex.Message}"); }
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
        if (!ThemeMetrics.HasValue(value)) return;
        double[] edges;
        try { edges = ThemeMetrics.ReadEdges(value); }
        catch (Exception ex) { throw new ThemePackageException($"'{label}' is invalid: {ex.Message}"); }
        if (edges.Any(x => !Finite(x, min, max))) throw new ThemePackageException($"'{label}' is out of range.");
    }

    private static void ValidateRadius(JsonElement radius, string id)
    {
        if (!ThemeMetrics.HasValue(radius)) return;
        if (radius.ValueKind == JsonValueKind.String && radius.GetString() == "pill") return;
        if (radius.ValueKind != JsonValueKind.Number || !Finite(radius.GetDouble(), 0, 4096))
            throw new ThemePackageException($"Control '{id}' has invalid radius.");
    }

    private static bool Finite(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;
    private static bool IsSafeId(string id) => id is { Length: > 0 and <= 100 } &&
        id.All(c => char.IsLower(c) || char.IsDigit(c) || c == '-');

    private static void AssignThemeId(CompiledControl control, string themeId)
    {
        control.ThemeId = themeId;
        if (control.States is null)
            return;
        foreach (var state in control.States.Values)
            AssignThemeId(state, themeId);
    }

    private static void ValidateFieldsForShape(string id, CompiledControl control)
    {
        var invalid = control.Shape switch
        {
            "Window" => ThemeMetrics.HasValue(control.BorderThickness) || ThemeMetrics.HasValue(control.Radius) || control.Corner is not null ||
                        ThemeMetrics.HasValue(control.Padding) || control.Opacity is not null || control.Text is not null ||
                        control.Size is not null || control.Image is not null || control.Art is not null ||
                        control.LeftInset is not null || control.Images is not null || control.States is not null,
            "Text" => control.Fill is not null || ThemeMetrics.HasValue(control.BorderThickness) || ThemeMetrics.HasValue(control.Radius) ||
                      control.Corner is not null || ThemeMetrics.HasValue(control.Padding) || control.Opacity is not null ||
                      control.Size is not null || control.Image is not null || control.Art is not null ||
                      control.States is not null,
            "Asset" => control.Fill is not null || control.BorderColor is not null || ThemeMetrics.HasValue(control.BorderThickness) ||
                       ThemeMetrics.HasValue(control.Radius) || control.Corner is not null || ThemeMetrics.HasValue(control.Padding) ||
                       control.Image is not null || control.LeftInset is not null || control.Images is not null,
            "Path" => control.Art is not null || control.LeftInset is not null || control.Images is not null,
            _ => true
        };
        if (invalid)
            throw new ThemePackageException($"Control '{id}' declares fields that are not valid for shape '{control.Shape}'.");
    }
}

public static class ThemeMetrics
{
    public static bool HasValue(JsonElement value) =>
        value.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);

    public static double[] ReadEdges(JsonElement value, double fallback = 0)
    {
        if (!HasValue(value)) return [fallback, fallback, fallback, fallback];
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

    public static Thickness ToThickness(JsonElement value, double fallback = 0)
    {
        var edges = ReadEdges(value, fallback);
        return new Thickness(edges[3], edges[0], edges[1], edges[2]);
    }
}

public static class ThemeTypography
{
    public static FontStyle ToFontStyle(string? value) => value switch
    {
        "italic" => FontStyle.Italic,
        "oblique" => FontStyle.Oblique,
        _ => FontStyle.Normal
    };

    public static string ToTransform(string? value) => value switch
    {
        "uppercase" => "Upper",
        "lowercase" => "Lower",
        "capitalize" => "Title",
        _ => "Original"
    };
}

public static class ThemePaint
{
    public static Color ParseColor(string value)
    {
        if (value.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return Colors.Transparent;
        if (value.StartsWith('#')) return ParseCssHex(value);
        var match = System.Text.RegularExpressions.Regex.Match(value,
            @"^rgba?\(\s*(\d+(?:\.\d+)?)\s*[, ]\s*(\d+(?:\.\d+)?)\s*[, ]\s*(\d+(?:\.\d+)?)(?:\s*[,/]\s*(\d*\.?\d+))?\s*\)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success) throw new FormatException($"Invalid color: {value}");
        var red = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var green = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var blue = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        var alpha = match.Groups[4].Success ? double.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) : 1;
        if (!double.IsFinite(red) || red is < 0 or > 255 ||
            !double.IsFinite(green) || green is < 0 or > 255 ||
            !double.IsFinite(blue) || blue is < 0 or > 255 ||
            !double.IsFinite(alpha) || alpha is < 0 or > 1)
            throw new FormatException($"Color component is out of range: {value}");
        return Color.FromArgb(
            byte.CreateChecked(Math.Round(alpha * 255, MidpointRounding.AwayFromZero)),
            byte.CreateChecked(Math.Round(red, MidpointRounding.AwayFromZero)),
            byte.CreateChecked(Math.Round(green, MidpointRounding.AwayFromZero)),
            byte.CreateChecked(Math.Round(blue, MidpointRounding.AwayFromZero)));
    }

    private static Color ParseCssHex(string value)
    {
        static byte Nibble(char value) => value switch
        {
            >= '0' and <= '9' => (byte)(value - '0'),
            >= 'a' and <= 'f' => (byte)(value - 'a' + 10),
            >= 'A' and <= 'F' => (byte)(value - 'A' + 10),
            _ => throw new FormatException("Invalid hexadecimal color component.")
        };

        static byte Pair(ReadOnlySpan<char> value) => (byte)((Nibble(value[0]) << 4) | Nibble(value[1]));

        var hex = value.AsSpan(1);
        return hex.Length switch
        {
            3 => Color.FromArgb(255, (byte)(Nibble(hex[0]) * 17), (byte)(Nibble(hex[1]) * 17), (byte)(Nibble(hex[2]) * 17)),
            4 => Color.FromArgb((byte)(Nibble(hex[3]) * 17), (byte)(Nibble(hex[0]) * 17), (byte)(Nibble(hex[1]) * 17), (byte)(Nibble(hex[2]) * 17)),
            6 => Color.FromArgb(255, Pair(hex[..2]), Pair(hex.Slice(2, 2)), Pair(hex.Slice(4, 2))),
            8 => Color.FromArgb(Pair(hex.Slice(6, 2)), Pair(hex[..2]), Pair(hex.Slice(2, 2)), Pair(hex.Slice(4, 2))),
            _ => throw new FormatException($"Invalid CSS hexadecimal color: {value}")
        };
    }

    public static IBrush? Brush(string? value, IBrush? inherited = null) => value switch
    {
        null => null,
        "inherit" => inherited,
        _ => new SolidColorBrush(ParseColor(value))
    };
}
