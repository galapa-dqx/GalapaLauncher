using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Media;
using Galapa.Launcher.Theming;

namespace Galapa.Launcher.Views.Controls;

/// <summary>A small, safe SVG paint model for compiled theme marks and slices.</summary>
internal sealed class InlineSvgDocument
{
    private static readonly Regex TransformPattern = new(@"(matrix|translate|scale)\s*\(([^)]*)\)", RegexOptions.Compiled);

    public Rect ViewBox { get; }
    public IReadOnlyList<SvgPaintShape> Shapes { get; }

    private InlineSvgDocument(Rect viewBox, IReadOnlyList<SvgPaintShape> shapes)
    {
        ViewBox = viewBox;
        Shapes = shapes;
    }

    public static InlineSvgDocument Parse(string svg)
    {
        var root = XDocument.Parse(svg, LoadOptions.None).Root ?? throw new InvalidDataException("SVG has no root.");
        var viewBox = ParseViewBox(root);
        return new InlineSvgDocument(viewBox, ParseShapes(root));
    }

    public static IReadOnlyList<SvgPaintShape> ParseShapes(XElement container)
    {
        var output = new List<SvgPaintShape>();
        foreach (var child in container.Elements())
            ParseElement(child, Matrix.Identity, null, null, 1, output);
        return output;
    }

    public void Draw(DrawingContext context, Rect destination, IBrush? currentColor, bool uniform = true)
    {
        if (ViewBox.Width <= 0 || ViewBox.Height <= 0 || destination.Width <= 0 || destination.Height <= 0) return;
        var sx = destination.Width / ViewBox.Width;
        var sy = destination.Height / ViewBox.Height;
        var dx = destination.X;
        var dy = destination.Y;
        if (uniform)
        {
            var scale = Math.Min(sx, sy);
            dx += (destination.Width - ViewBox.Width * scale) / 2;
            dy += (destination.Height - ViewBox.Height * scale) / 2;
            sx = sy = scale;
        }
        var map = Matrix.CreateTranslation(-ViewBox.X, -ViewBox.Y) * Matrix.CreateScale(sx, sy) * Matrix.CreateTranslation(dx, dy);
        DrawShapes(context, Shapes, map, currentColor);
    }

    public static void DrawShapes(DrawingContext context, IReadOnlyList<SvgPaintShape> shapes, Matrix map, IBrush? currentColor)
    {
        foreach (var shape in shapes)
        {
            var fill = shape.Fill.Resolve(currentColor);
            var pen = shape.ResolvePen(currentColor);
            using (context.PushTransform(shape.Transform * map))
                context.DrawGeometry(fill, pen, shape.Geometry);
        }
    }

    private static void ParseElement(XElement element, Matrix parentTransform, string? inheritedFill,
        string? inheritedStroke, double inheritedStrokeWidth, ICollection<SvgPaintShape> output)
    {
        var transform = ParseTransform(element.Attribute("transform")?.Value) * parentTransform;
        var fill = element.Attribute("fill")?.Value ?? inheritedFill;
        var stroke = element.Attribute("stroke")?.Value ?? inheritedStroke;
        var strokeWidth = Number(element.Attribute("stroke-width")?.Value, inheritedStrokeWidth);
        if (!double.IsFinite(strokeWidth) || strokeWidth < 0)
            throw new InvalidDataException("SVG stroke width must be finite and non-negative.");

        if (element.Name.LocalName is "g" or "svg")
        {
            foreach (var child in element.Elements())
                ParseElement(child, transform, fill, stroke, strokeWidth, output);
            return;
        }

        Geometry? geometry = element.Name.LocalName switch
        {
            "path" when element.Attribute("d")?.Value is { Length: > 0 } data => StreamGeometry.Parse(data),
            "circle" => Circle(element),
            "line" => Line(element),
            "rect" => Rectangle(element),
            _ => null
        };
        if (geometry is not null)
            output.Add(new SvgPaintShape(geometry, SvgPaint.Parse(fill), SvgPaint.Parse(stroke), strokeWidth, transform));
    }

    private static Geometry? Circle(XElement element)
    {
        var radius = Number(element.Attribute("r")?.Value, 0);
        if (radius <= 0) return null;
        var cx = Number(element.Attribute("cx")?.Value, 0);
        var cy = Number(element.Attribute("cy")?.Value, 0);
        return new EllipseGeometry(new Rect(cx - radius, cy - radius, radius * 2, radius * 2));
    }

