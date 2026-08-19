using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Galapa.Core.Configuration;
using Galapa.Launcher.Theming;
using Microsoft.Extensions.Logging.Abstractions;

namespace Galapa.Launcher.Tests.Theming;

[Collection(SkiaRenderingCollection.Name)]
public sealed class ThemeManagerTests(SkiaHeadlessFixture skia)
{
    [Fact]
    public async Task ApplyIsTransactionalAcrossInvalidThemesAndPersistenceFailures()
    {
        var reader = new CompiledThemeReader();
        var estella = await reader.ReadAsync(TestPaths.BuiltInTheme("estella"));
        var duston = await reader.ReadAsync(TestPaths.BuiltInTheme("duston"));
        var estellaEntry = Entry(estella);
        var dustonEntry = Entry(duston);
        var invalidDiscovery = new DiscoveredTheme(new TestSource("invalid"), new ThemeId("invalid"), "Invalid");
        var invalidEntry = new ThemeCatalogEntry(invalidDiscovery)
        {
            State = new InvalidTheme(invalidDiscovery, null,
                [new ThemeDiagnostic(ThemeDiagnosticStage.Validation, "test.invalid", "Invalid test theme")])
        };
        var catalog = new TestCatalog(estellaEntry, dustonEntry, invalidEntry);
        var persistence = new FailingSettingsPersistence(new IOException("blocked"));
        var settings = new Settings { ThemeId = Settings.DefaultThemeId };
        var pipeline = Pipeline(new ThemeLoader());
        var manager = new ThemeManager(catalog, pipeline, settings, persistence,
            NullLogger<ThemeManager>.Instance);

        await skia.DispatchAsync(async () =>
        {
            var app = Application.Current!;
            var initialVariant = app.RequestedThemeVariant;
            var initialDictionaries = app.Resources.MergedDictionaries.ToHashSet();
            try
            {
                Assert.True((await manager.ApplyAsync(new ThemeId("estella"), persist: false)).Succeeded);
                var estellaDictionary = Assert.IsType<ResourceDictionary>(app.Resources.MergedDictionaries.Last());
                var panel = Assert.IsType<Galapa.Launcher.Views.Controls.ThemePartPresentation>(
                    estellaDictionary["Galapa.Part.panel"]);
                Assert.Same(estella.Compiled.Controls["panel"], panel.Control);
                Assert.Equal(20, Assert.IsType<double>(estellaDictionary["Galapa.Type.Control.titlebar.wordmark.Size"]));
                Assert.Equal(16, Assert.IsType<double>(estellaDictionary["Galapa.Type.Control.button.Size"]));
                Assert.Equal(0.3, Assert.IsType<double>(estellaDictionary["Galapa.Type.Control.tab.LetterSpacing"]));
                Assert.DoesNotContain(estellaDictionary.Keys.Cast<object>(), key =>
                    key is string text && text.StartsWith("Galapa.Type.", StringComparison.Ordinal) &&
                    !text.StartsWith("Galapa.Type.Control.", StringComparison.Ordinal));
                Assert.False(estellaDictionary.ContainsKey("Galapa.Font.Body"));
                Assert.False(Assert.IsType<bool>(estellaDictionary["Galapa.Part.input.ShowFocusRing"]));
                Assert.Equal("estella", settings.ThemeId);
                Assert.Equal(ThemeVariant.Light, app.RequestedThemeVariant);

                var invalidResult = await manager.ApplyAsync(invalidEntry, persist: false);
                Assert.Equal(ThemeApplyStatus.Invalid, invalidResult.Status);
                Assert.Equal("estella", manager.ActiveTheme!.Manifest.Id);
                Assert.Contains(estellaDictionary, app.Resources.MergedDictionaries);

                var failed = await manager.ApplyAsync(new ThemeId("duston"));
                Assert.Equal(ThemeApplyStatus.PersistenceFailed, failed.Status);
                Assert.Equal("estella", settings.ThemeId);
                Assert.Equal("estella", manager.ActiveTheme!.Manifest.Id);
                Assert.Equal(ThemeVariant.Light, app.RequestedThemeVariant);
                Assert.Contains(estellaDictionary, app.Resources.MergedDictionaries);

                Assert.True((await manager.ApplyAsync(new ThemeId("duston"), persist: false)).Succeeded);
                Assert.Equal("duston", manager.ActiveTheme!.Manifest.Id);
                Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);
                return true;
            }
            finally
            {
                foreach (var dictionary in app.Resources.MergedDictionaries
                             .Where(item => !initialDictionaries.Contains(item)).ToArray())
                    app.Resources.MergedDictionaries.Remove(dictionary);
                app.RequestedThemeVariant = initialVariant;
            }
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadingAlwaysMaterializesAvaloniaAssetsOnTheUiThread(bool initialApply)
    {
        var validated = await new CompiledThemeReader().ReadAsync(TestPaths.BuiltInTheme("kyururu"));
        var entry = Entry(validated);
        var catalog = new TestCatalog(entry);
        var loader = new UiAssertingLoader();
        var pipeline = Pipeline(loader);
        var manager = new ThemeManager(catalog, pipeline, new Settings(), new RecordingSettingsPersistence(),
            NullLogger<ThemeManager>.Instance);

        await skia.DispatchAsync(async () =>
        {
            var app = Application.Current!;
            var initialVariant = app.RequestedThemeVariant;
            var initialDictionaries = app.Resources.MergedDictionaries.ToHashSet();
            try
            {
                var applied = initialApply
                    ? await manager.ApplyInitialAsync(new ThemeId("kyururu"))
                    : await manager.ApplyAsync(new ThemeId("kyururu"), persist: false);
                Assert.True(applied.Succeeded);
                Assert.True(loader.WasCalledOnUiThread);
                return true;
            }
            finally
            {
                foreach (var dictionary in app.Resources.MergedDictionaries
                             .Where(item => !initialDictionaries.Contains(item)).ToArray())
                    app.Resources.MergedDictionaries.Remove(dictionary);
                app.RequestedThemeVariant = initialVariant;
            }
        });
    }

    [Fact]
    public async Task CancellationAfterInstallationRollsBackThemeAndPreference()
    {
        var reader = new CompiledThemeReader();
        var estella = await reader.ReadAsync(TestPaths.BuiltInTheme("estella"));
        var duston = await reader.ReadAsync(TestPaths.BuiltInTheme("duston"));
        var catalog = new TestCatalog(Entry(estella), Entry(duston));
        var pipeline = Pipeline(new ThemeLoader());
        var persistence = new BlockingPersistence();
        var settings = new Settings { ThemeId = "estella" };
        var manager = new ThemeManager(catalog, pipeline, settings, persistence,
            NullLogger<ThemeManager>.Instance);

        await skia.DispatchAsync(async () =>
        {
            var app = Application.Current!;
            var initialVariant = app.RequestedThemeVariant;
            var initialDictionaries = app.Resources.MergedDictionaries.ToHashSet();
            try
            {
                Assert.True((await manager.ApplyAsync(new ThemeId("estella"), persist: false)).Succeeded);
                using var cancellation = new CancellationTokenSource();
                var applying = manager.ApplyAsync(new ThemeId("duston"), persist: true, cancellation.Token);
                await persistence.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => applying);
                Assert.Equal("estella", settings.ThemeId);
                Assert.Equal("estella", manager.ActiveTheme!.Manifest.Id);
                Assert.Equal(ThemeVariant.Light, app.RequestedThemeVariant);
                return true;
            }
            finally
            {
                foreach (var dictionary in app.Resources.MergedDictionaries
                             .Where(item => !initialDictionaries.Contains(item)).ToArray())
                    app.Resources.MergedDictionaries.Remove(dictionary);
                app.RequestedThemeVariant = initialVariant;
            }
        });
    }

    [Fact]
    public async Task ApplyingOneThemeOnlyLoadsThatTheme()
    {
        var reader = new CompiledThemeReader();
        var estella = await reader.ReadAsync(TestPaths.BuiltInTheme("estella"));
        var duston = await reader.ReadAsync(TestPaths.BuiltInTheme("duston"));
        var loader = new RecordingLoader();
        var manager = new ThemeManager(new TestCatalog(Entry(estella), Entry(duston)), Pipeline(loader),
            new Settings(), new RecordingSettingsPersistence(), NullLogger<ThemeManager>.Instance);

        await skia.DispatchAsync(async () =>
        {
            var app = Application.Current!;
            var initialVariant = app.RequestedThemeVariant;
            var initialDictionaries = app.Resources.MergedDictionaries.ToHashSet();
            try
            {
                Assert.True((await manager.ApplyAsync(new ThemeId("estella"), persist: false)).Succeeded);
                Assert.Equal(["estella"], loader.ThemeIds);
                return true;
            }
            finally
            {
                foreach (var dictionary in app.Resources.MergedDictionaries
                             .Where(item => !initialDictionaries.Contains(item)).ToArray())
                    app.Resources.MergedDictionaries.Remove(dictionary);
                app.RequestedThemeVariant = initialVariant;
            }
        });
    }

    [Fact]
    public async Task ApplicationFailureRetainsTheCurrentTheme()
    {
        var reader = new CompiledThemeReader();
        var estella = await reader.ReadAsync(TestPaths.BuiltInTheme("estella"));
        var duston = await reader.ReadAsync(TestPaths.BuiltInTheme("duston"));
        var catalog = new TestCatalog(Entry(estella), Entry(duston));
        var manager = new ThemeManager(catalog, Pipeline(new BrokenDustonLoader()), new Settings(),
            new RecordingSettingsPersistence(), NullLogger<ThemeManager>.Instance);

        await skia.DispatchAsync(async () =>
        {
            var app = Application.Current!;
            var initialVariant = app.RequestedThemeVariant;
            var initialDictionaries = app.Resources.MergedDictionaries.ToHashSet();
            try
            {
                Assert.True((await manager.ApplyAsync(new ThemeId("estella"), persist: false)).Succeeded);
                var currentDictionary = manager.ActiveTheme!.Dictionary;

                var failed = await manager.ApplyAsync(new ThemeId("duston"), persist: false);

                Assert.Equal(ThemeApplyStatus.ApplyFailed, failed.Status);
                Assert.Equal("estella", manager.ActiveTheme!.Manifest.Id);
                Assert.Contains(currentDictionary, app.Resources.MergedDictionaries);
                Assert.Equal(ThemeVariant.Light, app.RequestedThemeVariant);
                return true;
            }
            finally
            {
                foreach (var dictionary in app.Resources.MergedDictionaries
                             .Where(item => !initialDictionaries.Contains(item)).ToArray())
                    app.Resources.MergedDictionaries.Remove(dictionary);
                app.RequestedThemeVariant = initialVariant;
            }
        });
    }

    private static ThemePipeline Pipeline(IThemeLoader loader) => new(new CompiledThemeReader(), loader,
        NullLogger<ThemePipeline>.Instance);

    private static ThemeCatalogEntry Entry(ValidatedTheme theme)
    {
        var entry = new ThemeCatalogEntry(theme.Discovery) { State = theme };
        return entry;
    }

    private sealed class TestCatalog(params ThemeCatalogEntry[] themes) : IThemeCatalog
    {
        public event EventHandler? ThemesChanged { add { } remove { } }
        public IReadOnlyList<ThemeCatalogEntry> Themes { get; } = themes;
        public ThemeCatalogEntry? Find(ThemeId id) => Themes.FirstOrDefault(theme => theme.Id == id);
        public Task InitializeAsync(ThemeId preferredThemeId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
        public Task ValidateRemainingAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> RecoverDefaultAsync(ThemeCatalogEntry entry,
            IReadOnlyList<ThemeDiagnostic> diagnostics, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class UiAssertingLoader : IThemeLoader
    {
        private readonly ThemeLoader _inner = new();
        public bool WasCalledOnUiThread { get; private set; }
        public LoadedTheme Load(ValidatedTheme theme)
        {
            WasCalledOnUiThread = Dispatcher.UIThread.CheckAccess();
            return _inner.Load(theme);
        }
    }

    private sealed class RecordingLoader : IThemeLoader
    {
        private readonly ThemeLoader _inner = new();
        public List<string> ThemeIds { get; } = [];
        public LoadedTheme Load(ValidatedTheme theme)
        {
            ThemeIds.Add(theme.Manifest.Id);
            return _inner.Load(theme);
        }
    }

    private sealed class BrokenDustonLoader : IThemeLoader
    {
        private readonly ThemeLoader _inner = new();
        public LoadedTheme Load(ValidatedTheme theme) => theme.Manifest.Id == "duston"
            ? new LoadedTheme(theme, FontManager.Current,
                new LoadedThemeResources(
                    new Dictionary<string, Galapa.Launcher.Views.Controls.ThemePartPresentation>(),
                    new Dictionary<string, Galapa.Launcher.Views.Controls.InlineSvgDocument>(),
                    new Dictionary<string, Galapa.Launcher.Views.Controls.NineSliceSvg>()))
            : _inner.Load(theme);
    }

    private sealed class BlockingPersistence : ISettingsPersistence
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task SaveAsync(Settings settings, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class TestSource(string id) : IThemeSource
    {
        public string SourceId => id;
        public string FallbackDisplayName => id;
        public string Description => id;
        public IReadOnlyList<ThemeFontSourceDescriptor> Fonts => [];
        public ValueTask<Stream> OpenCompiledJsonAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromException<Stream>(new FileNotFoundException());
    }
}
