using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Media;
using Galapa.Launcher.Theming;
using Galapa.Launcher.Views.Controls;

namespace Galapa.Launcher.Tests.Theming;

[Collection(SkiaRenderingCollection.Name)]
public sealed class ThemePartStateTests(SkiaHeadlessFixture skia)
{
    [Fact]
    public async Task SwitchingToASparserControlClearsPriorTypographyPaddingAndOpacity()
    {
        await skia.DispatchAsync(() =>
        {
            var decorated = new CompiledControl
            {
                Shape = "Path",
                ThemeId = "test",
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