    private static Geometry Line(XElement element)
    {
        var geometry = new StreamGeometry();
        using var writer = geometry.Open();
        writer.BeginFigure(new Point(Number(element.Attribute("x1")?.Value, 0), Number(element.Attribute("y1")?.Value, 0)), false);
        writer.LineTo(new Point(Number(element.Attribute("x2")?.Value, 0), Number(element.Attribute("y2")?.Value, 0)));
        writer.EndFigure(false);
        return geometry;
    }

    private static Geometry Rectangle(XElement element)
    {
        var rect = new Rect(Number(element.Attribute("x")?.Value, 0), Number(element.Attribute("y")?.Value, 0),
            Number(element.Attribute("width")?.Value, 0), Number(element.Attribute("height")?.Value, 0));
        var rx = Number(element.Attribute("rx")?.Value, 0);
        var ry = Number(element.Attribute("ry")?.Value, rx);
        return new RectangleGeometry(rect, rx, ry);
    }

    internal static Matrix ParseTransform(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return Matrix.Identity;
        var result = Matrix.Identity;
        var matches = TransformPattern.Matches(source);
        if (matches.Count == 0 || !string.IsNullOrWhiteSpace(TransformPattern.Replace(source, string.Empty).Trim(' ', ',')))
            throw new InvalidDataException($"Unsupported SVG transform '{source}'.");
        foreach (Match match in matches)
        {
            var values = match.Groups[2].Value.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
                .Select(RequiredNumber).ToArray();
            var next = match.Groups[1].Value switch
            {
                "translate" when values.Length is 1 or 2 => Matrix.CreateTranslation(values[0], values.Length > 1 ? values[1] : 0),
                "scale" when values.Length is 1 or 2 => Matrix.CreateScale(values[0], values.Length > 1 ? values[1] : values[0]),
                "matrix" when values.Length == 6 => new Matrix(values[0], values[1], values[2], values[3], values[4], values[5]),
                _ => throw new InvalidDataException($"SVG transform '{source}' has invalid arguments.")
            };
            // Avalonia matrices use row-vector composition. SVG's
            // translate(24) scale(-1) mirror therefore becomes S * T so the
            // visual result is 24-x rather than -24-x.
            result = next * result;
        }
        return result;
    }

    internal static Rect ParseViewBox(XElement root)
    {
        if (root.Attribute("viewBox")?.Value is { } source)
        {
            var values = source.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
                .Select(RequiredNumber).ToArray();
            if (values.Length != 4 || values[2] <= 0 || values[3] <= 0)
                throw new InvalidDataException($"Invalid SVG viewBox '{source}'.");
            return new Rect(values[0], values[1], values[2], values[3]);
        }
        var width = root.Attribute("width")?.Value is { } widthSource ? RequiredNumber(widthSource) : 1;
        var height = root.Attribute("height")?.Value is { } heightSource ? RequiredNumber(heightSource) : 1;
        if (width <= 0 || height <= 0) throw new InvalidDataException("SVG root size must be positive.");
        return new Rect(0, 0, width, height);
    }

    internal static double Number(string? value, double fallback) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? number
            : fallback;

    internal static double RequiredNumber(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && double.IsFinite(number)
            ? number
            : throw new InvalidDataException($"Invalid SVG number '{value}'.");
}

/// <summary>
/// Validates the safe renderer subset without touching Avalonia's platform
/// render interface. Theme discovery runs before a renderer is guaranteed to
/// exist, so package validation must remain usable in a plain unit test.
/// </summary>
internal static class ThemeSvgValidator
{
    private static readonly HashSet<string> SupportedElements =
        ["svg", "g", "path", "circle", "line", "rect"];
    private static readonly string[] NumericAttributes =
        ["x", "y", "x1", "y1", "x2", "y2", "cx", "cy", "r", "rx", "ry", "width", "height", "stroke-width"];

