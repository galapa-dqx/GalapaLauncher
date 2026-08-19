using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Galapa.Launcher.Theming;

namespace Galapa.Launcher.Views.Controls;

/// <summary>
/// Avalonia renderer for one compiled theme control. It paints outside the
/// content layout, so state borders can change thickness without moving text.
/// </summary>
public class ThemePart : ContentControl
{
    private Geometry? _cachedGeometry;
    private Size _cachedGeometrySize;
    private double _cachedGeometryRadius = double.NaN;
    private string? _cachedGeometryCorner;

    public static readonly StyledProperty<ThemePartPresentation?> PartStyleProperty =
        AvaloniaProperty.Register<ThemePart, ThemePartPresentation?>(nameof(PartStyle));
    public static readonly StyledProperty<ThemePartState> StateProperty =
        AvaloniaProperty.Register<ThemePart, ThemePartState>(nameof(State));
    public static readonly StyledProperty<Thickness> FallbackPaddingProperty =
        AvaloniaProperty.Register<ThemePart, Thickness>(nameof(FallbackPadding));
    public static readonly StyledProperty<bool> UseThemePaddingProperty =
        AvaloniaProperty.Register<ThemePart, bool>(nameof(UseThemePadding), true);

    public ThemePartPresentation? PartStyle { get => GetValue(PartStyleProperty); set => SetValue(PartStyleProperty, value); }
    public ThemePartState State { get => GetValue(StateProperty); set => SetValue(StateProperty, value); }
    public Thickness FallbackPadding { get => GetValue(FallbackPaddingProperty); set => SetValue(FallbackPaddingProperty, value); }
    public bool UseThemePadding { get => GetValue(UseThemePaddingProperty); set => SetValue(UseThemePaddingProperty, value); }

