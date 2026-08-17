using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using Galapa.Launcher.Theming;
using Galapa.Launcher.Views.Controls;
using SkiaSharp;
using Xunit.Sdk;

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

    public Task<byte[]> RenderPathAsync(CompiledControl style, int hostWidth, int hostHeight,
        double topBorderGapStart = double.NaN, double topBorderGapWidth = 0) =>
        _session.Dispatch(
            () => RenderPath(style, hostWidth, hostHeight, topBorderGapStart, topBorderGapWidth),
            CancellationToken.None);

    public Task<TabRenderInspection> RenderSelectedTabAsync() =>
        _session.Dispatch(RenderSelectedTab, CancellationToken.None);

    public Task<ScrollbarInspection> InspectScrollbarAsync() =>
        _session.Dispatch(InspectScrollbar, CancellationToken.None);

    public Task<T> DispatchAsync<T>(Func<Task<T>> action) =>
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

    private static byte[] RenderPath(CompiledControl style, int hostWidth, int hostHeight,
        double topBorderGapStart, double topBorderGapWidth)
    {
        var pixelSize = new PixelSize(hostWidth + FixtureOutset * 2, hostHeight + FixtureOutset * 2);
        var part = new ThemePart
        {
            PartStyle = style,
            Width = hostWidth,
            Height = hostHeight,
            TopBorderGapStart = topBorderGapStart,
            TopBorderGapWidth = topBorderGapWidth
        };
        Canvas.SetLeft(part, FixtureOutset);
        Canvas.SetTop(part, FixtureOutset);
        var visual = new Canvas
        {
            Width = pixelSize.Width,
            Height = pixelSize.Height,
            Background = new SolidColorBrush(Color.Parse(FixtureBackground)),
            Children = { part }
        };
        return Render(visual, pixelSize);
    }

    private static TabRenderInspection RenderSelectedTab()
    {
        var app = EnsureThemeStyles();

        var selectedState = new CompiledControl { BorderColor = "#22AA78", Content = "#22AA78" };
        var tabStyle = new CompiledControl
        {
            Shape = "Path",
            Content = "#3A7860",
            BorderColor = "transparent",
            BorderThickness = System.Text.Json.JsonSerializer.SerializeToElement(new[] { 0, 0, 2, 0 }),
            Padding = System.Text.Json.JsonSerializer.SerializeToElement(new[] { 0, 10, 0, 10 }),
            States = new Dictionary<string, CompiledControl> { ["selected"] = selectedState }
        };
        app.Resources["Galapa.Part.tab"] = tabStyle;
        app.Resources["Galapa.Part.tab.ContentBrush"] = new SolidColorBrush(Color.Parse("#3A7860"));
        app.Resources["Galapa.Part.tab.selected.ContentBrush"] = new SolidColorBrush(Color.Parse("#22AA78"));
        app.Resources["Galapa.Part.tab.selected.BorderBrush"] = new SolidColorBrush(Color.Parse("#22AA78"));

        var strip = new TabStrip
        {
            Width = 200,
            Height = 34,
            ItemsSource = new[] { "Launcher", "Settings" },
            SelectedIndex = 1
        };
        strip.Classes.Add("top-tabs");
        var root = new Border
        {
            Width = 200,
            Height = 34,
            Background = new SolidColorBrush(Color.Parse(FixtureBackground)),
            Child = strip
        };
        var window = new Window
        {
            Width = 200,
            Height = 34,
            SystemDecorations = SystemDecorations.None,
            CanResize = false,
            Content = root
        };
        window.Show();
        try
        {
            window.UpdateLayout();
            var selectedItem = strip.GetVisualDescendants().OfType<TabStripItem>().Single(x => x.IsSelected);
            var underline = selectedItem.GetVisualDescendants().OfType<Border>().Single(x => x.Name == "PART_TabUnderline");
            var underlineBounds = underline.Bounds;
            var underlineColor = (underline.Background as ISolidColorBrush)?.Color;
            var itemBorderColor = (selectedItem.BorderBrush as ISolidColorBrush)?.Color;
            var underlineIsVisible = underline.IsVisible;
            var underlineIsEffectivelyVisible = underline.IsEffectivelyVisible;
            var underlineOpacity = underline.Opacity;
            var itemBounds = selectedItem.Bounds;
            var png = Capture(window, new PixelSize(
                (int)Math.Ceiling(window.Bounds.Width), (int)Math.Ceiling(window.Bounds.Height)));
            return new TabRenderInspection(png, underlineBounds, underlineColor, itemBorderColor,
                underlineIsVisible, underlineIsEffectivelyVisible, underlineOpacity, itemBounds);
        }
        finally
        {
            window.Close();
        }
    }

    private static ScrollbarInspection InspectScrollbar()
    {
        _ = EnsureThemeStyles();
        var first = new ScrollViewer
        {
            Width = 100,
            Height = 100,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Content = new Border { Width = 100, Height = 400 }
        };
        var second = new ScrollViewer
        {
            Width = 100,
            Height = 100,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Content = new Border { Width = 100, Height = 250 }
        };
        var bar = new ThemedScrollbar { Height = 100, Target = first };
        var host = new Grid { ColumnDefinitions = new ColumnDefinitions("100,100,20"), Children = { first, second, bar } };
        Grid.SetColumn(second, 1);
        Grid.SetColumn(bar, 2);
        var window = new Window
        {
            Width = 220,
            Height = 100,
            SystemDecorations = SystemDecorations.None,
            CanResize = false,
            Content = host
        };
        window.Show();
        try
        {
            window.UpdateLayout();
            var initialMaximum = bar.Maximum;
            var initialViewport = bar.ViewportSize;
            bar.Value = 45;
            window.UpdateLayout();
            var firstOffset = first.Offset.Y;

            first.Offset = new Vector(0, 80);
            window.UpdateLayout();
            var valueAfterTargetScroll = bar.Value;

            bar.Target = second;
            window.UpdateLayout();
            var replacementMaximum = bar.Maximum;
            first.Offset = new Vector(0, 120);
            window.UpdateLayout();
            var valueAfterOldTargetScroll = bar.Value;

            var peer = ControlAutomationPeer.CreatePeerForElement(bar);
            var hasRangeAutomation = peer is IRangeValueProvider;
            window.Close();
            return new ScrollbarInspection(initialMaximum, initialViewport, firstOffset, valueAfterTargetScroll,
                replacementMaximum, valueAfterOldTargetScroll, bar.Maximum, bar.IsEnabled, hasRangeAutomation);
        }
        finally
        {
            if (window.IsVisible) window.Close();
        }
    }

    private static Application EnsureThemeStyles()
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

    private static byte[] Render(Control visual, PixelSize pixelSize)
    {
        visual.Measure(pixelSize.ToSize(1));
        visual.Arrange(new Rect(pixelSize.ToSize(1)));

        return Capture(visual, pixelSize);
    }

    private static byte[] Capture(Control visual, PixelSize pixelSize)
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

