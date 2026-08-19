using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Galapa.Launcher.Theming;
using Galapa.Launcher.Views.Controls;

namespace Galapa.Launcher.Tests.Theming;

internal sealed class PathTestRenderer(SkiaHeadlessFixture skia)
{
    internal Task<byte[]> RenderAsync(CompiledControl style, int width, int height,
        double gapStart = double.NaN, double gapWidth = 0) =>
        skia.DispatchAsync(() => Render(style, width, height, gapStart, gapWidth));

    internal Task<byte[]> RenderThemePartAsync(CompiledControl style, int width, int height) =>
        skia.DispatchAsync(() => Render(style, width, height, double.NaN, 0, notched: false));

    private static byte[] Render(CompiledControl style, int width, int height,
        double gapStart, double gapWidth, bool notched = true)
    {
        var pixelSize = new PixelSize(width + SkiaHeadlessFixture.FixtureOutset * 2,
            height + SkiaHeadlessFixture.FixtureOutset * 2);
        ThemePart part = notched
            ? new NotchedThemePart { TopBorderGapStart = gapStart, TopBorderGapWidth = gapWidth }
            : new ThemePart();
        part.PartStyle = ThemePartPresentation.Create(style);
        part.Width = width;
        part.Height = height;
        Canvas.SetLeft(part, SkiaHeadlessFixture.FixtureOutset);
        Canvas.SetTop(part, SkiaHeadlessFixture.FixtureOutset);
        var visual = new Canvas
        {
            Width = pixelSize.Width,
            Height = pixelSize.Height,
            Background = new SolidColorBrush(Color.Parse(SkiaHeadlessFixture.FixtureBackground)),
            Children = { part }
        };
        return SkiaHeadlessFixture.Render(visual, pixelSize);
    }
}
