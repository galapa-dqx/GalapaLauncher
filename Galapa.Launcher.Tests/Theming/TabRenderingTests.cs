using Avalonia.Media;
using SkiaSharp;

namespace Galapa.Launcher.Tests.Theming;

[Collection(SkiaRenderingCollection.Name)]
public sealed class TabRenderingTests(SkiaHeadlessFixture skia)
{
    [Fact]
    public async Task SelectedTabPaintsItsBottomBorderAcrossTheFullItem()
    {
        var result = await skia.RenderSelectedTabAsync();

        Assert.Equal(34, result.ItemBounds.Height);
        Assert.Equal(2, result.UnderlineBounds.Height);
        Assert.Equal(32, result.UnderlineBounds.Y);
        Assert.Equal(Color.Parse("#22AA78"), result.ItemBorderColor);
        Assert.Equal(Color.Parse("#22AA78"), result.UnderlineColor);
        Assert.Equal(Color.Parse("#22AA78"), result.LabelColor);

        using var bitmap = SKBitmap.Decode(result.Png);
        var underline = new SKColor(0x22, 0xAA, 0x78);
        var widestPaintedRow = 0;
        var rows = new List<string>();
        for (var y = 0; y < bitmap.Height; y++)
        {
            var rowPixels = 0;
            for (var x = 0; x < bitmap.Width; x++)
                if (bitmap.GetPixel(x, y) == underline) rowPixels++;
            if (rowPixels > 0) rows.Add($"{y}:{rowPixels}");
            widestPaintedRow = Math.Max(widestPaintedRow, rowPixels);
        }

        if (widestPaintedRow < 80)
        {
            var artifactRoot = TestPaths.Results("Tab");
            Directory.CreateDirectory(artifactRoot);
            var artifactPath = Path.Combine(artifactRoot, "selected-tab.actual.png");
            await File.WriteAllBytesAsync(artifactPath, result.Png);
            Assert.Fail(
                $"Expected the selected tab underline across its full width; widest painted row was {widestPaintedRow} pixels. " +
                $"Selected-color pixels by row: {string.Join(", ", rows)}. " +
                $"Underline visible/effective/opacity: {result.UnderlineIsVisible}/{result.UnderlineIsEffectivelyVisible}/{result.UnderlineOpacity}. " +
                $"Item: {result.ItemBounds}; underline: {result.UnderlineBounds}. Actual: {artifactPath}");
        }
    }
}