    static ThemePart()
    {
        ClipToBoundsProperty.OverrideDefaultValue<ThemePart>(false);
        AffectsRender<ThemePart>(PartStyleProperty, StateProperty);
        AffectsMeasure<ThemePart>(PartStyleProperty, FallbackPaddingProperty, UseThemePaddingProperty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PartStyleProperty)
            ApplyStyle();
        else if (change.Property == StateProperty)
            ApplyVisualState();
        else if (change.Property == FallbackPaddingProperty || change.Property == UseThemePaddingProperty)
            ApplyPadding();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var style = PartStyle;
        if (style is null || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var drawRect = GetDrawRect();
        var visual = style.Visual(State);
        var contentBrush = visual.ContentInherited ? Foreground : visual.Content;
        if (style.Control.Shape == "Asset")
        {
            visual.Slices?.Draw(context, drawRect, contentBrush);
            return;
        }
        if (style.Control.Shape != "Path") return;

        var radius = style.Normalized.Radius.Resolve(drawRect.Width, drawRect.Height);
        var corner = style.Control.Corner ?? "round";
        var geometry = GeometryFor(drawRect.Size, radius, corner);
        using (context.PushTransform(Matrix.CreateTranslation(drawRect.X, drawRect.Y)))
        {
            context.DrawGeometry(visual.Fill, null, geometry);
            var gap = GetTopBorderGap();
            DrawBorder(context, geometry, visual, gap.Start, gap.Width);
        }
    }

    protected virtual Rect GetDrawRect() => new(Bounds.Size);
    protected virtual (double Start, double Width) GetTopBorderGap() => (double.NaN, 0);

    private void ApplyStyle()
    {
        var style = PartStyle;
        _cachedGeometry = null;
        if (style is null)
        {
            ClearTypography();
            return;
        }
        // Theme packages are validated before installation. Do not hide a
        // renderer-contract violation here: a silent failure leaves an empty
        // but interactive control and makes transactional application moot.
        ApplyPadding();

        var text = style.Control.Text;
        if (text?.Family is not null) SetCurrentValue(FontFamilyProperty,
            ThemeManager.FontFamilyFor(new ThemeId(style.Control.ThemeId), text.Family));
        else ClearValue(FontFamilyProperty);
        if (text?.Size is { } size) SetCurrentValue(FontSizeProperty, size); else ClearValue(FontSizeProperty);
        if (text?.Weight is { } weight) SetCurrentValue(FontWeightProperty, (FontWeight)weight); else ClearValue(FontWeightProperty);
        if (text?.Style is not null) SetCurrentValue(FontStyleProperty, ThemeTypography.ToFontStyle(text.Style));
        else ClearValue(FontStyleProperty);
        ApplyVisualState();
    }

    private void ApplyPadding()
    {
        var style = PartStyle;
        var visual = style?.Visual(State);
        var padding = !UseThemePadding
            ? FallbackPadding
            : visual?.Slices is not null
                ? visual.Slices.ContentPadding
                : style is not null && ThemeMetrics.HasValue(style.Control.Padding)
                    ? new Thickness(style.Normalized.Padding.Left, style.Normalized.Padding.Top,
                        style.Normalized.Padding.Right, style.Normalized.Padding.Bottom)
                    : FallbackPadding;
        if (Padding != padding) SetCurrentValue(PaddingProperty, padding);
    }

    private void ApplyVisualState()
    {
        var visual = PartStyle?.Visual(State);
        ApplyPadding();
        if (visual?.ContentInherited == false && visual.Content is not null) SetCurrentValue(ForegroundProperty, visual.Content);
        else ClearValue(ForegroundProperty);
        SetCurrentValue(OpacityProperty, visual?.Opacity ?? 1);
        InvalidateVisual();
    }

    private void ClearTypography()
    {
        ClearValue(FontFamilyProperty);
        ClearValue(FontSizeProperty);
        ClearValue(FontWeightProperty);
        ClearValue(FontStyleProperty);
        ClearValue(ForegroundProperty);
        ClearValue(OpacityProperty);
    }

    private Geometry GeometryFor(Size size, double radius, string corner)
    {
        if (_cachedGeometry is not null && _cachedGeometrySize == size &&
            Math.Abs(_cachedGeometryRadius - radius) < .001 && _cachedGeometryCorner == corner)
            return _cachedGeometry;
        _cachedGeometrySize = size;
        _cachedGeometryRadius = radius;
        _cachedGeometryCorner = corner;
        return _cachedGeometry = CreateGeometry(new Rect(size), radius, corner);
    }

    private static Geometry CreateGeometry(Rect rect, double radius, string corner)
    {
        radius = Math.Min(Math.Max(0, radius), Math.Min(rect.Width, rect.Height) / 2);
        if (radius <= 0) return new RectangleGeometry(rect);
        if (corner == "round") return new RectangleGeometry(rect, radius, radius);

        var points = corner switch
        {
            "bevel" => BevelPoints(rect, radius),
            "notch" => NotchPoints(rect, radius),
            "scoop" => ScoopPoints(rect, radius),
            "squircle" => SquirclePoints(rect, radius),
            _ => BevelPoints(rect, radius)
        };
        var geometry = new StreamGeometry();
        using var writer = geometry.Open();
        writer.BeginFigure(points[0], true);
        foreach (var point in points.Skip(1)) writer.LineTo(point);
        writer.EndFigure(true);
        return geometry;
    }

    private static Point[] BevelPoints(Rect r, double d) =>
    [new(r.Left + d, r.Top), new(r.Right - d, r.Top), new(r.Right, r.Top + d), new(r.Right, r.Bottom - d),
        new(r.Right - d, r.Bottom), new(r.Left + d, r.Bottom), new(r.Left, r.Bottom - d), new(r.Left, r.Top + d)];

    private static Point[] NotchPoints(Rect r, double d) =>
    [new(r.Left + d, r.Top), new(r.Right - d, r.Top), new(r.Right - d, r.Top + d), new(r.Right, r.Top + d),
        new(r.Right, r.Bottom - d), new(r.Right - d, r.Bottom - d), new(r.Right - d, r.Bottom), new(r.Left + d, r.Bottom),
        new(r.Left + d, r.Bottom - d), new(r.Left, r.Bottom - d), new(r.Left, r.Top + d), new(r.Left + d, r.Top + d)];

    private static Point[] ScoopPoints(Rect r, double d)
    {
        var points = new List<Point>();
        void Arc(double cx, double cy, double start, double end)
        {
            const int segments = 8;
            for (var i = 0; i <= segments; i++)
            {
                var angle = (start + (end - start) * i / segments) * Math.PI / 180;
                points.Add(new Point(cx + Math.Cos(angle) * d, cy + Math.Sin(angle) * d));
            }
        }

        // Walk clockwise around the perimeter. A scoop is the inverse of a
        // round corner: its quarter-circle is centred on the box corner and
        // bends into the surface instead of around an inset centre point.
        points.Add(new Point(r.Left + d, r.Top));
        points.Add(new Point(r.Right - d, r.Top));
        Arc(r.Right, r.Top, 180, 90);
        points.Add(new Point(r.Right, r.Bottom - d));
        Arc(r.Right, r.Bottom, 270, 180);
        points.Add(new Point(r.Left + d, r.Bottom));
        Arc(r.Left, r.Bottom, 0, -90);
        points.Add(new Point(r.Left, r.Top + d));
        Arc(r.Left, r.Top, 90, 0);
        return points.ToArray();
    }

    private static Point[] SquirclePoints(Rect r, double d)
    {
        var points = new List<Point>();
        void Corner(double cx, double cy, double start, double end)
        {
            const int segments = 12;
            for (var i = 0; i <= segments; i++)
            {
                var t = start + (end - start) * i / segments;
                // n=4 superellipse, the conventional CSS squircle profile.
                var x = Math.Sign(Math.Cos(t)) * Math.Sqrt(Math.Abs(Math.Cos(t))) * d;
                var y = Math.Sign(Math.Sin(t)) * Math.Sqrt(Math.Abs(Math.Sin(t))) * d;
                points.Add(new Point(cx + x, cy + y));
            }
        }

        // Unlike a whole-box superellipse, border-radius only shapes the
        // configured corner square. The straight runs must remain, and a
        // smaller radius must therefore produce a visibly smaller corner.
        points.Add(new Point(r.Left + d, r.Top));
        points.Add(new Point(r.Right - d, r.Top));
        Corner(r.Right - d, r.Top + d, -Math.PI / 2, 0);
        points.Add(new Point(r.Right, r.Bottom - d));
        Corner(r.Right - d, r.Bottom - d, 0, Math.PI / 2);
        points.Add(new Point(r.Left + d, r.Bottom));
        Corner(r.Left + d, r.Bottom - d, Math.PI / 2, Math.PI);
        points.Add(new Point(r.Left, r.Top + d));
        Corner(r.Left + d, r.Top + d, Math.PI, Math.PI * 3 / 2);
        return points.ToArray();
    }

    private void DrawBorder(DrawingContext context, Geometry geometry, ThemePartVisual visual,
        double gapStart, double gapWidth)
    {
        var brush = visual.Border;
        var edges = visual.BorderEdges;
        if (brush is null || !visual.HasBorder) return;
        var bounds = geometry.Bounds;
        var hasTopGap = edges[0] > 0 && double.IsFinite(gapStart) && gapWidth > 0 &&
                        gapStart < bounds.Width && gapStart + gapWidth > 0;
        var gapLeft = bounds.Left + Math.Clamp(gapStart, 0, bounds.Width);
        var gapRight = bounds.Left + Math.Clamp(gapStart + gapWidth, 0, bounds.Width);
        if (visual.UniformBorder)
        {
            var pen = visual.InsideBorderPen!;
            using var outerClip = context.PushGeometryClip(geometry);
            if (!hasTopGap)
            {
                context.DrawGeometry(null, pen, geometry);
                return;
            }

            // Clip the stroke to three non-overlapping regions rather than
            // painting it and hiding the label span afterward. The left and
            // right regions retain their complete corners/sides; the middle
            // region begins below the top stroke and retains the bottom edge.
            var clip = GetTopBorderClip(bounds, edges[0], gapLeft, gapRight);
            using (context.PushGeometryClip(clip))
                context.DrawGeometry(null, pen, geometry);
            return;
        }
        using (context.PushGeometryClip(geometry))
        {
            if (edges[0] > 0)
            {
                if (!hasTopGap)
                    context.FillRectangle(brush, new Rect(bounds.Left, bounds.Top, bounds.Width, edges[0]));
                else
                {
                    if (gapLeft > bounds.Left)
                        context.FillRectangle(brush,
                            new Rect(bounds.Left, bounds.Top, gapLeft - bounds.Left, edges[0]));
                    if (gapRight < bounds.Right)
                        context.FillRectangle(brush,
                            new Rect(gapRight, bounds.Top, bounds.Right - gapRight, edges[0]));
                }
            }
            if (edges[1] > 0) context.FillRectangle(brush, new Rect(bounds.Right - edges[1], bounds.Top, edges[1], bounds.Height));
            if (edges[2] > 0) context.FillRectangle(brush, new Rect(bounds.Left, bounds.Bottom - edges[2], bounds.Width, edges[2]));
            if (edges[3] > 0) context.FillRectangle(brush, new Rect(bounds.Left, bounds.Top, edges[3], bounds.Height));
        }
    }

    protected virtual Geometry GetTopBorderClip(Rect bounds, double topThickness, double gapLeft, double gapRight) =>
        BuildTopBorderClip(bounds, topThickness, gapLeft, gapRight);

    protected static Geometry BuildTopBorderClip(Rect bounds, double topThickness, double gapLeft, double gapRight)
    {
        var clip = new GeometryGroup { FillRule = FillRule.NonZero };
        if (gapLeft > bounds.Left)
            clip.Children.Add(new RectangleGeometry(
                new Rect(bounds.Left, bounds.Top, gapLeft - bounds.Left, bounds.Height)));
        if (gapRight < bounds.Right)
            clip.Children.Add(new RectangleGeometry(
                new Rect(gapRight, bounds.Top, bounds.Right - gapRight, bounds.Height)));
        if (gapRight > gapLeft && bounds.Height > topThickness)
            clip.Children.Add(new RectangleGeometry(
                new Rect(gapLeft, bounds.Top + topThickness, gapRight - gapLeft, bounds.Height - topThickness)));
        return clip;
    }
}

/// <summary>A path surface whose top edge has a real, unpainted label gap.</summary>
public sealed class NotchedThemePart : ThemePart
{
    private Geometry? _cachedClip;
    private Rect _cachedBounds;
    private double _cachedThickness = double.NaN;
    private double _cachedGapLeft = double.NaN;
    private double _cachedGapRight = double.NaN;
    public static readonly StyledProperty<double> TopBorderGapStartProperty =
        AvaloniaProperty.Register<NotchedThemePart, double>(nameof(TopBorderGapStart), double.NaN);
    public static readonly StyledProperty<double> TopBorderGapWidthProperty =
        AvaloniaProperty.Register<NotchedThemePart, double>(nameof(TopBorderGapWidth));

