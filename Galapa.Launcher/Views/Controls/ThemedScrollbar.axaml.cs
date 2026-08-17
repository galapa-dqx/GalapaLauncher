using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace Galapa.Launcher.Views.Controls;

public sealed class ThemedScrollbar : ScrollBar
{
    private ScrollViewer? _subscribedTarget;
    private bool _synchronizing;

    public static readonly StyledProperty<ScrollViewer?> TargetProperty =
        AvaloniaProperty.Register<ThemedScrollbar, ScrollViewer?>(nameof(Target));

    public ScrollViewer? Target { get => GetValue(TargetProperty); set => SetValue(TargetProperty, value); }

    public ThemedScrollbar()
    {
        Orientation = Avalonia.Layout.Orientation.Vertical;
        SmallChange = 16;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TargetProperty && this.GetVisualRoot() is not null)
            SubscribeTarget(change.GetNewValue<ScrollViewer?>());
        else if (change.Property == ValueProperty && !_synchronizing)
            ScrollTargetTo(Value);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        SubscribeTarget(Target);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        SubscribeTarget(null);
        base.OnDetachedFromVisualTree(e);
    }

    private void SubscribeTarget(ScrollViewer? target)
    {
        if (_subscribedTarget is not null)
            _subscribedTarget.ScrollChanged -= TargetOnScrollChanged;

        _subscribedTarget = target;
        if (target is not null)
            target.ScrollChanged += TargetOnScrollChanged;

        SynchronizeFromTarget();
    }

    private void TargetOnScrollChanged(object? sender, ScrollChangedEventArgs e) => SynchronizeFromTarget();

    private void SynchronizeFromTarget()
    {
        var target = _subscribedTarget;
        if (target is null)
        {
            Maximum = ViewportSize = LargeChange = Value = 0;
            IsEnabled = false;
            return;
        }

        var maximum = Math.Max(0, target.Extent.Height - target.Viewport.Height);
        _synchronizing = true;
        try
        {
            Maximum = maximum;
            ViewportSize = target.Viewport.Height;
            LargeChange = Math.Max(1, target.Viewport.Height);
            Value = Math.Clamp(target.Offset.Y, 0, maximum);
            IsEnabled = maximum > .5;
        }
        finally
        {
            _synchronizing = false;
        }
    }

    private void ScrollTargetTo(double value)
    {
        var target = _subscribedTarget;
        if (target is null)
            return;

        target.Offset = new Vector(target.Offset.X, Math.Clamp(value, 0, Maximum));
    }
}
