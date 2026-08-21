using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Galapa.Launcher.Theming;

public readonly record struct ThemeRect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
}

public readonly record struct ThemeThickness(double Left, double Top, double Right, double Bottom);

public readonly record struct ThemeMatrix(double M11, double M12, double M21, double M22, double M31, double M32)
{
    public static ThemeMatrix Identity => new(1, 0, 0, 1, 0, 0);
    public static ThemeMatrix Translation(double x, double y) => new(1, 0, 0, 1, x, y);
    public static ThemeMatrix Scale(double x, double y) => new(x, 0, 0, y, 0, 0);

    public static ThemeMatrix operator *(ThemeMatrix left, ThemeMatrix right) => new(
        left.M11 * right.M11 + left.M12 * right.M21,
        left.M11 * right.M12 + left.M12 * right.M22,
        left.M21 * right.M11 + left.M22 * right.M21,
        left.M21 * right.M12 + left.M22 * right.M22,
        left.M31 * right.M11 + left.M32 * right.M21 + right.M31,
        left.M31 * right.M12 + left.M32 * right.M22 + right.M32);
}

public readonly record struct ThemeColor(byte A, byte R, byte G, byte B)
{
    private static readonly Regex RgbPattern = new(
        @"^rgba?\(\s*(\d+(?:\.\d+)?)\s*[, ]\s*(\d+(?:\.\d+)?)\s*[, ]\s*(\d+(?:\.\d+)?)(?:\s*[,/]\s*(\d*\.?\d+))?\s*\)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static ThemeColor Parse(string value)
    {
        if (value.Equals("transparent", StringComparison.OrdinalIgnoreCase)) return new ThemeColor(0, 0, 0, 0);
        if (value.Equals("black", StringComparison.OrdinalIgnoreCase)) return new ThemeColor(255, 0, 0, 0);
        if (value.Equals("white", StringComparison.OrdinalIgnoreCase)) return new ThemeColor(255, 255, 255, 255);
        if (value.StartsWith('#')) return ParseHex(value);
        var match = RgbPattern.Match(value);
        if (!match.Success) throw new FormatException($"Invalid color: {value}");
        var red = double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var green = double.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var blue = double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        var alpha = match.Groups[4].Success
            ? double.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture)
            : 1;
        if (!double.IsFinite(red) || red is < 0 or > 255 ||
            !double.IsFinite(green) || green is < 0 or > 255 ||
            !double.IsFinite(blue) || blue is < 0 or > 255 ||
            !double.IsFinite(alpha) || alpha is < 0 or > 1)
            throw new FormatException($"Color component is out of range: {value}");
        return new ThemeColor(
            byte.CreateChecked(Math.Round(alpha * 255, MidpointRounding.AwayFromZero)),
            byte.CreateChecked(Math.Round(red, MidpointRounding.AwayFromZero)),
            byte.CreateChecked(Math.Round(green, MidpointRounding.AwayFromZero)),
            byte.CreateChecked(Math.Round(blue, MidpointRounding.AwayFromZero)));
    }

    private static ThemeColor ParseHex(string value)
    {
        static byte Nibble(char character) => character switch
        {
            >= '0' and <= '9' => (byte)(character - '0'),
            >= 'a' and <= 'f' => (byte)(character - 'a' + 10),
            >= 'A' and <= 'F' => (byte)(character - 'A' + 10),
            _ => throw new FormatException("Invalid hexadecimal color component.")
        };
        static byte Pair(ReadOnlySpan<char> pair) => (byte)((Nibble(pair[0]) << 4) | Nibble(pair[1]));
        var hex = value.AsSpan(1);
        return hex.Length switch
        {
            3 => new ThemeColor(255, (byte)(Nibble(hex[0]) * 17), (byte)(Nibble(hex[1]) * 17), (byte)(Nibble(hex[2]) * 17)),
            4 => new ThemeColor((byte)(Nibble(hex[3]) * 17), (byte)(Nibble(hex[0]) * 17), (byte)(Nibble(hex[1]) * 17), (byte)(Nibble(hex[2]) * 17)),
            6 => new ThemeColor(255, Pair(hex[..2]), Pair(hex.Slice(2, 2)), Pair(hex.Slice(4, 2))),
            8 => new ThemeColor(Pair(hex.Slice(6, 2)), Pair(hex[..2]), Pair(hex.Slice(2, 2)), Pair(hex.Slice(4, 2))),
            _ => throw new FormatException($"Invalid CSS hexadecimal color: {value}")
        };
    }
}

