using System.Text.Json;
using Galapa.Launcher.Theming;
using SkiaSharp;
using Xunit.Sdk;

namespace Galapa.Launcher.Tests.Theming;

[Collection(SkiaRenderingCollection.Name)]
public sealed class InputRenderingTests(SkiaHeadlessFixture skia)
{
    private static readonly SKColor Fill = SKColor.Parse("#DDE4E2");
    private static readonly SKColor Border = SKColor.Parse("#22AA78");

    [Fact]
    public async Task PathFrameHasARealTopBorderGapWithoutMaskingItsFill()
    {
        const int width = 160;
        const int height = 38;
        const int gapStart = 10;
        const int gapWidth = 72;
        var png = await skia.RenderPathAsync(InputStyle(), width, height, gapStart, gapWidth);
        using var bitmap = SKBitmap.Decode(png) ?? throw new XunitException("Could not decode input render.");
        var x0 = SkiaHeadlessFixture.FixtureOutset;
        var y0 = SkiaHeadlessFixture.FixtureOutset;

        AssertColorNear(Border, bitmap.GetPixel(x0 + 4, y0), "left top-border run");
        AssertColorNear(Border, bitmap.GetPixel(x0 + gapStart + gapWidth + 4, y0), "right top-border run");
        AssertColorNear(Fill, bitmap.GetPixel(x0 + gapStart + gapWidth / 2, y0), "label gap");
        AssertColorNear(Fill, bitmap.GetPixel(x0 + gapStart + gapWidth / 2, y0 + 3), "fill below label gap");
        AssertColorNear(Border, bitmap.GetPixel(x0, y0 + height / 2), "left border");
        AssertColorNear(Border, bitmap.GetPixel(x0 + width / 2, y0 + height - 1), "bottom border");
    }

    [Fact]
    public async Task LabelGapPreservesRoundedCornerGeometry()
    {
        var withGap = await skia.RenderPathAsync(InputStyle(6), 160, 38, 10, 72);
        var withoutGap = await skia.RenderPathAsync(InputStyle(6), 160, 38);
        using var actual = SKBitmap.Decode(withGap) ?? throw new XunitException("Could not decode gapped input render.");
        using var expected = SKBitmap.Decode(withoutGap) ?? throw new XunitException("Could not decode reference input render.");

        // The split stroke must not alter either corner or either vertical edge.
        for (var y = 0; y < actual.Height; y++)
        for (var x = 0; x < 10; x++)
            Assert.Equal(expected.GetPixel(x, y), actual.GetPixel(x, y));
        for (var y = 0; y < actual.Height; y++)
        for (var x = actual.Width - 10; x < actual.Width; x++)
            Assert.Equal(expected.GetPixel(x, y), actual.GetPixel(x, y));
    }

    private static CompiledControl InputStyle(double radius = 0) => new()
    {
        Shape = "Path",
        Fill = "#DDE4E2",
        BorderColor = "#22AA78",
        BorderThickness = JsonSerializer.SerializeToElement(2),
        Radius = JsonSerializer.SerializeToElement(radius),
        Corner = "round"
    };

    private static void AssertColorNear(SKColor expected, SKColor actual, string location)
    {
        var delta = Math.Max(
            Math.Max(Math.Abs(expected.Red - actual.Red), Math.Abs(expected.Green - actual.Green)),
            Math.Max(Math.Abs(expected.Blue - actual.Blue), Math.Abs(expected.Alpha - actual.Alpha)));
        Assert.True(delta <= 8,
            $"Expected {location} to be {expected}, but it was {actual} (maximum channel delta {delta}).");
    }
}
