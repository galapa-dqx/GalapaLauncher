using Avalonia;
using Avalonia.Controls;

namespace Galapa.Launcher.Views.Controls;

/// <summary>Keeps artwork at its authored ratio while its column grows.</summary>
public sealed class AspectRatioBox : Decorator
{
    public static readonly StyledProperty<double> RatioProperty =
        AvaloniaProperty.Register<AspectRatioBox, double>(nameof(Ratio), 1d);

    public double Ratio { get => GetValue(RatioProperty); set => SetValue(RatioProperty, value); }

    static AspectRatioBox() => AffectsMeasure<AspectRatioBox>(RatioProperty);

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is null) return default;
        var ratio = double.IsFinite(Ratio) && Ratio > 0 ? Ratio : 1d;
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 1d;
        var height = width / ratio;
        if (double.IsFinite(availableSize.Height)) height = Math.Min(height, availableSize.Height);
        Child.Measure(new Size(width, height));
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(finalSize));
        return finalSize;
    }
}
