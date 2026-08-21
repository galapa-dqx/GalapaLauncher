using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Galapa.Launcher.Theming;
using Microsoft.Extensions.Logging.Abstractions;

namespace Galapa.Launcher.Tests.Theming;

[Collection(SkiaRenderingCollection.Name)]
public sealed class ThemeAssetRegistrationTests(SkiaHeadlessFixture skia)
{
    [Fact]
    public async Task EveryBuiltInOwnsResolvableFontsAndPreparableRenderAssets()
    {
        Assert.Null(typeof(CompiledControl).GetProperty("ThemeId"));
        var reader = new CompiledThemeReader();
        var packages = new List<ValidatedTheme>();
        foreach (var path in Directory.EnumerateFiles(TestPaths.BuiltInThemeRoot, "*.compiled.json"))
            packages.Add(await reader.ReadAsync(path));

        await skia.DispatchAsync(() =>
        {
            var loader = new ThemeLoader();
            foreach (var package in packages)
            {
                var loaded = loader.Load(package);
                Assert.Same(package, loaded.Validation);
                Assert.Equal(package.Compiled.Controls.Count, loaded.Resources.Controls.Count);
                Assert.Same(Avalonia.Media.FontManager.Current, loaded.FontManager);
            }
            return Task.FromResult(true);
        });
    }

    [Fact]
    public void EstellaRecoveryThemeIsEmbeddedInTheLauncherAssembly()
    {
        Assert.Contains("Galapa.Launcher.Recovery.estella.compiled.json",
            typeof(CompiledThemeReader).Assembly.GetManifestResourceNames());
    }

    [Fact]
    public async Task CatalogDiscoversAllThemesBeforeBackgroundValidation()
    {
        var pipeline = ThemeTestFactory.Pipeline();
        var catalog = new ThemeCatalog(pipeline, NullLogger<ThemeCatalog>.Instance);

        await Task.Run(() => catalog.InitializeAsync(ThemeId.Default));
        Assert.Equal(13, catalog.Themes.Count);
        Assert.Equal("estella", catalog.Themes[0].Id?.Value);
        Assert.NotNull(catalog.Find(ThemeId.Default));
        Assert.Contains(catalog.Themes, entry => entry.State is DiscoveredTheme);

        await Task.Run(() => catalog.ValidateRemainingAsync());

        Assert.All(catalog.Themes, entry => Assert.IsType<ValidatedTheme>(entry.State));
    }

    [Fact]
    public async Task FontValidationDoesNotPoisonTheRegisteredTypefaceForRendering()
    {
        var package = await new CompiledThemeReader().ReadAsync(TestPaths.BuiltInTheme("estella"));
        var png = await skia.ValidateAndRenderThemeTextAsync(package);
        using var bitmap = BitmapTestSupport.Decode(png, "registered theme font render");
        Assert.Contains(Enumerable.Range(0, bitmap.Width * bitmap.Height), index =>
        {
            var pixel = bitmap.GetPixel(index % bitmap.Width, index / bitmap.Width);
            return pixel.Alpha > 0 && pixel.Red > 0;
        });
    }
}

internal static class ThemeAssetRegistrationScenarios
{
    public static Task<byte[]> ValidateAndRenderThemeTextAsync(this SkiaHeadlessFixture skia,
        ValidatedTheme package) => skia.DispatchAsync(() =>
    {
        ThemeFontRegistrar.Validate(package);
        var loaded = new ThemeLoader().Load(package);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var text = package.Compiled.Controls["titlebar.wordmark"].Text!;
        return SkiaHeadlessFixture.Render(new TextBlock
        {
            Text = "Galapa",
            FontFamily = loaded.Resources.Controls["titlebar.wordmark"].Typography.Family,
            FontStyle = ThemeTypography.ToFontStyle(text.Style),
            FontWeight = (FontWeight)(text.Weight ?? 400),
            FontSize = 24,
            Foreground = Brushes.White,
            Width = 140,
            Height = 44
        }, new PixelSize(140, 44));
    });
}
