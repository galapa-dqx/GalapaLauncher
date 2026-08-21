using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Interactivity;
using Avalonia.Input;
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

    [Fact]
    public async Task SvgDefaultsToNonzeroAndHonorsExplicitEvenOddFillRules()
    {
        const string path = "M2 2H18V18H2Z M6 6H14V14H6Z";
        var nonzero = $"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 20 20'><path d='{path}'/></svg>";
        var evenOdd = $"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 20 20'><path fill-rule='evenodd' d='{path}'/></svg>";

        using var filled = SKBitmap.Decode(await skia.RenderInlineSvgAsync(nonzero));
        using var hollow = SKBitmap.Decode(await skia.RenderInlineSvgAsync(evenOdd));

        Assert.Equal(255, filled.GetPixel(10, 10).Alpha);
        Assert.Equal(0, hollow.GetPixel(10, 10).Alpha);
    }

    [Fact]
    public async Task LeafAndPaintOpacityArePreservedByMaterialization()
    {
        const string svg = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 20 20' fill-opacity='.5'>" +
                           "<rect width='20' height='20' opacity='.5' fill='#ff0000'/></svg>";

        using var bitmap = SKBitmap.Decode(await skia.RenderInlineSvgAsync(svg));

        Assert.InRange(bitmap.GetPixel(10, 10).Alpha, (byte)62, (byte)65);
    }

    [Fact]
    public void StrokeContractIsRetainedInNeutralIr()
    {
        const string svg = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 20 20'>" +
                           "<path d='M2 10H18' fill='none' stroke='#000' stroke-width='2' " +
                           "stroke-linecap='round' stroke-linejoin='bevel' stroke-miterlimit='7' " +
                           "stroke-dasharray='4 2' stroke-dashoffset='1'/></svg>";

        var shape = Assert.Single(new ThemeSvgIrParser().ParseDocument(svg).Shapes);

        Assert.Equal(SvgLineCapIr.Round, shape.StrokeLineCap);
        Assert.Equal(SvgLineJoinIr.Bevel, shape.StrokeLineJoin);
        Assert.Equal(7, shape.StrokeMiterLimit);
        Assert.Equal([4d, 2d], shape.StrokeDashArray);
        Assert.Equal(1, shape.StrokeDashOffset);
    }

    [Fact]
    public async Task StrokeCapsJoinsAndDashesAffectMaterializedRendering()
    {
        const string prefix = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 20 20'>";
        var butt = await skia.RenderInlineSvgAsync(prefix +
            "<path d='M5 10 L15 10' fill='none' stroke='#fff' stroke-width='4' stroke-linecap='butt'/></svg>");
        var round = await skia.RenderInlineSvgAsync(prefix +
            "<path d='M5 10 L15 10' fill='none' stroke='#fff' stroke-width='4' stroke-linecap='round'/></svg>");
        var solid = await skia.RenderInlineSvgAsync(prefix +
            "<path d='M2 10 L18 10' fill='none' stroke='#fff' stroke-width='2'/></svg>");
        var dashed = await skia.RenderInlineSvgAsync(prefix +
            "<path d='M2 10 L18 10' fill='none' stroke='#fff' stroke-width='2' stroke-dasharray='2 2'/></svg>");
        var bevel = await skia.RenderInlineSvgAsync(prefix +
            "<path d='M3 18 L10 2 L17 18' fill='none' stroke='#fff' stroke-width='4' stroke-linejoin='bevel'/></svg>");
        var miter = await skia.RenderInlineSvgAsync(prefix +
            "<path d='M3 18 L10 2 L17 18' fill='none' stroke='#fff' stroke-width='4' stroke-linejoin='miter' stroke-miterlimit='8'/></svg>");

        using var buttBitmap = BitmapTestSupport.Decode(butt, "butt-cap SVG render");
        using var roundBitmap = BitmapTestSupport.Decode(round, "round-cap SVG render");
        using var solidBitmap = BitmapTestSupport.Decode(solid, "solid-stroke SVG render");
        using var dashedBitmap = BitmapTestSupport.Decode(dashed, "dashed-stroke SVG render");
        using var bevelBitmap = BitmapTestSupport.Decode(bevel, "bevel-join SVG render");
        using var miterBitmap = BitmapTestSupport.Decode(miter, "miter-join SVG render");

        Assert.True(PaintedPixels(roundBitmap) > PaintedPixels(buttBitmap));
        Assert.True(PaintedPixels(solidBitmap) > PaintedPixels(dashedBitmap));
        Assert.NotEqual(PaintedPixels(bevelBitmap), PaintedPixels(miterBitmap));
    }

    [Theory]
    [InlineData("style='fill:red'")]
    [InlineData("filter='blur(1px)'")]
    [InlineData("mask='none'")]
    [InlineData("clip-path='none'")]
    [InlineData("color='red'")]
    public void UnsupportedPaintBehaviorIsRejected(string attribute)
    {
        var svg = $"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 10 10'><rect {attribute} width='10' height='10'/></svg>";
        Assert.Throws<InvalidDataException>(() => new ThemeSvgIrParser().ParseDocument(svg));
    }

    [Fact]
    public void GroupOpacityIsRejectedBecauseItRequiresCompositing()
    {
        const string svg = "<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 10 10'><g opacity='.5'><rect width='10' height='10'/></g></svg>";
        Assert.Throws<InvalidDataException>(() => new ThemeSvgIrParser().ParseDocument(svg));
    }

    private static int PaintedPixels(SKBitmap bitmap) =>
        Enumerable.Range(0, bitmap.Width * bitmap.Height)
            .Count(index => bitmap.GetPixel(index % bitmap.Width, index / bitmap.Width).Alpha > 0);
}

