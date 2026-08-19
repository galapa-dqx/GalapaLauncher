using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using Galapa.Launcher.Theming;

namespace Galapa.Launcher.Views.Controls;

/// <summary>A UI-thread materialization of a validated SVG document.</summary>
public sealed class InlineSvgDocument
{
    public Rect ViewBox { get; }
    public IReadOnlyList<SvgPaintShape> Shapes { get; }

    private InlineSvgDocument(Rect viewBox, IReadOnlyList<SvgPaintShape> shapes)
    {
        ViewBox = viewBox;
        Shapes = shapes;
    }

    public static InlineSvgDocument Load(SvgDocumentIr source)
    {
        Dispatcher.UIThread.VerifyAccess();
        return new InlineSvgDocument(ToRect(source.ViewBox), source.Shapes.Select(LoadShape).ToArray());
    }

    /// <summary>Compatibility helper for app-owned/test SVGs; theme loading uses validated IR.</summary>
    public static InlineSvgDocument Parse(string source) =>
        Load(new ThemeSvgIrParser().ParseDocument(source));

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
        var map = Matrix.CreateTranslation(-ViewBox.X, -ViewBox.Y) * Matrix.CreateScale(sx, sy) *
                  Matrix.CreateTranslation(dx, dy);
        DrawShapes(context, Shapes, map, currentColor);
    }

    internal static void DrawShapes(DrawingContext context, IReadOnlyList<SvgPaintShape> shapes,
        Matrix map, IBrush? currentColor)
    {
        foreach (var shape in shapes)
        {
            using (context.PushTransform(shape.Transform * map))
                context.DrawGeometry(shape.Fill.Resolve(currentColor), shape.ResolvePen(currentColor), shape.Geometry);
        }
    }

    private static SvgPaintShape LoadShape(SvgShapeIr shape)
    {
        Geometry geometry = shape.Kind switch
        {
            SvgGeometryKind.Path => StreamGeometry.Parse(shape.PathData!),
            SvgGeometryKind.Circle => new EllipseGeometry(ToRect(shape.Rect)),
            SvgGeometryKind.Rectangle => new RectangleGeometry(ToRect(shape.Rect), shape.RadiusX, shape.RadiusY),
            SvgGeometryKind.Line => Line(shape),
            _ => throw new InvalidDataException($"Unsupported SVG geometry kind '{shape.Kind}'.")
        };
        return new SvgPaintShape(geometry, SvgPaint.Load(shape.Fill), SvgPaint.Load(shape.Stroke),
            shape.StrokeWidth, ToMatrix(shape.Transform));
    }

    private static Geometry Line(SvgShapeIr shape)
    {
        var geometry = new StreamGeometry();
        using var writer = geometry.Open();
        writer.BeginFigure(new Point(shape.Rect.X, shape.Rect.Y), false);
        writer.LineTo(new Point(shape.X2, shape.Y2));
        writer.EndFigure(false);
        return geometry;
    }

    internal static Rect ToRect(ThemeRect value) => new(value.X, value.Y, value.Width, value.Height);
    internal static Matrix ToMatrix(ThemeMatrix value) =>
        new(value.M11, value.M12, value.M21, value.M22, value.M31, value.M32);
}

internal readonly record struct SvgPaint(IBrush? Brush, bool UsesCurrentColor)
{
    public IBrush? Resolve(IBrush? currentColor) => UsesCurrentColor ? currentColor : Brush;

    public static SvgPaint Load(ThemePaintIr value) => value.Kind switch
    {
        ThemePaintKind.None => default,
        ThemePaintKind.CurrentColor => new SvgPaint(null, true),
        ThemePaintKind.Color => new SvgPaint(ThemePaint.Brush(value.Color), false),
        _ => throw new ArgumentOutOfRangeException(nameof(value))
    };
}

public sealed class SvgPaintShape
{
    private readonly SvgPaint _stroke;
    private readonly double _strokeWidth;
    private readonly Pen? _fixedPen;
    private IBrush? _lastCurrentColor;
    private Pen? _lastCurrentColorPen;

    internal SvgPaintShape(Geometry geometry, SvgPaint fill, SvgPaint stroke, double strokeWidth, Matrix transform)
    {
        Geometry = geometry;
        Fill = fill;
        _stroke = stroke;
        _strokeWidth = strokeWidth;
        Transform = transform;
        _fixedPen = stroke.Brush is null ? null : new Pen(stroke.Brush, strokeWidth);
    }