public enum ThemePaintKind { None, Color, CurrentColor }

public readonly record struct ThemePaintIr(ThemePaintKind Kind, ThemeColor Color)
{
    public static ThemePaintIr Parse(string? value) => value switch
    {
        null or "none" => default,
        "currentColor" => new ThemePaintIr(ThemePaintKind.CurrentColor, default),
        _ => new ThemePaintIr(ThemePaintKind.Color, ThemeColor.Parse(value))
    };
}

public enum SvgGeometryKind { Path, Circle, Line, Rectangle }
public enum SvgFillRuleIr { NonZero, EvenOdd }
public enum SvgLineCapIr { Butt, Round, Square }
public enum SvgLineJoinIr { Miter, Round, Bevel }

public sealed record SvgShapeIr(
    SvgGeometryKind Kind,
    string? PathData,
    ThemeRect Rect,
    double RadiusX,
    double RadiusY,
    double X2,
    double Y2,
    ThemePaintIr Fill,
    ThemePaintIr Stroke,
    double StrokeWidth,
    double FillOpacity,
    double StrokeOpacity,
    SvgFillRuleIr FillRule,
    SvgLineCapIr StrokeLineCap,
    SvgLineJoinIr StrokeLineJoin,
    double StrokeMiterLimit,
    IReadOnlyList<double> StrokeDashArray,
    double StrokeDashOffset,
    ThemeMatrix Transform);

public sealed record SvgDocumentIr(ThemeRect ViewBox, IReadOnlyList<SvgShapeIr> Shapes);

public sealed record AspectRatioIr(bool None, bool Slice, double X, double Y);

public sealed record NineSliceCellIr(
    int Column,
    int Row,
    double Width,
    double Height,
    ThemeRect ViewBox,
    AspectRatioIr AspectRatio,
    string Repeat,
    IReadOnlyList<SvgShapeIr> Shapes);

public sealed record NineSliceIr(
    IReadOnlyList<double?> Columns,
    IReadOnlyList<double?> Rows,
    ThemeThickness ContentPadding,
    ThemeThickness Outset,
    IReadOnlyList<NineSliceCellIr> Cells);

public sealed record ValidatedThemeAssets(
    IReadOnlyDictionary<string, SvgDocumentIr> Documents,
    IReadOnlyDictionary<string, NineSliceIr> NineSlices,
    int XmlParseCount);

/// <summary>Parses the safe SVG subset into immutable, Avalonia-free data.</summary>
public sealed class ThemeSvgIrParser
{
    private static readonly Regex TransformPattern = new(
        @"(matrix|translate|scale)\s*\(([^)]*)\)", RegexOptions.Compiled);
    private static readonly Regex SliceIdPattern = new(@"^\d+_\d+$", RegexOptions.Compiled);
    private static readonly HashSet<string> SupportedElements = ["svg", "g", "path", "circle", "line", "rect"];
    private static readonly string[] NumericAttributes =
        ["x", "y", "x1", "y1", "x2", "y2", "cx", "cy", "r", "rx", "ry", "width", "height", "stroke-width",
            "opacity", "fill-opacity", "stroke-opacity", "stroke-miterlimit", "stroke-dashoffset"];
    private static readonly HashSet<string> SupportedAttributes =
    [
        "id", "viewBox", "width", "height", "x", "y", "x1", "y1", "x2", "y2", "cx", "cy", "r", "rx", "ry",
        "d", "transform", "fill", "stroke", "fill-rule", "opacity", "fill-opacity", "stroke-opacity", "stroke-width",
        "stroke-linecap", "stroke-linejoin", "stroke-miterlimit", "stroke-dasharray", "stroke-dashoffset",
        "preserveAspectRatio", "data-slice-repeat", "role", "aria-label", "aria-hidden", "version"
    ];

