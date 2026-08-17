using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using Galapa.Launcher.Theming;

namespace Galapa.Launcher.Views.Controls;

/// <summary>
/// Avalonia renderer for one compiled theme control. It paints outside the
/// content layout, so state borders can change thickness without moving text.
/// </summary>
public sealed class ThemePart : ContentControl
{
    private NineSliceSvg? _slices;

    public static readonly StyledProperty<CompiledControl?> PartStyleProperty =
        AvaloniaProperty.Register<ThemePart, CompiledControl?>(nameof(PartStyle));
    public static readonly StyledProperty<ThemePartState> StateProperty =
        AvaloniaProperty.Register<ThemePart, ThemePartState>(nameof(State));
    public static readonly StyledProperty<Thickness> FallbackPaddingProperty =
        AvaloniaProperty.Register<ThemePart, Thickness>(nameof(FallbackPadding));
    public static readonly StyledProperty<bool> UseThemePaddingProperty =
        AvaloniaProperty.Register<ThemePart, bool>(nameof(UseThemePadding), true);
    public static readonly StyledProperty<double> AnimatedOffsetProperty =
        AvaloniaProperty.Register<ThemePart, double>(nameof(AnimatedOffset));
    public static readonly StyledProperty<double> VisualWidthProperty =
        AvaloniaProperty.Register<ThemePart, double>(nameof(VisualWidth), double.NaN);
    public static readonly StyledProperty<double> VisualHeightProperty =
        AvaloniaProperty.Register<ThemePart, double>(nameof(VisualHeight), double.NaN);
    public static readonly StyledProperty<double> TopBorderGapStartProperty =
        AvaloniaProperty.Register<ThemePart, double>(nameof(TopBorderGapStart), double.NaN);
    public static readonly StyledProperty<double> TopBorderGapWidthProperty =
        AvaloniaProperty.Register<ThemePart, double>(nameof(TopBorderGapWidth));

    public CompiledControl? PartStyle { get => GetValue(PartStyleProperty); set => SetValue(PartStyleProperty, value); }
    public ThemePartState State { get => GetValue(StateProperty); set => SetValue(StateProperty, value); }
    public Thickness FallbackPadding { get => GetValue(FallbackPaddingProperty); set => SetValue(FallbackPaddingProperty, value); }
    public bool UseThemePadding { get => GetValue(UseThemePaddingProperty); set => SetValue(UseThemePaddingProperty, value); }
    public double AnimatedOffset { get => GetValue(AnimatedOffsetProperty); set => SetValue(AnimatedOffsetProperty, value); }
    public double VisualWidth { get => GetValue(VisualWidthProperty); set => SetValue(VisualWidthProperty, value); }
    public double VisualHeight { get => GetValue(VisualHeightProperty); set => SetValue(VisualHeightProperty, value); }
    public double TopBorderGapStart { get => GetValue(TopBorderGapStartProperty); set => SetValue(TopBorderGapStartProperty, value); }
    public double TopBorderGapWidth { get => GetValue(TopBorderGapWidthProperty); set => SetValue(TopBorderGapWidthProperty, value); }