    public double TopBorderGapStart { get => GetValue(TopBorderGapStartProperty); set => SetValue(TopBorderGapStartProperty, value); }
    public double TopBorderGapWidth { get => GetValue(TopBorderGapWidthProperty); set => SetValue(TopBorderGapWidthProperty, value); }

    static NotchedThemePart() =>
        AffectsRender<NotchedThemePart>(TopBorderGapStartProperty, TopBorderGapWidthProperty);

    protected override (double Start, double Width) GetTopBorderGap() =>
        (TopBorderGapStart, TopBorderGapWidth);

    protected override Geometry GetTopBorderClip(Rect bounds, double topThickness, double gapLeft, double gapRight)
    {
        if (_cachedClip is not null && _cachedBounds == bounds &&
            Math.Abs(_cachedThickness - topThickness) < .001 &&
            Math.Abs(_cachedGapLeft - gapLeft) < .001 && Math.Abs(_cachedGapRight - gapRight) < .001)
            return _cachedClip;
        _cachedBounds = bounds;
        _cachedThickness = topThickness;
        _cachedGapLeft = gapLeft;
        _cachedGapRight = gapRight;
        return _cachedClip = BuildTopBorderClip(bounds, topThickness, gapLeft, gapRight);
    }
}

/// <summary>Switch thumb whose travel is derived from its arranged track.</summary>
public sealed class SwitchThumbPart : ThemePart
{
    public static readonly StyledProperty<double> PositionProperty =
        AvaloniaProperty.Register<SwitchThumbPart, double>(nameof(Position));
    public static readonly StyledProperty<double> VisualWidthProperty =
        AvaloniaProperty.Register<SwitchThumbPart, double>(nameof(VisualWidth), double.NaN);
    public static readonly StyledProperty<double> VisualHeightProperty =
        AvaloniaProperty.Register<SwitchThumbPart, double>(nameof(VisualHeight), double.NaN);