    public static void Validate(XElement root, bool nineSlice)
    {
        _ = InlineSvgDocument.ParseViewBox(root);
        foreach (var element in root.DescendantsAndSelf())
        {
            if (!SupportedElements.Contains(element.Name.LocalName))
                throw new InvalidDataException($"Unsupported SVG element '{element.Name.LocalName}'.");
            _ = InlineSvgDocument.ParseTransform(element.Attribute("transform")?.Value);
            ValidatePaint(element.Attribute("fill")?.Value);
            ValidatePaint(element.Attribute("stroke")?.Value);
            foreach (var attribute in NumericAttributes)
                if (element.Attribute(attribute)?.Value is { } value)
                    _ = InlineSvgDocument.RequiredNumber(value);
            if (element.Name.LocalName == "path" && string.IsNullOrWhiteSpace(element.Attribute("d")?.Value))
                throw new InvalidDataException("SVG path has no path data.");
        }

        if (!nineSlice) return;
        var slices = root.Descendants()
            .Where(x => x.Name.LocalName == "svg" && Regex.IsMatch(x.Attribute("id")?.Value ?? "", @"^\d+_\d+$"))
            .ToArray();
        if (slices.Length == 0) throw new InvalidDataException("Nine-slice SVG has no slices.");
        var positions = slices.Select(x => x.Attribute("id")!.Value).ToArray();
        if (positions.Distinct(StringComparer.Ordinal).Count() != positions.Length)
            throw new InvalidDataException("Nine-slice SVG contains duplicate slice positions.");
        var parsed = positions.Select(id => id.Split('_').Select(int.Parse).ToArray()).ToArray();
        var columns = parsed.Max(x => x[0]) + 1;
        var rows = parsed.Max(x => x[1]) + 1;
        if (columns is not (1 or 3) || rows is not (1 or 3) || slices.Length != columns * rows)
            throw new InvalidDataException($"Unsupported nine-slice grid {columns}x{rows}.");
        for (var column = 0; column < columns; column++)
        for (var row = 0; row < rows; row++)
            if (!positions.Contains($"{column}_{row}", StringComparer.Ordinal))
                throw new InvalidDataException($"Nine-slice SVG is missing slice '{column}_{row}'.");
        foreach (var slice in slices)
        {
            if (InlineSvgDocument.Number(slice.Attribute("width")?.Value, 0) <= 0 ||
                InlineSvgDocument.Number(slice.Attribute("height")?.Value, 0) <= 0)
                throw new InvalidDataException($"Nine-slice cell '{slice.Attribute("id")?.Value}' has an invalid size.");
            _ = InlineSvgDocument.ParseViewBox(slice);
            _ = NineSliceSvg.ParseAspectRatio(slice.Attribute("preserveAspectRatio")?.Value);
            if (slice.Attribute("data-slice-repeat")?.Value is { } repeat &&
                repeat is not ("stretch" or "repeat" or "round" or "space"))
                throw new InvalidDataException($"Nine-slice cell '{slice.Attribute("id")?.Value}' has invalid repeat mode '{repeat}'.");
        }
    }

    private static void ValidatePaint(string? paint)
    {
        if (string.IsNullOrWhiteSpace(paint) || paint is "none" or "currentColor") return;
        _ = ThemePaint.ParseColor(paint);
    }
}

internal readonly record struct SvgPaint(IBrush? Brush, bool UsesCurrentColor)
{
    public IBrush? Resolve(IBrush? currentColor) => UsesCurrentColor ? currentColor : Brush;

    public static SvgPaint Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "none") return default;
        if (value == "currentColor") return new SvgPaint(null, true);
        if (value.StartsWith("url(", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SVG URL paints are not supported.");
        return new SvgPaint(ThemePaint.Brush(value), false);
    }
}

internal sealed class SvgPaintShape(
    Geometry geometry,
    SvgPaint fill,
    SvgPaint stroke,
    double strokeWidth,
    Matrix transform)
{
    private IBrush? _lastCurrentColor;
    private Pen? _lastCurrentColorPen;
    private readonly Pen? _fixedPen = stroke.Brush is null ? null : new Pen(stroke.Brush, strokeWidth);

    public Geometry Geometry { get; } = geometry;
    public SvgPaint Fill { get; } = fill;
    public Matrix Transform { get; } = transform;

    public Pen? ResolvePen(IBrush? currentColor)
    {
        if (!stroke.UsesCurrentColor) return _fixedPen;
        if (currentColor is null) return null;
        if (ReferenceEquals(currentColor, _lastCurrentColor)) return _lastCurrentColorPen;
        _lastCurrentColor = currentColor;
        return _lastCurrentColorPen = new Pen(currentColor, strokeWidth);
    }
}

internal static class ThemeSvgCache
{
    private static readonly ConditionalWeakTable<string, InlineSvgDocument> Documents = new();
    private static readonly ConditionalWeakTable<string, NineSliceSvg> Slices = new();

    public static InlineSvgDocument Document(string source) =>
        Documents.GetValue(source, static value => InlineSvgDocument.Parse(value));

