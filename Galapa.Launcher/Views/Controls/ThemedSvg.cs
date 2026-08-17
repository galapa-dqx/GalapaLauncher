using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Galapa.Launcher.Views.Controls;

/// <summary>Safe SVG mark renderer for app fallbacks and compiled inline art.</summary>
public sealed class ThemedSvg : Control
{
    private InlineSvgDocument? _document;

    public static readonly StyledProperty<string?> InlineSvgProperty =
        AvaloniaProperty.Register<ThemedSvg, string?>(nameof(InlineSvg));
    public static readonly StyledProperty<IBrush?> CurrentColorProperty =
        AvaloniaProperty.Register<ThemedSvg, IBrush?>(nameof(CurrentColor));
    public static readonly StyledProperty<string?> FallbackPathDataProperty =
        AvaloniaProperty.Register<ThemedSvg, string?>(nameof(FallbackPathData));
    public static readonly StyledProperty<string?> FallbackInlineSvgProperty =
        AvaloniaProperty.Register<ThemedSvg, string?>(nameof(FallbackInlineSvg));

    public string? InlineSvg { get => GetValue(InlineSvgProperty); set => SetValue(InlineSvgProperty, value); }
    public IBrush? CurrentColor { get => GetValue(CurrentColorProperty); set => SetValue(CurrentColorProperty, value); }
    public string? FallbackPathData { get => GetValue(FallbackPathDataProperty); set => SetValue(FallbackPathDataProperty, value); }
    public string? FallbackInlineSvg { get => GetValue(FallbackInlineSvgProperty); set => SetValue(FallbackInlineSvgProperty, value); }

    static ThemedSvg() => AffectsRender<ThemedSvg>(InlineSvgProperty, CurrentColorProperty, FallbackPathDataProperty, FallbackInlineSvgProperty);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == InlineSvgProperty || change.Property == FallbackPathDataProperty ||
            change.Property == FallbackInlineSvgProperty) Load();
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        _document?.Draw(context, new Rect(Bounds.Size), CurrentColor ?? Brushes.Black);
    }

    private void Load()
    {
        _document = null;
        if (TryLoad(InlineSvg) || TryLoad(FallbackInlineSvg))
        {
            InvalidateVisual();
            return;
        }
        if (FallbackPathData is { Length: > 0 } data)
            TryLoad($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 24 24\"><path d=\"{data}\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"1.5\"/></svg>");
        InvalidateVisual();
    }

    private bool TryLoad(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return false;
        try
        {
            _document = ThemeSvgCache.Document(source);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
