using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Galapa.Launcher.Views.Controls;

public sealed class NewsGem : Control
{
    private static readonly Geometry Events = Geometry.Parse("M10.1416 3.99121L8.36914 9.44922H2.63086L0.857422 3.99121L5.5 0.617188L10.1416 3.99121Z");
    private static readonly Geometry Updates = Geometry.Parse("M5.29297 0.5L6.5 1.70703V9.29297L5.29297 10.5H1.70703L0.5 9.29297V1.70703L1.70703 0.5H5.29297Z");
    private static readonly Geometry Maintenance = Geometry.Parse("M4 0.680664C5.01231 1.6119 5.84555 2.41723 6.4502 3.3291C7.10652 4.319 7.5 5.4446 7.5 7.00098C7.49994 8.55696 7.10649 9.68137 6.4502 10.6709C5.84559 11.5825 5.01211 12.3871 4 13.3184C2.98789 12.3871 2.15441 11.5825 1.5498 10.6709C0.893514 9.68137 0.500064 8.55696 0.5 7.00098C0.5 5.4446 0.893482 4.319 1.5498 3.3291C2.15445 2.41723 2.98769 1.6119 4 0.680664Z");

    public static readonly StyledProperty<string> CategoryProperty =
        AvaloniaProperty.Register<NewsGem, string>(nameof(Category), "news");
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<NewsGem, IBrush?>(nameof(Fill));
    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<NewsGem, IBrush?>(nameof(Stroke));

    public string Category { get => GetValue(CategoryProperty); set => SetValue(CategoryProperty, value); }
    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    static NewsGem() => AffectsRender<NewsGem>(CategoryProperty, FillProperty, StrokeProperty);

    protected override Size MeasureOverride(Size availableSize) => new(11, 14);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        var pen = Stroke is null ? null : new Pen(Stroke, 1);
        switch (Category)
        {
            case "events":
                using (context.PushTransform(Matrix.CreateTranslation(0, 1.5)))
                    context.DrawGeometry(Fill, pen, Events);
                break;
            case "updates":
                using (context.PushTransform(Matrix.CreateTranslation(2, 1.5)))
                    context.DrawGeometry(Fill, pen, Updates);
                break;
            case "maintenance":
                using (context.PushTransform(Matrix.CreateTranslation(1.5, 0)))
                    context.DrawGeometry(Fill, pen, Maintenance);
                break;
            default:
                context.DrawEllipse(Fill, pen, new Point(5.5, 7), 4, 4);
                break;
        }
    }
}
