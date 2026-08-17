using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;

namespace Galapa.Launcher.Views.Controls;

public partial class ThemedScrollbar : UserControl
{
    private ScrollViewer? _subscribedTarget;
    private bool _dragging;
    private double _dragStartY;
    private double _dragStartOffset;

    public static readonly StyledProperty<ScrollViewer?> TargetProperty =
        AvaloniaProperty.Register<ThemedScrollbar, ScrollViewer?>(nameof(Target));

    public ScrollViewer? Target { get => GetValue(TargetProperty); set => SetValue(TargetProperty, value); }

    public ThemedScrollbar()
    {
        InitializeComponent();
        PART_Track.PointerPressed += TrackOnPointerPressed;
        PART_Thumb.PointerPressed += ThumbOnPointerPressed;
        PART_Thumb.PointerMoved += ThumbOnPointerMoved;
        PART_Thumb.PointerReleased += ThumbOnPointerReleased;
        PART_Thumb.PointerCaptureLost += (_, _) => _dragging = false;
        SizeChanged += (_, _) => SyncThumb();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TargetProperty) SubscribeTarget(change.GetNewValue<ScrollViewer?>());
    }

    private void SubscribeTarget(ScrollViewer? target)
    {
        if (_subscribedTarget is not null)
        {
            _subscribedTarget.ScrollChanged -= TargetOnScrollChanged;
            _subscribedTarget.LayoutUpdated -= TargetOnLayoutUpdated;
        }
        _subscribedTarget = target;
        if (target is not null)
        {
            target.ScrollChanged += TargetOnScrollChanged;
            target.LayoutUpdated += TargetOnLayoutUpdated;
        }
        SyncThumb();
    }

    private void TargetOnScrollChanged(object? sender, ScrollChangedEventArgs e) => SyncThumb();
    private void TargetOnLayoutUpdated(object? sender, EventArgs e) => SyncThumb();

    private void SyncThumb()
    {
        var target = Target;
        var trackHeight = Bounds.Height;
        if (target is null || trackHeight <= 0) return;
        var extent = target.Extent.Height;
        var viewport = target.Viewport.Height;
        var ratio = extent <= 0 ? 1 : Math.Clamp(viewport / extent, 0, 1);
        var thumbHeight = Math.Clamp(trackHeight * ratio, trackHeight * .12, trackHeight);
        var maxScroll = Math.Max(0, extent - viewport);
        var maxTravel = Math.Max(0, trackHeight - thumbHeight);
        var top = maxScroll <= 0 ? 0 : Math.Clamp(target.Offset.Y / maxScroll, 0, 1) * maxTravel;
        PART_Thumb.Height = thumbHeight;
        Canvas.SetTop(PART_Thumb, top);
        IsEnabled = maxScroll > .5;
        Opacity = maxScroll > .5 ? 1 : .45;
    }

    private void TrackOnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Target is null || _dragging || e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
            return;
        var trackHeight = Bounds.Height;
        var maxScroll = Math.Max(0, Target.Extent.Height - Target.Viewport.Height);
        if (trackHeight <= 0 || maxScroll <= 0) return;
        var fraction = Math.Clamp(e.GetPosition(this).Y / trackHeight, 0, 1);
        Target.Offset = new Vector(Target.Offset.X, fraction * maxScroll);
        e.Handled = true;
    }

    private void ThumbOnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (Target is null || e.GetCurrentPoint(this).Properties.PointerUpdateKind != PointerUpdateKind.LeftButtonPressed)
            return;
        _dragging = true;
        _dragStartY = e.GetPosition(this).Y;
        _dragStartOffset = Target.Offset.Y;
        e.Pointer.Capture(PART_Thumb);
        e.Handled = true;
    }

    private void ThumbOnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging || Target is null) return;
        var maxScroll = Math.Max(0, Target.Extent.Height - Target.Viewport.Height);
        var travel = Math.Max(1, Bounds.Height - PART_Thumb.Bounds.Height);
        Target.Offset = new Vector(Target.Offset.X,
            Math.Clamp(_dragStartOffset + (e.GetPosition(this).Y - _dragStartY) * maxScroll / travel, 0, maxScroll));
        e.Handled = true;
    }

    private void ThumbOnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
    }
}