    static ThemePart()
    {
        AffectsRender<ThemePart>(PartStyleProperty, StateProperty, AnimatedOffsetProperty, VisualWidthProperty,
            VisualHeightProperty, TopBorderGapStartProperty, TopBorderGapWidthProperty);
        AffectsMeasure<ThemePart>(PartStyleProperty, FallbackPaddingProperty, UseThemePaddingProperty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PartStyleProperty || change.Property == StateProperty ||
            change.Property == FallbackPaddingProperty || change.Property == UseThemePaddingProperty)
            ApplyStyle();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var style = PartStyle;
        if (style is null || Bounds.Width <= 0 || Bounds.Height <= 0) return;
        var visualWidth = double.IsNaN(VisualWidth) ? Bounds.Width : Math.Clamp(VisualWidth, 0, Bounds.Width);
        var visualHeight = double.IsNaN(VisualHeight) ? Bounds.Height : Math.Clamp(VisualHeight, 0, Bounds.Height);
        var drawRect = new Rect(
            Math.Clamp(AnimatedOffset, 0, Math.Max(0, Bounds.Width - visualWidth)),
            (Bounds.Height - visualHeight) / 2,
            visualWidth,
            visualHeight);
        var active = ActiveState(style);
        var contentBrush = ThemePaint.Brush(active?.Content ?? style.Content, Foreground);
        if (style.Shape == "Asset")
        {
            _slices?.Draw(context, drawRect, contentBrush);
            return;
        }
        if (style.Shape != "Path") return;

        var fill = ThemePaint.Brush(active?.Fill ?? style.Fill);
        var border = ThemePaint.Brush(active?.BorderColor ?? style.BorderColor);
        var stateHasBorder = active is not null &&
                             active.BorderThickness.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);
        var edges = ThemeMetrics.ReadEdges(stateHasBorder ? active!.BorderThickness : style.BorderThickness);
        var radius = ThemeMetrics.ReadRadius(style.Radius, drawRect.Width, drawRect.Height);
        var geometry = CreateGeometry(drawRect, radius, style.Corner ?? "round");
        context.DrawGeometry(fill, null, geometry);
        DrawBorder(context, geometry, border, edges, TopBorderGapStart, TopBorderGapWidth);
    }

    private void ApplyStyle()
    {
        var style = PartStyle;
        _slices = null;
        if (style is null) return;
        var active = ActiveState(style);
        try
        {
            var art = active?.Art ?? style.Art;
            if (style.Shape == "Asset" && art is not null) _slices = NineSliceSvg.Parse(art);
        }
        catch
        {
            // A bad optional visual never takes the launcher down. Built-ins
            // are validated before this point; this is future import defense.
        }

        Thickness padding;
        if (!UseThemePadding)
            padding = FallbackPadding;
        else if (_slices is not null)
            padding = _slices.ContentPadding;
        else if (style.Padding.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            var edges = ThemeMetrics.ReadEdges(style.Padding);
            padding = new Thickness(edges[3], edges[0], edges[1], edges[2]);
        }
        else padding = FallbackPadding;
        SetCurrentValue(PaddingProperty, padding);

        var text = style.Text;
        if (text?.Family is not null) SetCurrentValue(FontFamilyProperty, ThemeManager.FontFamilyFor(text.Family));
        if (text?.Size is { } size) SetCurrentValue(FontSizeProperty, size);
        if (text?.Weight is { } weight) SetCurrentValue(FontWeightProperty, (FontWeight)weight);
        if (text?.Style is not null) SetCurrentValue(FontStyleProperty, text.Style == "italic" ? FontStyle.Italic : text.Style == "oblique" ? FontStyle.Oblique : FontStyle.Normal);
        var content = ThemePaint.Brush(active?.Content ?? style.Content, Foreground);
        if (content is not null) SetCurrentValue(ForegroundProperty, content);
        SetCurrentValue(OpacityProperty, active?.Opacity ?? style.Opacity ?? 1);
        InvalidateMeasure();
        InvalidateVisual();
    }

    private CompiledControl? ActiveState(CompiledControl style)
    {
        var key = State switch
        {
            ThemePartState.Hover => "hover",
            ThemePartState.Pressed => "pressed",
            ThemePartState.Focused => "focused",
            ThemePartState.Disabled => "disabled",
            ThemePartState.Selected => "selected",
            ThemePartState.Checked => "checked",
            _ => null
        };
        return key is null ? null : style.States?.GetValueOrDefault(key);
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

    private static void DrawBorder(DrawingContext context, Geometry geometry, IBrush? brush,
        IReadOnlyList<double> edges, double gapStart, double gapWidth)
    {
        if (brush is null || edges.All(x => x <= 0)) return;
        var bounds = geometry.Bounds;
        var hasTopGap = edges[0] > 0 && double.IsFinite(gapStart) && gapWidth > 0 &&
                        gapStart < bounds.Width && gapStart + gapWidth > 0;
        var gapLeft = bounds.Left + Math.Clamp(gapStart, 0, bounds.Width);
        var gapRight = bounds.Left + Math.Clamp(gapStart + gapWidth, 0, bounds.Width);
        if (edges.All(x => Math.Abs(x - edges[0]) < .001))
        {
            var pen = new Pen(brush, edges[0]);
            if (!hasTopGap)
            {
                context.DrawGeometry(null, pen, geometry);
                return;
            }

            // Clip the stroke to three non-overlapping regions rather than
            // painting it and hiding the label span afterward. The left and
            // right regions retain their complete corners/sides; the middle
            // region begins below the top stroke and retains the bottom edge.
            var clip = new GeometryGroup { FillRule = FillRule.NonZero };
            if (gapLeft > bounds.Left)
                clip.Children.Add(new RectangleGeometry(
                    new Rect(bounds.Left, bounds.Top, gapLeft - bounds.Left, bounds.Height)));
            if (gapRight < bounds.Right)
                clip.Children.Add(new RectangleGeometry(
                    new Rect(gapRight, bounds.Top, bounds.Right - gapRight, bounds.Height)));
            if (gapRight > gapLeft && bounds.Height > edges[0])
                clip.Children.Add(new RectangleGeometry(
                    new Rect(gapLeft, bounds.Top + edges[0], gapRight - gapLeft, bounds.Height - edges[0])));
            using (context.PushGeometryClip(clip))
                context.DrawGeometry(null, pen, geometry);
            return;
        }
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
