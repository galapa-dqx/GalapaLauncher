using Galapa.Launcher.Theming;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;

namespace Galapa.Launcher.Tests.Theming;

[Collection(SkiaRenderingCollection.Name)]
public sealed class ThemeAssetRegistrationTests(SkiaHeadlessFixture skia)
{
    [Fact]
    public async Task EveryBuiltInOwnsResolvableFontsAndPreparableRenderAssets()
    {
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
        var pipeline = new ThemePipeline(new CompiledThemeReader(), new ThemeLoader(),
            NullLogger<ThemePipeline>.Instance);
        var catalog = new ThemeCatalog(pipeline, NullLogger<ThemeCatalog>.Instance);

        await Task.Run(() => catalog.InitializeAsync(new ThemeId(Galapa.Core.Configuration.Settings.DefaultThemeId)));
        Assert.Equal(13, catalog.Themes.Count);
        Assert.Equal("estella", catalog.Themes[0].Id?.Value);
        Assert.NotNull(catalog.Find(new ThemeId(Galapa.Core.Configuration.Settings.DefaultThemeId)));
        Assert.Contains(catalog.Themes, entry => entry.State is DiscoveredTheme);

        await Task.Run(() => catalog.ValidateRemainingAsync());

        Assert.All(catalog.Themes, entry => Assert.IsType<ValidatedTheme>(entry.State));
    }

    [Fact]
    public async Task FontValidationDoesNotPoisonTheRegisteredTypefaceForRendering()
    {
        var package = await new CompiledThemeReader().ReadAsync(TestPaths.BuiltInTheme("estella"));
        var png = await skia.ValidateAndRenderThemeTextAsync(package);
        using var bitmap = SKBitmap.Decode(png);
        Assert.Contains(Enumerable.Range(0, bitmap.Width * bitmap.Height), index =>
        {
            var pixel = bitmap.GetPixel(index % bitmap.Width, index / bitmap.Width);
            return pixel.Alpha > 0 && pixel.Red > 0;
        });
    }
}
