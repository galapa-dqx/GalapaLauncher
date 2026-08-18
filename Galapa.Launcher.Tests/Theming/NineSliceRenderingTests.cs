using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using Galapa.Launcher.Theming;
using Galapa.Launcher.Views.Controls;
using Galapa.Launcher.Views;
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

    public Task<byte[]> RenderThemePartAsync(CompiledControl style, int hostWidth, int hostHeight) =>
        _session.Dispatch(() => RenderThemePart(style, hostWidth, hostHeight), CancellationToken.None);

    public Task<bool> ConstructMainWindowAsync() =>
        _session.Dispatch(() =>
        {
            _ = EnsureThemeStyles();
            var window = new MainWindow(true);
            return window.FindControl<ThemePart>("PART_TitleBar") is not null;
        }, CancellationToken.None);

    public Task<bool> DerivedSettingsShellReceivesSharedTemplateAsync() =>
        _session.Dispatch(() =>
        {
            _ = EnsureThemeStyles();
            var content = new Border { HorizontalAlignment = HorizontalAlignment.Stretch };
            var shell = new SettingsPageShell
            {
                Width = 700,
                Height = 480,
                Content = content,
                HelpContent = new TextBlock { Text = "Help" }
            };
            var window = new Window
            {
                Width = 700,
                Height = 480,
                SystemDecorations = SystemDecorations.None,
                Content = shell
            };
            window.Show();
            try
            {
                window.UpdateLayout();
                return shell.GetVisualDescendants().OfType<ScrollViewer>()
                           .Any(viewer => viewer.Name == "PART_ContentScroll") &&
                       shell.GetVisualDescendants().OfType<ThemedScrollbar>().Any() &&
                       content.Bounds.Width > 300;
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    public Task<bool> SettingListItemsStretchAsync() =>
        _session.Dispatch(() =>
        {
            _ = EnsureThemeStyles();
            var item = new Button { Height = 24, Content = "Item" };
            item.Classes.Add("themed");
            item.Classes.Add("setting-row");
            var items = new ItemsControl
            {
                Width = 350,
                Height = 100,
                ItemsSource = new[] { "Item" },
                ItemTemplate = new FuncDataTemplate<string>((_, _) => item)
            };
            items.Classes.Add("stretch-items");
            var window = new Window
            {
                Width = 350,
                Height = 100,
                SystemDecorations = SystemDecorations.None,
                Content = items
            };
            window.Show();
            try
            {
                window.UpdateLayout();
                return item.Bounds.Width > 300;
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);

    public Task<byte[]> ValidateAndRenderThemeTextAsync(ThemePackage package) =>
        _session.Dispatch(() =>
        {
            ThemeFontRegistrar.Validate(package);
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            var text = package.Compiled.Controls["titlebar.wordmark"].Text!;
            return Render(new TextBlock
            {
                Text = "Galapa",
                FontFamily = ThemeManager.FontFamilyFor(package.Manifest.Id, text.Family!),
                FontStyle = ThemeTypography.ToFontStyle(text.Style),
                FontWeight = (FontWeight)(text.Weight ?? 400),
                FontSize = 24,
                Foreground = Brushes.White,
                Width = 140,
                Height = 44
            }, new PixelSize(140, 44));
        }, CancellationToken.None);

    public Task<byte[]> RenderInlineSvgAsync(string svg) =>
        _session.Dispatch(() => Render(new ThemedSvg
        {
            InlineSvg = svg,
            Width = 20,
            Height = 20
        }, new PixelSize(20, 20)), CancellationToken.None);

    public Task<byte[]> RenderSwitchThumbAsync(double position) =>
        _session.Dispatch(() => Render(new SwitchThumbPart
        {
            PartStyle = new CompiledControl
            {
                Shape = "Path",
                Fill = "#22AA78",
                Radius = System.Text.Json.JsonSerializer.SerializeToElement("pill")
            },
            Position = position,
            VisualWidth = 13,
            VisualHeight = 13,
            Width = 30,
            Height = 13
        }, new PixelSize(30, 13)), CancellationToken.None);

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
        var part = new NotchedThemePart
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

    private static byte[] RenderThemePart(CompiledControl style, int hostWidth, int hostHeight)
    {
        var pixelSize = new PixelSize(hostWidth + FixtureOutset * 2, hostHeight + FixtureOutset * 2);
        var part = new ThemePart { PartStyle = style, Width = hostWidth, Height = hostHeight };
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
        app.Resources["Galapa.Type.navigation.ContentBrush"] = new SolidColorBrush(Color.Parse("#3A7860"));
        app.Resources["Galapa.Type.navigation.Family"] = FontFamily.Default;
        app.Resources["Galapa.Type.navigation.Size"] = 16d;
        app.Resources["Galapa.Type.navigation.Weight"] = FontWeight.SemiBold;
        app.Resources["Galapa.Type.navigation.Style"] = FontStyle.Normal;
        app.Resources["Galapa.Type.navigation.LetterSpacing"] = 0d;
        app.Resources["Galapa.Type.navigation.Transform"] = "Original";

        var strip = new TabStrip
        {
            Width = 200,
            Height = 34,
            ItemsSource = new[] { "Launcher", "Settings" },
            SelectedIndex = 1,
            ItemTemplate = new FuncDataTemplate<string>((value, _) =>
            {
                var label = new Galapa.UI.Controls.ThemedTextBlock { SourceText = value };
                label.Classes.Add("navigation");
                return label;
            })
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
            var labelColor = (selectedItem.GetVisualDescendants()
                .OfType<Galapa.UI.Controls.ThemedTextBlock>().Single().Foreground as ISolidColorBrush)?.Color;
            var png = Capture(window, new PixelSize(
                (int)Math.Ceiling(window.Bounds.Width), (int)Math.Ceiling(window.Bounds.Height)));
            return new TabRenderInspection(png, underlineBounds, underlineColor, itemBorderColor,
                underlineIsVisible, underlineIsEffectivelyVisible, underlineOpacity, itemBounds, labelColor);
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
            var track = bar.GetVisualDescendants().OfType<Track>().Single();
            var directionReversed = track.IsDirectionReversed;
            var pageUp = bar.GetVisualDescendants().OfType<RepeatButton>().Single(x => x.Name == "PART_PageUpButton");
            var pageDown = bar.GetVisualDescendants().OfType<RepeatButton>().Single(x => x.Name == "PART_PageDownButton");
            bar.Value = 150;
            pageUp.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var afterPageUp = bar.Value;
            pageDown.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var afterPageDown = bar.Value;
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
                replacementMaximum, valueAfterOldTargetScroll, bar.Maximum, bar.IsEnabled, hasRangeAutomation,
                directionReversed, afterPageUp, afterPageDown);
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
    Rect ItemBounds,
    Color? LabelColor);

public sealed record ScrollbarInspection(
    double InitialMaximum,
    double InitialViewport,
    double FirstOffset,
    double ValueAfterTargetScroll,
    double ReplacementMaximum,
    double ValueAfterOldTargetScroll,
    double DetachedMaximum,
    bool DetachedIsEnabled,
    bool HasRangeAutomation,
    bool IsDirectionReversed,
    double ValueAfterPageUp,
    double ValueAfterPageDown);

[Collection(SkiaRenderingCollection.Name)]
public sealed class InlineSvgRenderingTests(SkiaHeadlessFixture skia)
{
    [Theory]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 10 10'><rect width='10' height='10'/></svg>", 0, 0, 0)]
    [InlineData("<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 10 10' fill='#c03040'><rect width='10' height='10'/></svg>", 192, 48, 64)]
    public async Task SvgInitialAndRootFillAreInherited(string svg, byte red, byte green, byte blue)
    {
        using var bitmap = SKBitmap.Decode(await skia.RenderInlineSvgAsync(svg));
        var pixel = bitmap.GetPixel(10, 10);
        Assert.Equal(red, pixel.Red);
        Assert.Equal(green, pixel.Green);
        Assert.Equal(blue, pixel.Blue);
        Assert.Equal(255, pixel.Alpha);
    }
}

[Collection(SkiaRenderingCollection.Name)]
public sealed class SwitchRenderingTests(SkiaHeadlessFixture skia)
{
    [Fact]
    public async Task ThumbTravelUsesTheArrangedTrackWidth()
    {
        using var left = SKBitmap.Decode(await skia.RenderSwitchThumbAsync(0));
        using var right = SKBitmap.Decode(await skia.RenderSwitchThumbAsync(1));
        var fill = SKColor.Parse("#22AA78");

        Assert.Equal(fill, left.GetPixel(6, 6));
        Assert.Equal(0, left.GetPixel(23, 6).Alpha);
        Assert.Equal(0, right.GetPixel(6, 6).Alpha);
        Assert.Equal(fill, right.GetPixel(23, 6));
    }
}

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
    public void UnknownRepeatModeIsRejectedByTheSharedStructureParser()
    {
        var source = File.ReadAllText(Path.Combine(SourceFixtureRoot, "diagnostic.9.svg"));
        Assert.Throws<InvalidDataException>(() => NineSliceSvg.Parse(source.Replace("data-slice-repeat=\"stretch\"",
            "data-slice-repeat=\"surprise\"", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ProductionThemePartDoesNotClipNineSliceOutset()
    {
        var svg = await File.ReadAllTextAsync(Path.Combine(SourceFixtureRoot, "diagnostic.9.svg"));
        var png = await skia.RenderThemePartAsync(new CompiledControl { Shape = "Asset", Art = svg }, 20, 20);
        using var bitmap = SKBitmap.Decode(png);
        var background = SKColor.Parse(SkiaHeadlessFixture.FixtureBackground);

        Assert.NotEqual(background, bitmap.GetPixel(SkiaHeadlessFixture.FixtureOutset - 1, 12));
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