public sealed record TabRenderInspection(
    byte[] Png,
    Rect UnderlineBounds,
    Color? UnderlineColor,
    Color? ItemBorderColor,
    bool UnderlineIsVisible,
    bool UnderlineIsEffectivelyVisible,
    double UnderlineOpacity,
    Rect ItemBounds);

public sealed record ScrollbarInspection(
    double InitialMaximum,
    double InitialViewport,
    double FirstOffset,
    double ValueAfterTargetScroll,
    double ReplacementMaximum,
    double ValueAfterOldTargetScroll,
    double DetachedMaximum,
    bool DetachedIsEnabled,
    bool HasRangeAutomation);

public static class SkiaHeadlessTestApplication
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<Application>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

[Collection(SkiaRenderingCollection.Name)]
public sealed class NineSliceRenderingTests(SkiaHeadlessFixture skia)
{
    private const string UpdateVariable = "GALAPA_UPDATE_RENDER_FIXTURES";
    private static readonly string ProjectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
    private static readonly string SourceFixtureRoot = Path.Combine(ProjectRoot, "Fixtures", "NineSlice");

    public static TheoryData<string, string, int, int> GoldenCases => new()
    {
        { "stretch-natural", "stretch", 20, 20 },
        { "stretch-enlarged", "stretch", 49, 37 },
        { "stretch-shrink-both", "stretch", 4, 4 },
        { "stretch-shrink-width", "stretch", 8, 33 },
        { "repeat-enlarged", "repeat", 49, 37 },
        { "repeat-narrow-band", "repeat", 17, 17 },
        { "round-enlarged", "round", 49, 37 },
        { "space-enlarged", "space", 49, 37 },
        { "space-narrow-band", "space", 17, 17 }
    };