    public static NineSliceSvg NineSlice(string source) =>
        Slices.GetValue(source, static value => NineSliceSvg.Parse(value));
}

internal static class ThemeRenderAssets
{
    /// <summary>
    /// Materializes every immutable renderer asset while a package is being
    /// admitted to the catalog. ThemePart instances then share these objects;
    /// hover, focus, and theme switching never reparse XML or path data.
    /// </summary>
    public static void Prepare(ThemePackage package)
    {
        foreach (var control in package.Compiled.Controls.Values)
        {
            PrepareControl(control, control.Shape);
            if (control.States is null) continue;
            foreach (var state in control.States.Values)
                PrepareControl(state, control.Shape);
        }
    }

    private static void PrepareControl(CompiledControl control, string? inheritedShape)
    {
        if (control.Art is not null)
        {
            if (inheritedShape != "Asset")
                throw new InvalidDataException("Only Asset controls may contain nine-slice artwork.");
            _ = ThemeSvgCache.NineSlice(control.Art);
        }
        if (control.Image is not null) _ = ThemeSvgCache.Document(control.Image);
        if (control.Images is not null)
            foreach (var image in control.Images.Values)
                _ = ThemeSvgCache.Document(image);
    }
}

internal sealed class NineSliceSvg
{
    public IReadOnlyList<double?> Columns { get; }
    public IReadOnlyList<double?> Rows { get; }
    public Thickness ContentPadding { get; }
    public Thickness Outset { get; }
    public IReadOnlyList<NineSliceCell> Cells { get; }

    private NineSliceSvg(IReadOnlyList<double?> columns, IReadOnlyList<double?> rows,
        Thickness contentPadding, Thickness outset, IReadOnlyList<NineSliceCell> cells)
    {
        Columns = columns;
        Rows = rows;
        ContentPadding = contentPadding;
        Outset = outset;
        Cells = cells;
    }

    public static NineSliceSvg Parse(string svg)
    {
        var root = XDocument.Parse(svg, LoadOptions.None).Root ?? throw new InvalidDataException("Nine-slice SVG has no root.");
        var rootViewBox = InlineSvgDocument.ParseViewBox(root);
        var rootWidth = InlineSvgDocument.Number(root.Attribute("width")?.Value, rootViewBox.Width);
        var rootHeight = InlineSvgDocument.Number(root.Attribute("height")?.Value, rootViewBox.Height);
        var scale = rootWidth / rootViewBox.Width;
        var verticalScale = rootHeight / rootViewBox.Height;
        if (!double.IsFinite(scale) || scale <= 0 || !double.IsFinite(verticalScale) || verticalScale <= 0 ||
            Math.Abs(scale - verticalScale) > .001)
            throw new InvalidDataException("Nine-slice SVG has an invalid root size.");
        var sliceElements = root.Descendants().Where(x => x.Name.LocalName == "svg" && Regex.IsMatch(x.Attribute("id")?.Value ?? "", @"^\d+_\d+$")).ToArray();
        if (sliceElements.Length == 0) throw new InvalidDataException("Nine-slice SVG has no slices.");

        var cells = new List<NineSliceCell>();
        var columns = 0;
        var rows = 0;
        foreach (var element in sliceElements)
        {
            var position = element.Attribute("id")!.Value.Split('_').Select(int.Parse).ToArray();
            columns = Math.Max(columns, position[0] + 1);
            rows = Math.Max(rows, position[1] + 1);
            var width = InlineSvgDocument.Number(element.Attribute("width")?.Value, 0);
            var height = InlineSvgDocument.Number(element.Attribute("height")?.Value, 0);
            var viewBox = element.Attribute("viewBox") is not null ? InlineSvgDocument.ParseViewBox(element) : new Rect(0, 0, width, height);
            if (width <= 0 || height <= 0 || !double.IsFinite(viewBox.Width) || !double.IsFinite(viewBox.Height) ||
                viewBox.Width <= 0 || viewBox.Height <= 0)
                throw new InvalidDataException($"Nine-slice cell '{element.Attribute("id")?.Value}' has an invalid size.");
            var repeat = element.Attribute("data-slice-repeat")?.Value ?? "stretch";
            if (repeat is not ("stretch" or "repeat" or "round" or "space")) repeat = "stretch";
            cells.Add(new NineSliceCell(position[0], position[1], width * scale, height * scale, viewBox,
                element.Attribute("preserveAspectRatio")?.Value ?? "none", repeat,
                InlineSvgDocument.ParseShapes(element)));
        }
        if (columns is not (1 or 3) || rows is not (1 or 3) || cells.Count != columns * rows)
            throw new InvalidDataException($"Unsupported nine-slice grid {columns}x{rows}.");
        if (cells.Select(cell => (cell.Column, cell.Row)).Distinct().Count() != cells.Count)
            throw new InvalidDataException("Nine-slice SVG contains duplicate slice positions.");
        for (var column = 0; column < columns; column++)
        for (var row = 0; row < rows; row++)
            if (!cells.Any(cell => cell.Column == column && cell.Row == row))
                throw new InvalidDataException($"Nine-slice SVG is missing slice '{column}_{row}'.");

        var frame = root.Descendants().FirstOrDefault(x => x.Attribute("id")?.Value == "frame");
        var frameRect = frame is null ? rootViewBox : ElementRect(frame);
        var outset = new Thickness(
            Math.Max(0, (frameRect.X - rootViewBox.X) * scale),
            Math.Max(0, (frameRect.Y - rootViewBox.Y) * scale),
            Math.Max(0, (rootViewBox.Right - frameRect.Right) * scale),
            Math.Max(0, (rootViewBox.Bottom - frameRect.Bottom) * scale));
        var content = root.Descendants().FirstOrDefault(x => x.Attribute("id")?.Value == "content");
        var padding = new Thickness();
        if (content is not null)
        {
            var rect = ElementRect(content);
            padding = new Thickness(
                Math.Max(0, (rect.X - frameRect.X) * scale),
                Math.Max(0, (rect.Y - frameRect.Y) * scale),
                Math.Max(0, (frameRect.Right - rect.Right) * scale),
                Math.Max(0, (frameRect.Bottom - rect.Bottom) * scale));
        }

        double?[] Track(int count, bool horizontal) => Enumerable.Range(0, count).Select(i =>
            count == 1 || i == 1 ? (double?)null : cells.First(x => horizontal ? x.Column == i : x.Row == i) is { } cell ?
                horizontal ? cell.Width : cell.Height : 0).ToArray();
        return new NineSliceSvg(Track(columns, true), Track(rows, false), padding, outset, cells);
    }