    public double Position { get => GetValue(PositionProperty); set => SetValue(PositionProperty, value); }
    public double VisualWidth { get => GetValue(VisualWidthProperty); set => SetValue(VisualWidthProperty, value); }
    public double VisualHeight { get => GetValue(VisualHeightProperty); set => SetValue(VisualHeightProperty, value); }

    static SwitchThumbPart() =>
        AffectsRender<SwitchThumbPart>(PositionProperty, VisualWidthProperty, VisualHeightProperty);

    protected override Rect GetDrawRect()
    {
        var width = double.IsNaN(VisualWidth) ? Bounds.Width : Math.Clamp(VisualWidth, 0, Bounds.Width);
        var height = double.IsNaN(VisualHeight) ? Bounds.Height : Math.Clamp(VisualHeight, 0, Bounds.Height);
        return new Rect(Math.Clamp(Position, 0, 1) * Math.Max(0, Bounds.Width - width),
            (Bounds.Height - height) / 2, width, height);
    }
}

public sealed class ThemePartPresentation
{
    private readonly ThemePartVisual _normal;
    private readonly IReadOnlyDictionary<string, ThemePartVisual> _states;

    private ThemePartPresentation(NormalizedCompiledControl control, IReadOnlyDictionary<string, NineSliceSvg> slices,
        IReadOnlyDictionary<string, InlineSvgDocument> documents)
    {
        Normalized = control;
        Control = control.Source;
        Images = Control.Images?.Where(pair => documents.ContainsKey(pair.Value))
            .ToDictionary(pair => pair.Key, pair => documents[pair.Value], StringComparer.Ordinal)
            ?? new Dictionary<string, InlineSvgDocument>(StringComparer.Ordinal);
        _normal = ThemePartVisual.Create(control.Normal, Control.Shape, slices);
        _states = control.States.ToDictionary(pair => pair.Key,
            pair => ThemePartVisual.Create(pair.Value, Control.Shape, slices), StringComparer.Ordinal);
    }

