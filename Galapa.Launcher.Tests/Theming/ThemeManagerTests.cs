using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Galapa.Core.Configuration;
using Galapa.Launcher.Theming;
using Galapa.TestUtilities;
using Microsoft.Extensions.Logging.Abstractions;

namespace Galapa.Launcher.Tests.Theming;

[Collection(SkiaRenderingCollection.Name)]
public sealed class ThemeManagerTests(SkiaHeadlessFixture skia) : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose()
    {
        Paths.AppData = null;
        _temp.Dispose();
    }

    [Fact]
    public async Task ApplyIsTransactionalAcrossConstructionAndPersistenceFailures()
    {
        var reader = new CompiledThemeReader();
        var themeFolder = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "Galapa.Launcher", "Assets", "Themes"));
        var estella = await reader.ReadAsync(Path.Combine(themeFolder, "estella.compiled.json"));
        var duston = await reader.ReadAsync(Path.Combine(themeFolder, "duston.compiled.json"));
        var invalidControls = new Dictionary<string, CompiledControl>(estella.Compiled.Controls, StringComparer.Ordinal);
        invalidControls.Remove("button");
        var invalid = estella with
        {
            Manifest = estella.Manifest with { Id = "invalid", DisplayName = "Invalid" },
            Compiled = estella.Compiled with { Controls = invalidControls }
        };
        var catalog = new TestCatalog(estella, duston, invalid);
        var settings = new Settings { ThemeId = Settings.DefaultThemeId };
        Paths.AppData = _temp.Path;

        await skia.DispatchAsync(async () =>
        {
            var app = Application.Current!;
            var initialVariant = app.RequestedThemeVariant;
            var initialDictionaries = app.Resources.MergedDictionaries.ToHashSet();
            var manager = new ThemeManager(catalog, settings, NullLogger<ThemeManager>.Instance);
            try
            {
                Assert.True(await manager.ApplyAsync("estella"));
                var estellaDictionary = Assert.IsType<ResourceDictionary>(app.Resources.MergedDictionaries.Last());
                Assert.Same(estella.Compiled.Controls["panel"], estellaDictionary["Galapa.Part.panel"]);
                Assert.Equal(20, Assert.IsType<double>(estellaDictionary["Galapa.Type.brand.Size"]));
                Assert.Equal(16, Assert.IsType<double>(estellaDictionary["Galapa.Type.button.Size"]));
                Assert.Equal(0.3, Assert.IsType<double>(estellaDictionary["Galapa.Type.navigation.LetterSpacing"]));
                Assert.IsAssignableFrom<IBrush>(estellaDictionary["Galapa.Part.button.ContentBrush"]);
                Assert.Equal("estella", Settings.Load().ThemeId);
                Assert.Equal(ThemeVariant.Light, app.RequestedThemeVariant);

                Assert.False(await manager.ApplyAsync("invalid", persist: false));
                Assert.Same(estella, manager.ActiveTheme);
                Assert.Contains(estellaDictionary, app.Resources.MergedDictionaries);

                var blockedSettingsPath = Path.Combine(_temp.Path, "not-a-directory");
                File.WriteAllText(blockedSettingsPath, "block settings persistence");
                Paths.AppData = blockedSettingsPath;
                Assert.False(await manager.ApplyAsync("duston"));
                Assert.Equal("estella", settings.ThemeId);
                Assert.Same(estella, manager.ActiveTheme);
                Assert.Equal(ThemeVariant.Light, app.RequestedThemeVariant);
                Assert.Contains(estellaDictionary, app.Resources.MergedDictionaries);

                Assert.True(await manager.ApplyAsync("duston", persist: false));
                Assert.Same(duston, manager.ActiveTheme);
                Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);
                return true;
            }
            finally
            {
                foreach (var dictionary in app.Resources.MergedDictionaries.Where(x => !initialDictionaries.Contains(x)).ToArray())
                    app.Resources.MergedDictionaries.Remove(dictionary);
                app.RequestedThemeVariant = initialVariant;
            }
        });
    }

    [Fact]
    public async Task ApplyMaterializesUncachedAvaloniaAssetsOnTheUiThread()
    {
        var reader = new CompiledThemeReader();
        var themePath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "Galapa.Launcher", "Assets", "Themes",
            "kyururu.compiled.json"));
        var sourcePackage = await reader.ReadAsync(themePath);
        var controls = new Dictionary<string, CompiledControl>(sourcePackage.Compiled.Controls, StringComparer.Ordinal);
        var ornament = controls["play-ornament"];
        controls["play-ornament"] = ornament with
        {
            // Keep this document distinct from the process-wide render cache so
            // another test cannot accidentally hide an off-thread construction.
            Image = ornament.Image + "\n<!-- ui-thread-materialization-regression -->"
        };
        var package = sourcePackage with
        {
            Compiled = sourcePackage.Compiled with { Controls = controls }
        };
        var manager = new ThemeManager(new TestCatalog(package), new Settings(), NullLogger<ThemeManager>.Instance);

        await skia.DispatchAsync(async () =>
        {
            var app = Application.Current!;
            var initialVariant = app.RequestedThemeVariant;
            var initialDictionaries = app.Resources.MergedDictionaries.ToHashSet();
            try
            {
                Assert.True(await manager.ApplyAsync("kyururu", persist: false));
                Assert.Same(package, manager.ActiveTheme);
                return true;
            }
            finally
            {
                foreach (var dictionary in app.Resources.MergedDictionaries.Where(x => !initialDictionaries.Contains(x)).ToArray())
                    app.Resources.MergedDictionaries.Remove(dictionary);
                app.RequestedThemeVariant = initialVariant;
            }
        });
    }

    private sealed class TestCatalog(params ThemePackage[] themes) : IThemeCatalog
    {
        public event EventHandler? ThemesChanged
        {
            add { }
            remove { }
        }
        public IReadOnlyList<ThemePackage> Themes { get; } = themes;
        public ThemePackage? Find(string id) => Themes.FirstOrDefault(theme => theme.Manifest.Id == id);
        public Task LoadInitialAsync(string preferredThemeId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task LoadRemainingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