    [Theory]
    [MemberData(nameof(GoldenCases))]
    public async Task RenderingMatchesApprovedFixture(string name, string repeat, int width, int height)
    {
        var source = await File.ReadAllTextAsync(Path.Combine(SourceFixtureRoot, "diagnostic.9.svg"));
        var svg = source.Replace("data-slice-repeat=\"stretch\"", $"data-slice-repeat=\"{repeat}\"", StringComparison.Ordinal);
        var actual = await skia.RenderAsync(svg, width, height);

        var expectedName = $"{name}-{width}x{height}.png";
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            var sourceExpected = Path.Combine(SourceFixtureRoot, "Expected");
            Directory.CreateDirectory(sourceExpected);
            await File.WriteAllBytesAsync(Path.Combine(sourceExpected, expectedName), actual);
            return;
        }

        var expectedPath = Path.Combine(SourceFixtureRoot, "Expected", expectedName);
        if (!File.Exists(expectedPath))
            throw new XunitException($"Missing render fixture '{expectedName}'. Run the test with {UpdateVariable}=1 to create it.");

        GoldenImage.ComparePixels(expectedPath, actual, name, "NineSlice");
    }

    [Fact]
    public void ParserFindsTracksInsetsAndOutset()
    {
        var slices = NineSliceSvg.Parse(File.ReadAllText(Path.Combine(SourceFixtureRoot, "diagnostic.9.svg")));

        Assert.Equal(new double?[] { 8, null, 8 }, slices.Columns);
        Assert.Equal(new double?[] { 8, null, 8 }, slices.Rows);
        Assert.Equal(new Thickness(4), slices.ContentPadding);
        Assert.Equal(new Thickness(2), slices.Outset);
        Assert.Equal(9, slices.Cells.Count);
    }

    [Fact]
    public void UnknownRepeatModeFallsBackToStretch()
    {
        var source = File.ReadAllText(Path.Combine(SourceFixtureRoot, "diagnostic.9.svg"));
        var slices = NineSliceSvg.Parse(source.Replace("data-slice-repeat=\"stretch\"",
            "data-slice-repeat=\"surprise\"", StringComparison.Ordinal));

        Assert.All(slices.Cells, cell => Assert.Equal("stretch", cell.Repeat));
    }

    [Theory]
    [InlineData("xMinYMin meet", false, 0, 0)]
    [InlineData("xMidYMid meet", false, .5, .5)]
    [InlineData("xMaxYMax slice", true, 1, 1)]
    [InlineData("defer xMaxYMin meet", false, 1, 0)]
    public void PreserveAspectRatioAlignmentIsParsed(string source, bool slice, double x, double y)
    {
        var alignment = NineSliceSvg.ParseAspectRatio(source);

        Assert.False(alignment.None);
        Assert.Equal(slice, alignment.Slice);
        Assert.Equal(x, alignment.X);
        Assert.Equal(y, alignment.Y);
    }

    [Fact]
    public void InvalidPreserveAspectRatioIsRejected() =>
        Assert.Throws<InvalidDataException>(() => NineSliceSvg.ParseAspectRatio("xMaybeYMid meet"));

    [Fact]
    public void FixedTracksShrinkByOneUniformFactor()
    {
        double?[] tracks = [8, null, 8];
        Assert.Equal(new[] { 4d, 0d, 4d }, NineSliceSvg.Tracks(tracks, 8, .5));
        Assert.Equal(new[] { 6d, 25d, 6d }, NineSliceSvg.Tracks(tracks, 37, .75));
    }

    [Fact]
    public void RepeatTilesAreCenteredAndClipSymmetrically()
    {
        AssertSegments(NineSliceSvg.TileSegments(0, 29, 8, "repeat"),
            (-5.5, 8), (2.5, 8), (10.5, 8), (18.5, 8), (26.5, 8));
        AssertSegments(NineSliceSvg.TileSegments(0, 5, 8, "repeat"),
            (-1.5, 8));
    }

    [Fact]
    public void RoundAndSpaceFollowCssSizingRules()
    {
        AssertSegments(NineSliceSvg.TileSegments(0, 29, 8, "round"),
            (0, 7.25), (7.25, 7.25), (14.5, 7.25), (21.75, 7.25));
        AssertSegments(NineSliceSvg.TileSegments(0, 29, 8, "space"),
            (0, 8), (10.5, 8), (21, 8));
        AssertSegments(NineSliceSvg.TileSegments(0, 5, 8, "space"),
            (-1.5, 8));
    }

    private static void AssertSegments(IReadOnlyList<TileSegment> actual,
        params (double Start, double Length)[] expected)
    {
        Assert.Equal(expected.Length, actual.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Start, actual[i].Start, 6);
            Assert.Equal(expected[i].Length, actual[i].Length, 6);
        }
    }

}