    public CompiledControl Control { get; }
    public NormalizedCompiledControl Normalized { get; }
    public IReadOnlyDictionary<string, InlineSvgDocument> Images { get; }

    internal static ThemePartPresentation Load(NormalizedCompiledControl control,
        IReadOnlyDictionary<string, NineSliceSvg> slices,
        IReadOnlyDictionary<string, InlineSvgDocument> documents) =>
        new(control, slices, documents);

    /// <summary>Creates an isolated presentation for renderer tests and app-owned controls.</summary>
    public static ThemePartPresentation Create(CompiledControl control)
    {
        var parser = new ThemeSvgIrParser();
        var documents = new Dictionary<string, InlineSvgDocument>(StringComparer.Ordinal);
        var slices = new Dictionary<string, NineSliceSvg>(StringComparer.Ordinal);
        void Assets(CompiledControl value)
        {
            if (value.Art is not null && !slices.ContainsKey(value.Art))
                slices[value.Art] = NineSliceSvg.Load(parser.ParseNineSlice(value.Art));
            if (value.Image is not null && !documents.ContainsKey(value.Image))
                documents[value.Image] = InlineSvgDocument.Load(parser.ParseDocument(value.Image));
            if (value.Images is not null)
                foreach (var image in value.Images.Values)
                    if (!documents.ContainsKey(image))
                        documents[image] = InlineSvgDocument.Load(parser.ParseDocument(image));
        }
        Assets(control);
        if (control.States is not null)
            foreach (var state in control.States.Values) Assets(state);
        return new ThemePartPresentation(CompiledThemeNormalizer.Normalize(control), slices, documents);
    }

    public ThemePartVisual Visual(ThemePartState state)
    {
        var key = CompiledThemeContract.StateName(state);
        return key is not null && _states.TryGetValue(key, out var visual) ? visual : _normal;
    }
}

public sealed class ThemePartVisual
{
    private ThemePartVisual(IBrush? fill, IBrush? content, bool contentInherited, IBrush? border,
        double[] borderEdges, double opacity, NineSliceSvg? slices)
    {
        Fill = fill;
        Content = content;
        ContentInherited = contentInherited;
        Border = border;
        BorderEdges = borderEdges;
        Opacity = opacity;
        Slices = slices;
        HasBorder = border is not null && (borderEdges[0] > 0 || borderEdges[1] > 0 ||
            borderEdges[2] > 0 || borderEdges[3] > 0);
        UniformBorder = HasBorder && Math.Abs(borderEdges[1] - borderEdges[0]) < .001 &&
            Math.Abs(borderEdges[2] - borderEdges[0]) < .001 && Math.Abs(borderEdges[3] - borderEdges[0]) < .001;
        if (UniformBorder)
            InsideBorderPen = new Pen(border, borderEdges[0] * 2);
    }

    public IBrush? Fill { get; }
    public IBrush? Content { get; }
    public bool ContentInherited { get; }
    public IBrush? Border { get; }
    public double[] BorderEdges { get; }
    public bool HasBorder { get; }
    public bool UniformBorder { get; }
    public Pen? InsideBorderPen { get; }
    public double Opacity { get; }
    public NineSliceSvg? Slices { get; }

    public static ThemePartVisual Create(NormalizedControlVisual resolved, string? shape,
        IReadOnlyDictionary<string, NineSliceSvg> slices)
    {
        return new ThemePartVisual(
            ThemePaint.Brush(resolved.Fill),
            ThemePaint.Brush(resolved.Content),
            resolved.ContentInherited,
            ThemePaint.Brush(resolved.BorderColor),
            [resolved.Border.Top, resolved.Border.Right, resolved.Border.Bottom, resolved.Border.Left],
            resolved.Opacity,
            shape == "Asset" && resolved.Art is not null ? slices[resolved.Art] : null);
    }
}