    public void Draw(DrawingContext context, Rect hostBounds, IBrush? currentColor)
    {
        var painted = new Rect(hostBounds.X - Outset.Left, hostBounds.Y - Outset.Top,
            hostBounds.Width + Outset.Left + Outset.Right, hostBounds.Height + Outset.Top + Outset.Bottom);
        var capX = Columns.Where(x => x.HasValue).Sum(x => x!.Value);
        var capY = Rows.Where(x => x.HasValue).Sum(x => x!.Value);
        var factor = Math.Min(1, Math.Min(capX > 0 ? painted.Width / capX : 1, capY > 0 ? painted.Height / capY : 1));
        var widths = Tracks(Columns, painted.Width, factor);
        var heights = Tracks(Rows, painted.Height, factor);
        var xs = Positions(painted.X, widths);
        var ys = Positions(painted.Y, heights);

        foreach (var cell in Cells)
        {
            var destination = new Rect(xs[cell.Column], ys[cell.Row], widths[cell.Column], heights[cell.Row]);
            if (destination.Width <= 0 || destination.Height <= 0) continue;
            var tileX = Columns.Count == 3 && Columns[cell.Column] is null && cell.Repeat != "stretch";
            var tileY = Rows.Count == 3 && Rows[cell.Row] is null && cell.Repeat != "stretch";
            if (tileX || tileY)
            {
                var horizontal = TileSegments(destination.X, destination.Width, tileX ? cell.Width * factor : destination.Width, tileX ? cell.Repeat : "stretch");
                var vertical = TileSegments(destination.Y, destination.Height, tileY ? cell.Height * factor : destination.Height, tileY ? cell.Repeat : "stretch");
                using (context.PushClip(destination))
                    foreach (var x in horizontal)
                    foreach (var y in vertical)
                        DrawCell(context, cell, new Rect(x.Start, y.Start, x.Length, y.Length), currentColor, preserveAspectRatio: false);
                continue;
            }
            using (context.PushClip(destination))
                DrawCell(context, cell, destination, currentColor, preserveAspectRatio: true);
        }
    }