    public int ParseCount { get; private set; }
    internal static event Action? XmlParsed;

    public SvgDocumentIr ParseDocument(string source)
    {
        var root = ParseRoot(source);
        ValidateTree(root);
        return new SvgDocumentIr(ParseViewBox(root), ParseShapes(root));
    }

    public NineSliceIr ParseNineSlice(string source)
    {
        var root = ParseRoot(source);
        ValidateTree(root);
        var rootViewBox = ParseViewBox(root);
        var rootWidth = Number(root.Attribute("width")?.Value, rootViewBox.Width);
        var rootHeight = Number(root.Attribute("height")?.Value, rootViewBox.Height);
        var scale = rootWidth / rootViewBox.Width;
        var verticalScale = rootHeight / rootViewBox.Height;
        if (!double.IsFinite(scale) || scale <= 0 || !double.IsFinite(verticalScale) || verticalScale <= 0 ||
            Math.Abs(scale - verticalScale) > .001)
            throw new InvalidDataException("Nine-slice SVG has an invalid root size.");

        var elements = root.Descendants()
            .Where(element => element.Name.LocalName == "svg" && SliceIdPattern.IsMatch(element.Attribute("id")?.Value ?? ""))
            .ToArray();
        if (elements.Length == 0) throw new InvalidDataException("Nine-slice SVG has no slices.");
        var cells = elements.Select(element =>
        {
            var id = element.Attribute("id")!.Value;
            var position = id.Split('_').Select(int.Parse).ToArray();
            var width = Number(element.Attribute("width")?.Value, 0);
            var height = Number(element.Attribute("height")?.Value, 0);
            if (width <= 0 || height <= 0)
                throw new InvalidDataException($"Nine-slice cell '{id}' has an invalid size.");
            var viewBox = element.Attribute("viewBox") is not null
                ? ParseViewBox(element)
                : new ThemeRect(0, 0, width, height);
            var repeat = element.Attribute("data-slice-repeat")?.Value ?? "stretch";
            if (repeat is not ("stretch" or "repeat" or "round" or "space"))
                throw new InvalidDataException($"Nine-slice cell '{id}' has invalid repeat mode '{repeat}'.");
            return new NineSliceCellIr(position[0], position[1], width * scale, height * scale,
                viewBox, ParseAspectRatio(element.Attribute("preserveAspectRatio")?.Value ?? "none"), repeat,
                ParseShapes(element));
        }).ToArray();
        if (cells.Select(cell => (cell.Column, cell.Row)).Distinct().Count() != cells.Length)
            throw new InvalidDataException("Nine-slice SVG contains duplicate slice positions.");
        var columns = cells.Max(cell => cell.Column) + 1;
        var rows = cells.Max(cell => cell.Row) + 1;
        if (columns is not (1 or 3) || rows is not (1 or 3) || cells.Length != columns * rows)
            throw new InvalidDataException($"Unsupported nine-slice grid {columns}x{rows}.");
        for (var column = 0; column < columns; column++)
            for (var row = 0; row < rows; row++)
                if (!cells.Any(cell => cell.Column == column && cell.Row == row))
                    throw new InvalidDataException($"Nine-slice SVG is missing slice '{column}_{row}'.");

        var frame = root.Descendants().FirstOrDefault(element => element.Attribute("id")?.Value == "frame");
        var frameRect = frame is null ? rootViewBox : ElementRect(frame);
        var outset = new ThemeThickness(
            Math.Max(0, (frameRect.X - rootViewBox.X) * scale),
            Math.Max(0, (frameRect.Y - rootViewBox.Y) * scale),
            Math.Max(0, (rootViewBox.Right - frameRect.Right) * scale),
            Math.Max(0, (rootViewBox.Bottom - frameRect.Bottom) * scale));
        var content = root.Descendants().FirstOrDefault(element => element.Attribute("id")?.Value == "content");
        var padding = new ThemeThickness(0, 0, 0, 0);
        if (content is not null)
        {
            var rect = ElementRect(content);
            padding = new ThemeThickness(
                Math.Max(0, (rect.X - frameRect.X) * scale),
                Math.Max(0, (rect.Y - frameRect.Y) * scale),
                Math.Max(0, (frameRect.Right - rect.Right) * scale),
                Math.Max(0, (frameRect.Bottom - rect.Bottom) * scale));
        }
        double?[] Track(int count, bool horizontal) => Enumerable.Range(0, count).Select(index =>
        {
            if (count == 1 || index == 1) return (double?)null;
            var cell = cells.First(item => horizontal ? item.Column == index : item.Row == index);
            return horizontal ? cell.Width : cell.Height;
        }).ToArray();
        return new NineSliceIr(Track(columns, true), Track(rows, false), padding, outset, cells);
    }