internal static class InlineRenderingScenarios
{
    public static Task<byte[]> RenderInlineSvgAsync(this SkiaHeadlessFixture skia, string svg) =>
        skia.DispatchAsync(() => SkiaHeadlessFixture.Render(new ThemedSvg
        {
            InlineSvg = svg,
            Width = 20,
            Height = 20
        }, new PixelSize(20, 20)));

    public static Task<byte[]> RenderSwitchThumbAsync(this SkiaHeadlessFixture skia, double position) =>
        skia.DispatchAsync(() => SkiaHeadlessFixture.Render(new SwitchThumbPart
        {
            PartStyle = ThemePartPresentation.Create(new CompiledControl
            {
                Shape = "Path",
                Fill = "#22AA78",
                Radius = System.Text.Json.JsonSerializer.SerializeToElement("pill")
            }),
            Position = position,
            VisualWidth = 13,
            VisualHeight = 13,
            Width = 30,
            Height = 13
        }, new PixelSize(30, 13)));
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

[Collection(SkiaRenderingCollection.Name)]
public sealed class NineSliceRenderingTests(SkiaHeadlessFixture skia)
{
    private readonly PathTestRenderer _pathRenderer = new(skia);
    private static readonly string SourceFixtureRoot = TestPaths.Fixtures("NineSlice");

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
        var expectedPath = Path.Combine(SourceFixtureRoot, "Expected", expectedName);
        await GoldenImage.AssertMatchesOrUpdate(expectedPath, actual, name, "NineSlice");
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
        var png = await _pathRenderer.RenderThemePartAsync(new CompiledControl { Shape = "Asset", Art = svg }, 20, 20);
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
    public async Task AlternatingBoundsReuseIndependentCachedLayouts()
    {
        var source = await File.ReadAllTextAsync(Path.Combine(SourceFixtureRoot, "diagnostic.9.svg"));
        await skia.DispatchAsync(() =>
        {
            var slices = NineSliceSvg.Parse(source);
            var first = slices.LayoutForTesting(new Rect(0, 0, 20, 20));
            var second = slices.LayoutForTesting(new Rect(0, 0, 49, 37));

            Assert.Same(first, slices.LayoutForTesting(new Rect(0, 0, 20, 20)));
            Assert.Same(second, slices.LayoutForTesting(new Rect(0, 0, 49, 37)));
            Assert.Equal(2, slices.LayoutBuildCount);
            return true;
        });
    }

    [Fact]
    public async Task BoundsLayoutCacheIsBoundedToEightEntries()
    {
        var source = await File.ReadAllTextAsync(Path.Combine(SourceFixtureRoot, "diagnostic.9.svg"));
        await skia.DispatchAsync(() =>
        {
            var slices = NineSliceSvg.Parse(source);
            for (var index = 0; index < 9; index++)
                _ = slices.LayoutForTesting(new Rect(0, 0, 20 + index, 20 + index));
            _ = slices.LayoutForTesting(new Rect(0, 0, 20, 20));

            Assert.Equal(10, slices.LayoutBuildCount);
            return true;
        });
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
