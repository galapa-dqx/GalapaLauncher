using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Galapa.Launcher.Views.Controls;

namespace Galapa.Launcher.Tests.Theming;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SkiaRenderingCollection : ICollectionFixture<SkiaHeadlessFixture>
{
    public const string Name = "CPU Skia rendering";
}

public sealed class SkiaHeadlessFixture : IDisposable
{
    internal const int FixtureOutset = 4;
    internal const string FixtureBackground = "#101018";
    private readonly HeadlessUnitTestSession _session =
        HeadlessUnitTestSession.StartNew(typeof(SkiaHeadlessTestApplication));

    public Task<byte[]> RenderAsync(string svg, int hostWidth, int hostHeight) =>
        _session.Dispatch(() => Render(svg, hostWidth, hostHeight), CancellationToken.None);

    public Task<T> DispatchAsync<T>(Func<Task<T>> action) =>
        _session.Dispatch(action, CancellationToken.None);

    public Task<T> DispatchAsync<T>(Func<T> action) =>
        _session.Dispatch(action, CancellationToken.None);

    public void Dispose() => _session.Dispose();

    private static byte[] Render(string svg, int hostWidth, int hostHeight)
    {
        const int outset = 2;
        var pixelSize = new PixelSize(hostWidth + outset * 2, hostHeight + outset * 2);
        var visual = new NineSliceFixtureVisual(svg, new Rect(outset, outset, hostWidth, hostHeight))
        {
            Width = pixelSize.Width,
            Height = pixelSize.Height
        };
        return Render(visual, pixelSize);
    }

    internal static Application EnsureThemeStyles()
    {
        var app = Application.Current ?? throw new InvalidOperationException("The test application is not running.");
        if (!app.Styles.OfType<FluentTheme>().Any()) app.Styles.Add(new FluentTheme());
        if (!app.Styles.OfType<StyleInclude>().Any(x => x.Source?.AbsoluteUri.Contains("Galapa.UI/Themes/Launcher") == true))
            app.Styles.Add(new StyleInclude(new Uri("avares://Galapa.Launcher.Tests/"))
            {
                Source = new Uri("avares://Galapa.UI/Themes/Launcher.axaml")
            });
        if (!app.Styles.OfType<StyleInclude>().Any(x => x.Source?.AbsoluteUri.Contains("CompiledThemeStyles") == true))
            app.Styles.Add(new StyleInclude(new Uri("avares://Galapa.Launcher.Tests/"))
            {
                Source = new Uri("avares://Galapa.Launcher/Styles/CompiledThemeStyles.axaml")
            });
        return app;
    }

    internal static byte[] Render(Control visual, PixelSize pixelSize)
    {
        visual.Measure(pixelSize.ToSize(1));
        visual.Arrange(new Rect(pixelSize.ToSize(1)));

        return Capture(visual, pixelSize);
    }

    internal static T WithWindow<T>(Control content, Size size, Func<Window, T> inspect)
    {
        var window = new Window
        {
            Width = size.Width,
            Height = size.Height,
            SystemDecorations = SystemDecorations.None,
            Content = content
        };
        window.Show();
        try
        {
            window.UpdateLayout();
            return inspect(window);
        }
        finally { window.Close(); }
    }

    internal static byte[] Capture(Control visual, PixelSize pixelSize)
    {

        using var bitmap = new RenderTargetBitmap(pixelSize, new Vector(96, 96));
        bitmap.Render(visual);
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        return stream.ToArray();
    }

    private sealed class NineSliceFixtureVisual : Control
    {
        private static readonly IBrush Background = new SolidColorBrush(Color.Parse("#101018"));
        private readonly NineSliceSvg _slices;
        private readonly Rect _hostBounds;

        public NineSliceFixtureVisual(string svg, Rect hostBounds)
        {
            _slices = NineSliceSvg.Parse(svg);
            _hostBounds = hostBounds;
        }

        public override void Render(DrawingContext context)
        {
            context.FillRectangle(Background, new Rect(Bounds.Size));
            _slices.Draw(context, _hostBounds, null);
        }
    }
}

public static class SkiaHeadlessTestApplication
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Application>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