    private XElement ParseRoot(string source)
    {
        if (source.Length > 2_000_000) throw new InvalidDataException("SVG is too large.");
        ParseCount++;
        XmlParsed?.Invoke();
        var root = XDocument.Parse(source, LoadOptions.None).Root
                   ?? throw new InvalidDataException("SVG has no root.");
        if (root.Name.LocalName != "svg") throw new InvalidDataException("Asset is not an SVG document.");
        return root;
    }

    private static void ValidateTree(XElement root)
    {
        foreach (var element in root.DescendantsAndSelf())
        {
            foreach (var attribute in element.Attributes())
            {
                if (attribute.Name.LocalName.StartsWith("on", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SVG contains an event handler.");
                if ((attribute.Name.LocalName is "href" or "src") && !attribute.Value.StartsWith('#'))
                    throw new InvalidDataException("SVG contains an external reference.");
                if (attribute.Value.Contains("url(", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SVG contains a URL paint or reference.");
                if (attribute.Name.LocalName == "style")
                    throw new InvalidDataException("SVG CSS style attributes are not supported.");
                if (!attribute.IsNamespaceDeclaration && !SupportedAttributes.Contains(attribute.Name.LocalName) &&
                    !attribute.Name.LocalName.StartsWith("data-", StringComparison.Ordinal) &&
                    !attribute.Name.LocalName.StartsWith("aria-", StringComparison.Ordinal))
                    throw new InvalidDataException($"Unsupported SVG attribute '{attribute.Name.LocalName}'.");
            }
            if (!SupportedElements.Contains(element.Name.LocalName))
                throw new InvalidDataException($"Unsupported SVG element '{element.Name.LocalName}'.");
            _ = ParseTransform(element.Attribute("transform")?.Value);
            ValidatePaint(element.Attribute("fill")?.Value);
            ValidatePaint(element.Attribute("stroke")?.Value);
            foreach (var name in NumericAttributes)
                if (element.Attribute(name)?.Value is { } value) _ = RequiredNumber(value);
            ValidateUnitInterval(element.Attribute("opacity")?.Value, "opacity");
            ValidateUnitInterval(element.Attribute("fill-opacity")?.Value, "fill-opacity");
            ValidateUnitInterval(element.Attribute("stroke-opacity")?.Value, "stroke-opacity");
            if (element.Name.LocalName is "g" or "svg" && element.Attribute("opacity") is { } groupOpacity &&
                Math.Abs(RequiredNumber(groupOpacity.Value) - 1) > .0001)
                throw new InvalidDataException("SVG group opacity requires unsupported compositing.");
            _ = ParseFillRule(element.Attribute("fill-rule")?.Value, SvgFillRuleIr.NonZero);
            _ = ParseLineCap(element.Attribute("stroke-linecap")?.Value, SvgLineCapIr.Butt);
            _ = ParseLineJoin(element.Attribute("stroke-linejoin")?.Value, SvgLineJoinIr.Miter);
            _ = ParseDashArray(element.Attribute("stroke-dasharray")?.Value, []);
            if (element.Attribute("stroke-miterlimit")?.Value is { } miterLimit && RequiredNumber(miterLimit) < 1)
                throw new InvalidDataException("SVG stroke miter limit must be at least one.");
            if (element.Name.LocalName == "path" && string.IsNullOrWhiteSpace(element.Attribute("d")?.Value))
                throw new InvalidDataException("SVG path has no path data.");
        }
    }

    private static IReadOnlyList<SvgShapeIr> ParseShapes(XElement container)
    {
        var output = new List<SvgShapeIr>();
        var transform = ParseTransform(container.Attribute("transform")?.Value);
        var style = ReadStyle(container, SvgInheritedStyle.Default);
        foreach (var child in container.Elements())
            ParseElement(child, transform, style, output);
        return output;
    }

    private static void ParseElement(XElement element, ThemeMatrix parentTransform, SvgInheritedStyle inherited,
        ICollection<SvgShapeIr> output)
    {
        var transform = ParseTransform(element.Attribute("transform")?.Value) * parentTransform;
        var style = ReadStyle(element, inherited);
        if (!double.IsFinite(style.StrokeWidth) || style.StrokeWidth < 0)
            throw new InvalidDataException("SVG stroke width must be finite and non-negative.");
        if (element.Name.LocalName is "g" or "svg")
        {
            foreach (var child in element.Elements())
                ParseElement(child, transform, style, output);
            return;
        }
        var opacity = UnitInterval(element.Attribute("opacity")?.Value, 1, "opacity");
        SvgShapeIr Shape(SvgGeometryKind kind, string? data, ThemeRect rect, double radiusX = 0,
            double radiusY = 0, double x2 = 0, double y2 = 0) => new(
            kind, data, rect, radiusX, radiusY, x2, y2, style.Fill, style.Stroke, style.StrokeWidth,
            style.FillOpacity * opacity, style.StrokeOpacity * opacity, style.FillRule, style.LineCap,
            style.LineJoin, style.MiterLimit, style.DashArray, style.DashOffset, transform);
        SvgShapeIr? shape = element.Name.LocalName switch
        {
            "path" when element.Attribute("d")?.Value is { Length: > 0 } data =>
                Shape(SvgGeometryKind.Path, data, default),
            "circle" when Number(element.Attribute("r")?.Value, 0) is > 0 and var radius =>
                Shape(SvgGeometryKind.Circle, null,
                    new ThemeRect(Number(element.Attribute("cx")?.Value, 0) - radius,
                        Number(element.Attribute("cy")?.Value, 0) - radius, radius * 2, radius * 2),
                    radius, radius),
            "line" => Shape(SvgGeometryKind.Line, null,
                new ThemeRect(Number(element.Attribute("x1")?.Value, 0), Number(element.Attribute("y1")?.Value, 0), 0, 0),
                x2: Number(element.Attribute("x2")?.Value, 0), y2: Number(element.Attribute("y2")?.Value, 0)),
            "rect" => Shape(SvgGeometryKind.Rectangle, null,
                new ThemeRect(Number(element.Attribute("x")?.Value, 0), Number(element.Attribute("y")?.Value, 0),
                    Number(element.Attribute("width")?.Value, 0), Number(element.Attribute("height")?.Value, 0)),
                Number(element.Attribute("rx")?.Value, 0),
                Number(element.Attribute("ry")?.Value, Number(element.Attribute("rx")?.Value, 0))),
            _ => null
        };
        if (shape is not null) output.Add(shape);
    }

    private static SvgInheritedStyle ReadStyle(XElement element, SvgInheritedStyle inherited) => new(
        element.Attribute("fill") is { } fill ? ThemePaintIr.Parse(fill.Value) : inherited.Fill,
        element.Attribute("stroke") is { } stroke ? ThemePaintIr.Parse(stroke.Value) : inherited.Stroke,
        Number(element.Attribute("stroke-width")?.Value, inherited.StrokeWidth),
        UnitInterval(element.Attribute("fill-opacity")?.Value, inherited.FillOpacity, "fill-opacity"),
        UnitInterval(element.Attribute("stroke-opacity")?.Value, inherited.StrokeOpacity, "stroke-opacity"),
        ParseFillRule(element.Attribute("fill-rule")?.Value, inherited.FillRule),
        ParseLineCap(element.Attribute("stroke-linecap")?.Value, inherited.LineCap),
        ParseLineJoin(element.Attribute("stroke-linejoin")?.Value, inherited.LineJoin),
        Number(element.Attribute("stroke-miterlimit")?.Value, inherited.MiterLimit),
        ParseDashArray(element.Attribute("stroke-dasharray")?.Value, inherited.DashArray),
        Number(element.Attribute("stroke-dashoffset")?.Value, inherited.DashOffset));

    public static ThemeMatrix ParseTransform(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return ThemeMatrix.Identity;
        var result = ThemeMatrix.Identity;
        var matches = TransformPattern.Matches(source);
        if (matches.Count == 0 || !string.IsNullOrWhiteSpace(TransformPattern.Replace(source, string.Empty).Trim(' ', ',')))
            throw new InvalidDataException($"Unsupported SVG transform '{source}'.");
        foreach (Match match in matches)
        {
            var values = match.Groups[2].Value.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
                .Select(RequiredNumber).ToArray();
            var next = match.Groups[1].Value switch
            {
                "translate" when values.Length is 1 or 2 => ThemeMatrix.Translation(values[0], values.Length > 1 ? values[1] : 0),
                "scale" when values.Length is 1 or 2 => ThemeMatrix.Scale(values[0], values.Length > 1 ? values[1] : values[0]),
                "matrix" when values.Length == 6 => new ThemeMatrix(values[0], values[1], values[2], values[3], values[4], values[5]),
                _ => throw new InvalidDataException($"SVG transform '{source}' has invalid arguments.")
            };
            result = next * result;
        }
        return result;
    }

    public static ThemeRect ParseViewBox(XElement root)
    {
        if (root.Attribute("viewBox")?.Value is { } source)
        {
            var values = source.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries).Select(RequiredNumber).ToArray();
            if (values.Length != 4 || values[2] <= 0 || values[3] <= 0)
                throw new InvalidDataException($"Invalid SVG viewBox '{source}'.");
            return new ThemeRect(values[0], values[1], values[2], values[3]);
        }
        var width = root.Attribute("width")?.Value is { } widthSource ? RequiredNumber(widthSource) : 1;
        var height = root.Attribute("height")?.Value is { } heightSource ? RequiredNumber(heightSource) : 1;
        if (width <= 0 || height <= 0) throw new InvalidDataException("SVG root size must be positive.");
        return new ThemeRect(0, 0, width, height);
    }

    public static AspectRatioIr ParseAspectRatio(string? source)
    {
        var tokens = (source ?? "xMidYMid meet").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length > 0 && tokens[0] == "defer") tokens = tokens[1..];
        if (tokens.Length == 0) return new AspectRatioIr(false, false, .5, .5);
        if (tokens[0] == "none") return new AspectRatioIr(true, false, 0, 0);
        var alignment = tokens[0];
        var x = alignment.StartsWith("xMin", StringComparison.Ordinal) ? 0d :
            alignment.StartsWith("xMid", StringComparison.Ordinal) ? .5 :
            alignment.StartsWith("xMax", StringComparison.Ordinal) ? 1 : double.NaN;
        var y = alignment.EndsWith("YMin", StringComparison.Ordinal) ? 0d :
            alignment.EndsWith("YMid", StringComparison.Ordinal) ? .5 :
            alignment.EndsWith("YMax", StringComparison.Ordinal) ? 1 : double.NaN;
        if (!double.IsFinite(x) || !double.IsFinite(y) || tokens.Length > 2 ||
            tokens.Length == 2 && tokens[1] is not ("meet" or "slice"))
            throw new InvalidDataException($"Invalid preserveAspectRatio value '{source}'.");
        return new AspectRatioIr(false, tokens.Length == 2 && tokens[1] == "slice", x, y);
    }

    private static SvgFillRuleIr ParseFillRule(string? value, SvgFillRuleIr fallback) => value switch
    {
        null => fallback,
        "nonzero" => SvgFillRuleIr.NonZero,
        "evenodd" => SvgFillRuleIr.EvenOdd,
        _ => throw new InvalidDataException($"Unsupported SVG fill rule '{value}'.")
    };

    private static SvgLineCapIr ParseLineCap(string? value, SvgLineCapIr fallback) => value switch
    {
        null => fallback,
        "butt" => SvgLineCapIr.Butt,
        "round" => SvgLineCapIr.Round,
        "square" => SvgLineCapIr.Square,
        _ => throw new InvalidDataException($"Unsupported SVG stroke line cap '{value}'.")
    };

    private static SvgLineJoinIr ParseLineJoin(string? value, SvgLineJoinIr fallback) => value switch
    {
        null => fallback,
        "miter" => SvgLineJoinIr.Miter,
        "round" => SvgLineJoinIr.Round,
        "bevel" => SvgLineJoinIr.Bevel,
        _ => throw new InvalidDataException($"Unsupported SVG stroke line join '{value}'.")
    };

    private static IReadOnlyList<double> ParseDashArray(string? value, IReadOnlyList<double> fallback)
    {
        if (value is null) return fallback;
        if (value == "none") return [];
        var values = value.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries).Select(RequiredNumber).ToArray();
        if (values.Length == 0 || values.Any(number => number < 0) || values.All(number => number == 0))
            throw new InvalidDataException($"Invalid SVG stroke dash array '{value}'.");
        return values.Length % 2 == 0 ? values : values.Concat(values).ToArray();
    }

    private static void ValidateUnitInterval(string? value, string name)
    {
        if (value is not null) _ = UnitInterval(value, 1, name);
    }

    private static double UnitInterval(string? value, double fallback, string name)
    {
        if (value is null) return fallback;
        var number = RequiredNumber(value);
        if (number is < 0 or > 1) throw new InvalidDataException($"SVG {name} must be between zero and one.");
        return number;
    }

    private static void ValidatePaint(string? paint)
    {
        if (string.IsNullOrWhiteSpace(paint)) return;
        _ = ThemePaintIr.Parse(paint);
    }

    private static ThemeRect ElementRect(XElement element) => new(
        Number(element.Attribute("x")?.Value, 0), Number(element.Attribute("y")?.Value, 0),
        Number(element.Attribute("width")?.Value, 0), Number(element.Attribute("height")?.Value, 0));

    public static double Number(string? value, double fallback) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? number
            : fallback;

    public static double RequiredNumber(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? number
            : throw new InvalidDataException($"Invalid SVG number '{value}'.");

    private sealed record SvgInheritedStyle(ThemePaintIr Fill, ThemePaintIr Stroke, double StrokeWidth,
        double FillOpacity, double StrokeOpacity, SvgFillRuleIr FillRule, SvgLineCapIr LineCap,
        SvgLineJoinIr LineJoin, double MiterLimit, IReadOnlyList<double> DashArray, double DashOffset)
    {
        public static SvgInheritedStyle Default { get; } = new(
            ThemePaintIr.Parse("black"), default, 1, 1, 1, SvgFillRuleIr.NonZero,
            SvgLineCapIr.Butt, SvgLineJoinIr.Miter, 4, [], 0);
    }
}
