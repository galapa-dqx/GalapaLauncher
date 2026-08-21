using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Media;
using Galapa.Launcher.Theming;
using Galapa.Launcher.Views.Controls;
using Avalonia;
using SkiaSharp;

namespace Galapa.Launcher.Tests.Theming;

[Collection(SkiaRenderingCollection.Name)]
public sealed class ThemePartStateTests(SkiaHeadlessFixture skia)
{
    [Fact]
    public void CompiledStateNamesRoundTripThroughOneMapping()
    {
        foreach (var (name, state) in CompiledThemeContract.StateNames)
        {
            Assert.Equal(state, CompiledThemeContract.PartState(name));
            Assert.Equal(name, CompiledThemeContract.StateName(state));
        }
        Assert.Null(CompiledThemeContract.StateName(ThemePartState.Normal));
    }

    [Fact]
    public async Task NewsGemUsesTheActiveCompiledVisualForItsFallbackStroke()
    {
        var png = await skia.DispatchAsync(() => SkiaHeadlessFixture.Render(new NewsGem
        {
            Category = "events",
            Fill = Brushes.Transparent,
            State = ThemePartState.Hover,
            PartStyle = ThemePartPresentation.Create(new CompiledControl
            {
                Shape = "Text",
                BorderColor = "#ff0000",
                States = new Dictionary<string, CompiledControl>
                {
                    ["hover"] = new() { BorderColor = "#00ff00" }
                }
            }),
            Width = 11,
            Height = 14
        }, new PixelSize(11, 14)));

        using var bitmap = BitmapTestSupport.Decode(png, "news gem state render");
        Assert.Contains(Enumerable.Range(0, bitmap.Width).SelectMany(x =>
            Enumerable.Range(0, bitmap.Height).Select(y => bitmap.GetPixel(x, y))),
            pixel => pixel.Green > 200 && pixel.Red < 50);
        Assert.DoesNotContain(Enumerable.Range(0, bitmap.Width).SelectMany(x =>
            Enumerable.Range(0, bitmap.Height).Select(y => bitmap.GetPixel(x, y))),
            pixel => pixel.Red > 200 && pixel.Green < 50);
    }

    [Fact]
    public async Task SwitchingToASparserControlClearsPriorTypographyPaddingAndOpacity()
    {
        await skia.DispatchAsync(() =>
        {
            var decorated = new CompiledControl
            {
                Shape = "Path",
                Content = "#ff0000",
                Opacity = .4,
                Padding = JsonSerializer.SerializeToElement(12),
                Text = new CompiledTextStyle
                {
                    Family = "Decorated",
                    Size = 29,
                    Weight = 700,
                    Style = "italic"
                }
            };
            var sparse = new CompiledControl { Shape = "Path" };
            var part = new ThemePart
            {
                FallbackPadding = new Avalonia.Thickness(3),
                PartStyle = ThemePartPresentation.Create(decorated)
            };

            Assert.Equal(29, part.FontSize);
            Assert.Equal(FontStyle.Italic, part.FontStyle);
            Assert.Equal(new Avalonia.Thickness(12), part.Padding);
            Assert.Equal(.4, part.Opacity);

            part.PartStyle = ThemePartPresentation.Create(sparse);

            Assert.False(part.IsSet(ThemePart.FontFamilyProperty));
            Assert.False(part.IsSet(ThemePart.FontSizeProperty));
            Assert.False(part.IsSet(ThemePart.FontWeightProperty));
            Assert.False(part.IsSet(ThemePart.FontStyleProperty));
            Assert.False(part.IsSet(ThemePart.ForegroundProperty));
            Assert.Equal(new Avalonia.Thickness(3), part.Padding);
            Assert.Equal(1, part.Opacity);
            return Task.FromResult(true);
        });
    }
}