    private static void DrawCell(DrawingContext context, NineSliceCell cell, Rect destination,
        IBrush? currentColor, bool preserveAspectRatio)
    {
        if (!double.IsFinite(cell.ViewBox.Width) || !double.IsFinite(cell.ViewBox.Height) ||
            cell.ViewBox.Width <= 0 || cell.ViewBox.Height <= 0)
            return;
        var sx = destination.Width / cell.ViewBox.Width;
        var sy = destination.Height / cell.ViewBox.Height;
        var alignment = ParseAspectRatio(cell.PreserveAspectRatio);
        if (preserveAspectRatio && !alignment.None)
        {
            var scale = alignment.Slice ? Math.Max(sx, sy) : Math.Min(sx, sy);
            sx = sy = scale;
        }
        var dx = destination.X + (destination.Width - cell.ViewBox.Width * sx) * alignment.X;
        var dy = destination.Y + (destination.Height - cell.ViewBox.Height * sy) * alignment.Y;
        var map = Matrix.CreateTranslation(-cell.ViewBox.X, -cell.ViewBox.Y) * Matrix.CreateScale(sx, sy) * Matrix.CreateTranslation(dx, dy);
        InlineSvgDocument.DrawShapes(context, cell.Shapes, map, currentColor);
    }

    internal static IReadOnlyList<TileSegment> TileSegments(double start, double available, double natural, string mode)
    {
        if (available <= 0) return [];
        if (mode == "stretch" || natural <= 0)
            return [new TileSegment(start, available)];

        if (mode == "round")
        {
            var count = Math.Max(1, (int)Math.Round(available / natural));
            var length = available / count;
            return Enumerable.Range(0, count).Select(i => new TileSegment(start + i * length, length)).ToArray();
        }

        if (mode == "space")
        {
            var count = Math.Max(1, (int)Math.Floor(available / natural));
            if (count == 1)
                return [new TileSegment(start + (available - natural) / 2, natural)];
            var gap = (available - count * natural) / (count - 1);
            return Enumerable.Range(0, count).Select(i => new TileSegment(start + i * (natural + gap), natural)).ToArray();
        }

        // CSS background-repeat anchors the tile at background-position:center,
        // then repeats in both directions. Preserve the overhanging first and
        // last tiles so the destination clip produces symmetric partial tiles.
        var centered = start + (available - natural) / 2;
        var first = centered - Math.Ceiling((centered - start) / natural) * natural;
        var repeatCount = Math.Max(1, (int)Math.Ceiling((start + available - first) / natural));
        return Enumerable.Range(0, repeatCount)
            .Select(i => new TileSegment(first + i * natural, natural)).ToArray();
    }

    internal static double[] Tracks(IReadOnlyList<double?> source, double total, double factor)
    {
        var fixedTotal = source.Where(x => x.HasValue).Sum(x => x!.Value * factor);
        var flexible = Math.Max(0, total - fixedTotal);
        var flexibleCount = source.Count(x => !x.HasValue);
        return source.Select(x => x.HasValue ? x.Value * factor : flexible / Math.Max(1, flexibleCount)).ToArray();
    }

    private static double[] Positions(double start, IReadOnlyList<double> sizes)
    {
        var result = new double[sizes.Count];
        for (var i = 0; i < result.Length; i++) result[i] = i == 0 ? start : result[i - 1] + sizes[i - 1];
        return result;
    }

    private static Rect ElementRect(XElement element) => new(
        InlineSvgDocument.Number(element.Attribute("x")?.Value, 0), InlineSvgDocument.Number(element.Attribute("y")?.Value, 0),
        InlineSvgDocument.Number(element.Attribute("width")?.Value, 0), InlineSvgDocument.Number(element.Attribute("height")?.Value, 0));

    internal static AspectRatioAlignment ParseAspectRatio(string? source)
    {
        var tokens = (source ?? "xMidYMid meet").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length > 0 && tokens[0] == "defer") tokens = tokens[1..];
        if (tokens.Length == 0) return new AspectRatioAlignment(false, false, .5, .5);
        if (tokens[0] == "none") return new AspectRatioAlignment(true, false, 0, 0);
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
        return new AspectRatioAlignment(false, tokens.Length == 2 && tokens[1] == "slice", x, y);
    }
}

internal sealed record NineSliceCell(int Column, int Row, double Width, double Height, Rect ViewBox,
    string PreserveAspectRatio, string Repeat, IReadOnlyList<SvgPaintShape> Shapes);
internal readonly record struct TileSegment(double Start, double Length);
internal readonly record struct AspectRatioAlignment(bool None, bool Slice, double X, double Y);
