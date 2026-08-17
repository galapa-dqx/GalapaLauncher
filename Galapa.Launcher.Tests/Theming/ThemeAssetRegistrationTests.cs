using Galapa.Launcher.Theming;
using Galapa.Launcher.Views.Controls;
using Microsoft.Extensions.Logging.Abstractions;

namespace Galapa.Launcher.Tests.Theming;

[Collection(SkiaRenderingCollection.Name)]
public sealed class ThemeAssetRegistrationTests(SkiaHeadlessFixture skia)
{
    [Fact]
    public async Task EveryBuiltInOwnsResolvableFontsAndPreparableRenderAssets()
    {
        var folder = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "Galapa.Launcher", "Assets", "Themes"));
        var reader = new CompiledThemeReader();
        var packages = new List<ThemePackage>();
        foreach (var path in Directory.EnumerateFiles(folder, "*.compiled.json"))
            packages.Add(await reader.ReadAsync(path));

        await skia.DispatchAsync(async () =>
        {
            ThemeFontRegistrar.RegisterBuiltIns();
            await Task.Run(() =>
            {
                foreach (var package in packages)
                    ThemeFontRegistrar.Validate(package);
            });
            foreach (var package in packages)
                ThemeRenderAssets.Prepare(package);
            return true;
        });
    }

    [Fact]
    public void EstellaRecoveryThemeIsEmbeddedInTheLauncherAssembly()
    {
        Assert.Contains("Galapa.Launcher.Recovery.estella.compiled.json",
            typeof(CompiledThemeReader).Assembly.GetManifestResourceNames());
    }

    [Fact]
    public async Task CatalogUsesBackgroundValidationAndUiThreadRenderPreparation()
    {
        await skia.DispatchAsync(() =>
        {
            ThemeFontRegistrar.RegisterBuiltIns();
            return Task.FromResult(true);
        });
        var catalog = new ThemeCatalog(new CompiledThemeReader(), NullLogger<ThemeCatalog>.Instance);

        await Task.Run(() => catalog.LoadAsync());
        await skia.DispatchAsync(() =>
        {
            catalog.PrepareRenderAssets();
            return Task.FromResult(true);
        });

        Assert.Equal(13, catalog.Themes.Count);
        Assert.NotNull(catalog.Find(Galapa.Core.Configuration.Settings.DefaultThemeId));
    }
}
