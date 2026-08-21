using Avalonia.Media;
using SkiaSharp;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.VisualTree;
using Galapa.Launcher.Theming;
using Galapa.Launcher.Views.Controls;

namespace Galapa.Launcher.Tests.Theming;

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

        using var bitmap = BitmapTestSupport.Decode(result.Png, "selected tab render");
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

    [Fact]
    public async Task HoverRoutesTheFrameAndNavigationLabelToCompiledHoverState()
    {
        var validated = await new CompiledThemeReader().ReadAsync(TestPaths.BuiltInTheme("kyururu"));
        var result = await skia.DispatchAsync(() =>
        {
            using var appState = ApplicationStateScope.Capture();
            var app = SkiaHeadlessFixture.EnsureThemeStyles();
            var loaded = new ThemeLoader().Load(validated);
            var resources = ThemeManager.BuildResources(loaded);
            app.Resources.MergedDictionaries.Add(resources);
            try
            {
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
                return SkiaHeadlessFixture.WithWindow(strip, new Size(200, 34), window =>
                {
                    var item = strip.GetVisualDescendants().OfType<TabStripItem>().First();
                    window.MouseMove(new Point(item.Bounds.Center.X, item.Bounds.Center.Y));
                    window.UpdateLayout();
                    var frame = item.GetVisualDescendants().OfType<ThemePart>().Single(part => part.Name == "PART_TabFrame");
                    var label = item.GetVisualDescendants().OfType<Galapa.UI.Controls.ThemedTextBlock>().Single();
                    return (frame.State, (label.Foreground as ISolidColorBrush)?.Color,
                        (resources["Galapa.Part.tab.hover.ContentBrush"] as ISolidColorBrush)?.Color);
                });
            }
            finally { app.Resources.MergedDictionaries.Remove(resources); }
        });

        Assert.Equal(ThemePartState.Hover, result.State);
        Assert.Equal(result.Item2, result.Item3);
    }
}

internal static class TabRenderingScenarios
{
    public static async Task<TabRenderInspection> RenderSelectedTabAsync(this SkiaHeadlessFixture skia)
    {
        var validated = await new CompiledThemeReader().ReadAsync(TestPaths.BuiltInTheme("kyururu"));
        return await skia.DispatchAsync(() => RenderSelectedTab(new ThemeLoader().Load(validated)));
    }

    private static TabRenderInspection RenderSelectedTab(LoadedTheme loaded)
    {
        using var appState = ApplicationStateScope.Capture();
        var app = SkiaHeadlessFixture.EnsureThemeStyles();
        app.Resources.MergedDictionaries.Add(ThemeManager.BuildResources(loaded));
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
            Background = new SolidColorBrush(Color.Parse(SkiaHeadlessFixture.FixtureBackground)),
            Child = strip
        };
        return SkiaHeadlessFixture.WithWindow(root, new Size(200, 34), window =>
        {
            var selectedItem = strip.GetVisualDescendants().OfType<TabStripItem>().Single(item => item.IsSelected);
            var underline = selectedItem.GetVisualDescendants().OfType<Border>()
                .Single(item => item.Name == "PART_TabUnderline");
            var labelColor = (selectedItem.GetVisualDescendants()
                .OfType<Galapa.UI.Controls.ThemedTextBlock>().Single().Foreground as ISolidColorBrush)?.Color;
            var png = SkiaHeadlessFixture.Capture(window, new PixelSize(
                (int)Math.Ceiling(window.Bounds.Width), (int)Math.Ceiling(window.Bounds.Height)));
            return new TabRenderInspection(
                png,
                underline.Bounds,
                (underline.Background as ISolidColorBrush)?.Color,
                (selectedItem.BorderBrush as ISolidColorBrush)?.Color,
                underline.IsVisible,
                underline.IsEffectivelyVisible,
                underline.Opacity,
                selectedItem.Bounds,
                labelColor);
        });
    }
}
