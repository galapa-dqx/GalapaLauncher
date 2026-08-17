using System.Globalization;
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
            var fill = Paint(shape.Fill, currentColor);
            var stroke = Paint(shape.Stroke, currentColor);
            var pen = stroke is null ? null : new Pen(stroke, shape.StrokeWidth);
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
            output.Add(new SvgPaintShape(geometry, fill, stroke, strokeWidth, transform));
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

    private static Matrix ParseTransform(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return Matrix.Identity;
        var result = Matrix.Identity;
        foreach (Match match in TransformPattern.Matches(source))
        {
            var values = match.Groups[2].Value.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
                .Select(x => Number(x, 0)).ToArray();
            var next = match.Groups[1].Value switch
            {
                "translate" when values.Length >= 1 => Matrix.CreateTranslation(values[0], values.Length > 1 ? values[1] : 0),
                "scale" when values.Length >= 1 => Matrix.CreateScale(values[0], values.Length > 1 ? values[1] : values[0]),
                "matrix" when values.Length == 6 => new Matrix(values[0], values[1], values[2], values[3], values[4], values[5]),
                _ => Matrix.Identity
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
        var values = (root.Attribute("viewBox")?.Value ?? string.Empty)
            .Split([' ', ','], StringSplitOptions.RemoveEmptyEntries).Select(x => Number(x, 0)).ToArray();
        if (values.Length == 4 && values[2] > 0 && values[3] > 0)
            return new Rect(values[0], values[1], values[2], values[3]);
        return new Rect(0, 0, Math.Max(1, Number(root.Attribute("width")?.Value, 1)),
            Math.Max(1, Number(root.Attribute("height")?.Value, 1)));
    }

    internal static double Number(string? value, double fallback) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number : fallback;

    private static IBrush? Paint(string? value, IBrush? currentColor)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "none") return null;
        if (value == "currentColor") return currentColor;
        if (value.StartsWith("url(", StringComparison.OrdinalIgnoreCase)) return null;
        return ThemePaint.Brush(value);
    }
}

internal sealed record SvgPaintShape(Geometry Geometry, string? Fill, string? Stroke, double StrokeWidth, Matrix Transform);

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
        var scale = InlineSvgDocument.Number(root.Attribute("width")?.Value, rootViewBox.Width) / rootViewBox.Width;
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
            var repeat = element.Attribute("data-slice-repeat")?.Value ?? "stretch";
            if (repeat is not ("stretch" or "repeat" or "round" or "space")) repeat = "stretch";
            cells.Add(new NineSliceCell(position[0], position[1], width * scale, height * scale, viewBox,
                element.Attribute("preserveAspectRatio")?.Value ?? "none", repeat,
                InlineSvgDocument.ParseShapes(element)));
        }
        if (columns is not (1 or 3) || rows is not (1 or 3) || cells.Count != columns * rows)
            throw new InvalidDataException($"Unsupported nine-slice grid {columns}x{rows}.");

        var frame = root.Descendants().FirstOrDefault(x => x.Attribute("id")?.Value == "frame");
        var frameRect = frame is null ? rootViewBox : ElementRect(frame);
        var outset = new Thickness((frameRect.X - rootViewBox.X) * scale, (frameRect.Y - rootViewBox.Y) * scale,
            (rootViewBox.Right - frameRect.Right) * scale, (rootViewBox.Bottom - frameRect.Bottom) * scale);
        var content = root.Descendants().FirstOrDefault(x => x.Attribute("id")?.Value == "content");
        var padding = new Thickness();
        if (content is not null)
        {
            var rect = ElementRect(content);
            padding = new Thickness((rect.X - frameRect.X) * scale, (rect.Y - frameRect.Y) * scale,
                (frameRect.Right - rect.Right) * scale, (frameRect.Bottom - rect.Bottom) * scale);
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
        var sx = destination.Width / cell.ViewBox.Width;
        var sy = destination.Height / cell.ViewBox.Height;
        if (preserveAspectRatio && cell.PreserveAspectRatio != "none")
        {
            var scale = cell.PreserveAspectRatio.Contains("slice", StringComparison.OrdinalIgnoreCase) ? Math.Max(sx, sy) : Math.Min(sx, sy);
            sx = sy = scale;
        }
        var dx = destination.X + (destination.Width - cell.ViewBox.Width * sx) / 2;
        var dy = destination.Y + (destination.Height - cell.ViewBox.Height * sy) / 2;
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
}

internal sealed record NineSliceCell(int Column, int Row, double Width, double Height, Rect ViewBox,
    string PreserveAspectRatio, string Repeat, IReadOnlyList<SvgPaintShape> Shapes);
internal readonly record struct TileSegment(double Start, double Length);