    public Geometry Geometry { get; }
    internal SvgPaint Fill { get; }
    public Matrix Transform { get; }

    public Pen? ResolvePen(IBrush? currentColor)
    {
        if (!_stroke.UsesCurrentColor) return _fixedPen;
        if (currentColor is null) return null;
        if (ReferenceEquals(currentColor, _lastCurrentColor)) return _lastCurrentColorPen;
        _lastCurrentColor = currentColor;
        return _lastCurrentColorPen = new Pen(currentColor, _strokeWidth);
    }
}

/// <summary>A UI-thread materialization of validated nine-slice data.</summary>
public sealed class NineSliceSvg
{
    private Rect _lastHostBounds;
    private IReadOnlyList<NineSliceDrawOperation>? _lastLayout;
    private readonly double _fixedWidth;
    private readonly double _fixedHeight;

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
        _fixedWidth = columns.Where(value => value.HasValue).Sum(value => value!.Value);
        _fixedHeight = rows.Where(value => value.HasValue).Sum(value => value!.Value);
    }

    public static NineSliceSvg Load(NineSliceIr source)
    {
        Dispatcher.UIThread.VerifyAccess();
        var cells = source.Cells.Select(cell => new NineSliceCell(
            cell.Column, cell.Row, cell.Width, cell.Height, InlineSvgDocument.ToRect(cell.ViewBox),
            new AspectRatioAlignment(cell.AspectRatio.None, cell.AspectRatio.Slice, cell.AspectRatio.X, cell.AspectRatio.Y),
            cell.Repeat, cell.Shapes.Select(LoadShape).ToArray())).ToArray();
        return new NineSliceSvg(source.Columns, source.Rows, ToThickness(source.ContentPadding),
            ToThickness(source.Outset), cells);
    }

    /// <summary>Compatibility helper for focused renderer tests.</summary>
    public static NineSliceSvg Parse(string source) =>
        Load(new ThemeSvgIrParser().ParseNineSlice(source));

    public void Draw(DrawingContext context, Rect hostBounds, IBrush? currentColor)
    {
        var layout = hostBounds == _lastHostBounds && _lastLayout is not null
            ? _lastLayout
            : _lastLayout = BuildLayout(hostBounds);
        _lastHostBounds = hostBounds;
        foreach (var operation in layout)
        {
            using (context.PushClip(operation.Clip))
                DrawCell(context, operation.Cell, operation.Destination, currentColor,
                    operation.PreserveAspectRatio);
        }
    }

    private IReadOnlyList<NineSliceDrawOperation> BuildLayout(Rect hostBounds)
    {
        var painted = new Rect(hostBounds.X - Outset.Left, hostBounds.Y - Outset.Top,
            hostBounds.Width + Outset.Left + Outset.Right,
            hostBounds.Height + Outset.Top + Outset.Bottom);
        var factor = Math.Min(1, Math.Min(_fixedWidth > 0 ? painted.Width / _fixedWidth : 1,
            _fixedHeight > 0 ? painted.Height / _fixedHeight : 1));
        var widths = Tracks(Columns, painted.Width, factor);
        var heights = Tracks(Rows, painted.Height, factor);
        var xs = Positions(painted.X, widths);
        var ys = Positions(painted.Y, heights);
        var operations = new List<NineSliceDrawOperation>();
        foreach (var cell in Cells)
        {
            var destination = new Rect(xs[cell.Column], ys[cell.Row], widths[cell.Column], heights[cell.Row]);
            if (destination.Width <= 0 || destination.Height <= 0) continue;
            var tileX = Columns.Count == 3 && Columns[cell.Column] is null && cell.Repeat != "stretch";
            var tileY = Rows.Count == 3 && Rows[cell.Row] is null && cell.Repeat != "stretch";
            if (tileX || tileY)
            {
                var horizontal = TileSegments(destination.X, destination.Width,
                    tileX ? cell.Width * factor : destination.Width, tileX ? cell.Repeat : "stretch");
                var vertical = TileSegments(destination.Y, destination.Height,
                    tileY ? cell.Height * factor : destination.Height, tileY ? cell.Repeat : "stretch");
                foreach (var x in horizontal)
                foreach (var y in vertical)
                    operations.Add(new NineSliceDrawOperation(cell, destination,
                        new Rect(x.Start, y.Start, x.Length, y.Length), false));
            }
            else operations.Add(new NineSliceDrawOperation(cell, destination, destination, true));
        }
        return operations;
    }

    private static void DrawCell(DrawingContext context, NineSliceCell cell, Rect destination,
        IBrush? currentColor, bool preserveAspectRatio)
    {
        if (cell.ViewBox.Width <= 0 || cell.ViewBox.Height <= 0) return;
        var sx = destination.Width / cell.ViewBox.Width;
        var sy = destination.Height / cell.ViewBox.Height;
        var alignment = cell.AspectRatio;
        if (preserveAspectRatio && !alignment.None)
        {
            var scale = alignment.Slice ? Math.Max(sx, sy) : Math.Min(sx, sy);
            sx = sy = scale;
        }
        var dx = destination.X + (destination.Width - cell.ViewBox.Width * sx) * alignment.X;
        var dy = destination.Y + (destination.Height - cell.ViewBox.Height * sy) * alignment.Y;
        var map = Matrix.CreateTranslation(-cell.ViewBox.X, -cell.ViewBox.Y) * Matrix.CreateScale(sx, sy) *
                  Matrix.CreateTranslation(dx, dy);
        InlineSvgDocument.DrawShapes(context, cell.Shapes, map, currentColor);
    }

    internal static IReadOnlyList<TileSegment> TileSegments(double start, double available,
        double natural, string mode)
    {
        if (available <= 0) return [];
        if (mode == "stretch" || natural <= 0) return [new TileSegment(start, available)];
        if (mode == "round")
        {
            var count = Math.Max(1, (int)Math.Round(available / natural));
            var length = available / count;
            return Enumerable.Range(0, count).Select(index => new TileSegment(start + index * length, length)).ToArray();
        }
        if (mode == "space")
        {
            var count = Math.Max(1, (int)Math.Floor(available / natural));
            if (count == 1) return [new TileSegment(start + (available - natural) / 2, natural)];
            var gap = (available - count * natural) / (count - 1);
            return Enumerable.Range(0, count).Select(index =>
                new TileSegment(start + index * (natural + gap), natural)).ToArray();
        }
        var centered = start + (available - natural) / 2;
        var first = centered - Math.Ceiling((centered - start) / natural) * natural;
        var repeatCount = Math.Max(1, (int)Math.Ceiling((start + available - first) / natural));
        return Enumerable.Range(0, repeatCount).Select(index =>
            new TileSegment(first + index * natural, natural)).ToArray();
    }

    internal static double[] Tracks(IReadOnlyList<double?> source, double total, double factor)
    {
        var fixedTotal = source.Where(value => value.HasValue).Sum(value => value!.Value * factor);
        var flexible = Math.Max(0, total - fixedTotal);
        var flexibleCount = source.Count(value => !value.HasValue);
        return source.Select(value => value.HasValue ? value.Value * factor :
            flexible / Math.Max(1, flexibleCount)).ToArray();
    }

    internal static AspectRatioAlignment ParseAspectRatio(string? source)
    {
        var value = ThemeSvgIrParser.ParseAspectRatio(source);
        return new AspectRatioAlignment(value.None, value.Slice, value.X, value.Y);
    }

    private static SvgPaintShape LoadShape(SvgShapeIr shape)
    {
        var document = InlineSvgDocument.Load(new SvgDocumentIr(new ThemeRect(0, 0, 1, 1), [shape]));
        return document.Shapes[0];
    }

    private static double[] Positions(double start, IReadOnlyList<double> sizes)
    {
        var result = new double[sizes.Count];
        for (var index = 0; index < result.Length; index++)
            result[index] = index == 0 ? start : result[index - 1] + sizes[index - 1];
        return result;
    }

    private static Thickness ToThickness(ThemeThickness value) =>
        new(value.Left, value.Top, value.Right, value.Bottom);
}

public sealed record NineSliceCell(int Column, int Row, double Width, double Height, Rect ViewBox,
    AspectRatioAlignment AspectRatio, string Repeat, IReadOnlyList<SvgPaintShape> Shapes);
internal sealed record NineSliceDrawOperation(NineSliceCell Cell, Rect Clip, Rect Destination,
    bool PreserveAspectRatio);
internal readonly record struct TileSegment(double Start, double Length);
public readonly record struct AspectRatioAlignment(bool None, bool Slice, double X, double Y);