internal static class GoldenImage
{
    internal static readonly string ProjectRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));

    internal static void ComparePixels(string expectedPath, byte[] actualPng, string name, string artifactGroup)
    {
        using var expected = SKBitmap.Decode(expectedPath)
            ?? throw new XunitException($"Could not decode expected image: {expectedPath}");
        using var actual = SKBitmap.Decode(actualPng)
            ?? throw new XunitException($"Could not decode actual image for {name}.");

        var mismatchCount = 0;
        var maximumDelta = 0;
        var diffWidth = Math.Max(expected.Width, actual.Width);
        var diffHeight = Math.Max(expected.Height, actual.Height);
        using var diff = new SKBitmap(diffWidth, diffHeight, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (var y = 0; y < diffHeight; y++)
        for (var x = 0; x < diffWidth; x++)
        {
            if (x >= expected.Width || y >= expected.Height || x >= actual.Width || y >= actual.Height)
            {
                mismatchCount++;
                maximumDelta = 255;
                diff.SetPixel(x, y, new SKColor(255, 0, 255, 255));
                continue;
            }
            var wanted = expected.GetPixel(x, y);
            var rendered = actual.GetPixel(x, y);
            var delta = Math.Max(
                Math.Max(Math.Abs(wanted.Red - rendered.Red), Math.Abs(wanted.Green - rendered.Green)),
                Math.Max(Math.Abs(wanted.Blue - rendered.Blue), Math.Abs(wanted.Alpha - rendered.Alpha)));
            maximumDelta = Math.Max(maximumDelta, delta);
            if (delta == 0)
            {
                diff.SetPixel(x, y, new SKColor((byte)(wanted.Red / 3), (byte)(wanted.Green / 3),
                    (byte)(wanted.Blue / 3), 255));
            }
            else
            {
                mismatchCount++;
                diff.SetPixel(x, y, new SKColor(255, 0, 255, 255));
            }
        }

        if (mismatchCount == 0) return;
        var artifactRoot = ArtifactRoot(artifactGroup);
        var actualPath = Path.Combine(artifactRoot, $"{name}.actual.png");
        var diffPath = Path.Combine(artifactRoot, $"{name}.diff.png");
        File.WriteAllBytes(actualPath, actualPng);
        Save(diff, diffPath);
        var sizeMessage = expected.Width == actual.Width && expected.Height == actual.Height
            ? string.Empty
            : $" Expected {expected.Width}x{expected.Height}, rendered {actual.Width}x{actual.Height}.";
        throw new XunitException(
            $"{name}: {mismatchCount} pixels differ (maximum channel delta {maximumDelta}).{sizeMessage} " +
            $"Actual: {actualPath}; diff: {diffPath}");
    }

    private static string ArtifactRoot(string artifactGroup)
    {
        var path = Path.Combine(ProjectRoot, "TestResults", artifactGroup);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void Save(SKBitmap bitmap, string path)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }
}
