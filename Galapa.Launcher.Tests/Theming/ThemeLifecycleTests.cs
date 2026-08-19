using Avalonia;
using Avalonia.Media;
using System.Diagnostics;
using Galapa.Core.Configuration;
using Galapa.Launcher.Theming;
using Microsoft.Extensions.Logging.Abstractions;

namespace Galapa.Launcher.Tests.Theming;

[Collection(SkiaRenderingCollection.Name)]
public sealed class ThemeLifecycleTests(SkiaHeadlessFixture skia)
{
    [Theory]
    [InlineData("estella")]
    [InlineData("theme-2")]
    [InlineData("a")]
    public void ThemeIdAcceptsCanonicalValues(string value) => Assert.Equal(value, new ThemeId(value).Value);

    [Theory]
    [InlineData("")]
    [InlineData("Estella")]
    [InlineData("under_score")]
    [InlineData("sp ace")]
    public void ThemeIdRejectsNonCanonicalValues(string value) =>
        Assert.Throws<ArgumentException>(() => new ThemeId(value));

    [Fact]
    public async Task ValidationParsesEveryUniqueSvgOnceWithoutAvaloniaObjects()
    {
        var parseEvents = 0;
        void Parsed() => parseEvents++;
        ThemeSvgIrParser.XmlParsed += Parsed;
        try
        {
            var validated = await Task.Run(() =>
                new CompiledThemeReader().ReadAsync(TestPaths.BuiltInTheme("aurelia")));
            var sources = AssetSources(validated.Compiled).Distinct(StringComparer.Ordinal).ToArray();

            Assert.Equal(sources.Length, validated.Assets.XmlParseCount);
            Assert.Equal(sources.Length, parseEvents);
            Assert.All(validated.Assets.Documents.Values, document =>
                Assert.Equal(typeof(ThemeRect), document.ViewBox.GetType()));
            Assert.All(validated.Assets.NineSlices.Values.SelectMany(slice => slice.Cells), cell =>
                Assert.Equal(typeof(ThemeMatrix), cell.Shapes.First().Transform.GetType()));
            Assert.All(validated.Controls.Values.Where(control => control.Normal.Fill.HasValue), control =>
                Assert.Equal(typeof(ThemeColor), control.Normal.Fill!.Value.GetType()));

            await skia.DispatchAsync(() => new ThemeLoader().Load(validated));
            Assert.Equal(sources.Length, parseEvents);
        }
        finally { ThemeSvgIrParser.XmlParsed -= Parsed; }
    }

    [Fact]
    public async Task EntryMovesThroughValidatingAndRetainsItsStableIdentity()
    {
        var bytes = await File.ReadAllBytesAsync(TestPaths.BuiltInTheme("estella"));
        var source = new GatedSource("estella", bytes);
        var discovery = Discovery(source);
        var entry = new ThemeCatalogEntry(discovery);
        var pipeline = Pipeline(new ThemeLoader());

        var validation = pipeline.ValidateAsync(entry);
        await source.Opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsType<ValidatingTheme>(entry.State);
        source.Release.TrySetResult();
        Assert.IsType<ValidatedTheme>(await validation);
        Assert.Same(entry, entry);
        Assert.Same(discovery, entry.State.Discovery);
    }

    [Fact]
    public async Task InvalidAndLoadFailedStatesCarryStructuredDiagnostics()
    {
        var invalidSource = new ByteSource("broken", "not json"u8.ToArray());
        var invalidEntry = new ThemeCatalogEntry(Discovery(invalidSource));
        var pipeline = Pipeline(new ThemeLoader());
        var invalid = Assert.IsType<InvalidTheme>(await pipeline.ValidateAsync(invalidEntry));
        Assert.Equal(ThemeDiagnosticStage.Validation, Assert.Single(invalid.Diagnostics).Stage);
        Assert.Equal("theme.validation.failed", invalid.Diagnostics[0].Code);

        var validated = await new CompiledThemeReader().ReadAsync(TestPaths.BuiltInTheme("estella"));
        var failedEntry = Entry(validated);
        var failedPipeline = Pipeline(new ThrowingLoader());
        await skia.DispatchAsync(async () =>
        {
            var failed = Assert.IsType<LoadFailedTheme>(await failedPipeline.LoadAsync(failedEntry));
            Assert.Equal(ThemeDiagnosticStage.Loading, Assert.Single(failed.Diagnostics).Stage);
            Assert.Same(failed, failedEntry.State);
            return true;
        });
    }

    [Fact]
    public async Task EntryMovesThroughLoadingBeforeBecomingLoaded()
    {
        var validated = await new CompiledThemeReader().ReadAsync(TestPaths.BuiltInTheme("estella"));
        var entry = Entry(validated);
        var transitions = new List<Type>();
        entry.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(ThemeCatalogEntry.State)) transitions.Add(entry.State.GetType());
        };

        var loaded = await skia.DispatchAsync(async () =>
            Assert.IsType<LoadedTheme>(await Pipeline(new ThemeLoader()).LoadAsync(entry)));

        Assert.Equal([typeof(LoadingTheme), typeof(LoadedTheme)], transitions);
        Assert.Same(loaded, entry.State);
    }

    [Fact]
    public async Task CancellationRestoresThePrecedingSuccessfulState()
    {
        var source = new CancelingSource("estella");
        var discovery = Discovery(source);
        var entry = new ThemeCatalogEntry(discovery);
        using var cancellation = new CancellationTokenSource();
        var validation = Pipeline(new ThemeLoader()).ValidateAsync(entry, cancellation.Token);
        await source.Opened.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => validation);
        Assert.Same(discovery, entry.State);
    }

    [Fact]
    public async Task CatalogKeepsDuplicateAndMissingEntriesVisibleInStableOrder()
    {
        var bytes = await File.ReadAllBytesAsync(TestPaths.BuiltInTheme("estella"));
        var estella = new ByteSource("estella", bytes, fonts: true);
        var sources = new IThemeSource[]
        {
            new ByteSource("zeta", bytes), new ByteSource("zeta", bytes), estella,
            new ByteSource("alpha", bytes)
        };
        var pipeline = Pipeline(new ThemeLoader());
        var catalog = new ThemeCatalog(pipeline, NullLogger<ThemeCatalog>.Instance, () => sources, estella);

        await catalog.InitializeAsync(new ThemeId("missing"));

        Assert.Equal("estella", catalog.Themes[0].Id?.Value);
        Assert.Equal(new[] { "estella", "alpha", "missing", "zeta", "zeta" },
            catalog.Themes.Select(entry => entry.Id?.Value));
        var duplicates = catalog.Themes.Where(entry => entry.Id?.Value == "zeta").ToArray();
        Assert.Equal(2, duplicates.Length);
        Assert.All(duplicates, duplicate =>
        {
            Assert.IsType<InvalidTheme>(duplicate.State);
            Assert.Equal("theme.id.duplicate", duplicate.Diagnostics.Single().Code);
        });
        var missing = catalog.Find(new ThemeId("missing"));
        Assert.NotNull(missing);
        Assert.IsType<InvalidTheme>(missing.State);
    }

    [Fact]
    public async Task MissingSavedThemeRemainsVisibleWhileEstellaRecoversWithoutChangingPreference()
    {
        var bytes = await File.ReadAllBytesAsync(TestPaths.BuiltInTheme("estella"));
        var estellaSource = new ByteSource("estella", bytes, fonts: true);
        var pipeline = Pipeline(new ThemeLoader());
        var catalog = new ThemeCatalog(pipeline, NullLogger<ThemeCatalog>.Instance,
            () => [estellaSource], estellaSource);
        var settings = new Settings { ThemeId = "missing" };
        var manager = new ThemeManager(catalog, pipeline, settings, new RecordingSettingsPersistence(),
            NullLogger<ThemeManager>.Instance);

        await skia.DispatchAsync(async () =>
        {
            var app = Application.Current!;
            var initialVariant = app.RequestedThemeVariant;
            var initialDictionaries = app.Resources.MergedDictionaries.ToHashSet();
            try
            {
                await catalog.InitializeAsync(new ThemeId("missing"));
                var result = await manager.ApplyInitialAsync(new ThemeId("missing"));
                Assert.True(result.Succeeded);
                Assert.Equal("estella", manager.ActiveTheme!.Manifest.Id);
                Assert.Equal("missing", settings.ThemeId);
                Assert.IsType<InvalidTheme>(catalog.Find(new ThemeId("missing"))!.State);
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
    public async Task InvalidSavedThemeRemainsVisibleWhileEstellaBecomesActive()
    {
        var estellaBytes = await File.ReadAllBytesAsync(TestPaths.BuiltInTheme("estella"));
        var estellaSource = new ByteSource("estella", estellaBytes, fonts: true);
        var brokenSource = new ByteSource("broken", "not json"u8.ToArray());
        var pipeline = Pipeline(new ThemeLoader());
        var catalog = new ThemeCatalog(pipeline, NullLogger<ThemeCatalog>.Instance,
            () => [brokenSource, estellaSource], estellaSource);
        var settings = new Settings { ThemeId = "broken" };
        var manager = new ThemeManager(catalog, pipeline, settings, new RecordingSettingsPersistence(),
            NullLogger<ThemeManager>.Instance);

        await skia.DispatchAsync(async () =>
        {
            var app = Application.Current!;
            var initialVariant = app.RequestedThemeVariant;
            var initialDictionaries = app.Resources.MergedDictionaries.ToHashSet();
            try
            {
                await catalog.InitializeAsync(new ThemeId("broken"));
                var result = await manager.ApplyInitialAsync(new ThemeId("broken"));
                Assert.True(result.Succeeded);
                Assert.Equal("estella", manager.ActiveTheme!.Manifest.Id);
                Assert.Equal("broken", settings.ThemeId);
                Assert.IsType<InvalidTheme>(catalog.Find(new ThemeId("broken"))!.State);
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
    public async Task LoadingTheSameValidationTwiceCreatesApplicationScopedAssets()
    {
        var validated = await new CompiledThemeReader().ReadAsync(TestPaths.BuiltInTheme("aurelia"));
        await skia.DispatchAsync(() =>
        {
            var loader = new ThemeLoader();
            var first = loader.Load(validated);
            var second = loader.Load(validated);
            Assert.NotSame(first.Resources, second.Resources);
            Assert.NotSame(first.Resources.Controls["panel"], second.Resources.Controls["panel"]);
            Assert.NotSame(first.Resources.NineSlices.Values.First(), second.Resources.NineSlices.Values.First());
            Assert.Same(FontManager.Current, first.FontManager);
            return Task.FromResult(true);
        });
    }

    [Fact]
    public async Task DeployedEstellaLoadFailureRecoversIntoTheSameCatalogEntry()
    {
        var bytes = await File.ReadAllBytesAsync(TestPaths.BuiltInTheme("estella"));
        var deployed = new ByteSource("estella", bytes, fonts: true);
        var recovery = new ByteSource("estella", bytes, fonts: true);
        var loader = new DeployedFailureLoader(deployed);
        var pipeline = Pipeline(loader);
        var catalog = new ThemeCatalog(pipeline, NullLogger<ThemeCatalog>.Instance, () => [deployed], recovery);
        var settings = new Settings { ThemeId = "estella" };
        var manager = new ThemeManager(catalog, pipeline, settings, new RecordingSettingsPersistence(),
            NullLogger<ThemeManager>.Instance);

        await skia.DispatchAsync(async () =>
        {
            var app = Application.Current!;
            var initialVariant = app.RequestedThemeVariant;
            var initialDictionaries = app.Resources.MergedDictionaries.ToHashSet();
            try
            {
                await catalog.InitializeAsync(new ThemeId("estella"));
                var entry = Assert.Single(catalog.Themes);
                Assert.Equal(1, recovery.OpenCount);
                Assert.True(manager.ApplyInitialAsync(new ThemeId("estella")).GetAwaiter().GetResult().Succeeded);
                Assert.Same(entry, catalog.Find(new ThemeId("estella")));
                Assert.Same(recovery, manager.ActiveTheme!.Loaded.Validation.EffectiveSource);
                Assert.Contains(manager.ActiveTheme.Diagnostics,
                    diagnostic => diagnostic.Code == "theme.loading.failed");
                Assert.Equal("estella", settings.ThemeId);
                Assert.Equal(1, recovery.OpenCount);
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

    private static ThemeCatalogEntry Entry(ValidatedTheme validated) =>
        new(validated.Discovery) { State = validated };

    private static DiscoveredTheme Discovery(IThemeSource source) => new(source,
        ThemeId.TryParse(source.SourceId, out var id) ? id : null, source.FallbackDisplayName);

    private static IEnumerable<string> AssetSources(CompiledTheme theme)
    {
        foreach (var control in theme.Controls.Values)
        {
            foreach (var source in AssetSources(control)) yield return source;
            if (control.States is null) continue;
            foreach (var state in control.States.Values)
                foreach (var source in AssetSources(state)) yield return source;
        }
    }

    private static IEnumerable<string> AssetSources(CompiledControl control)
    {
        if (control.Art is not null) yield return control.Art;
        if (control.Image is not null) yield return control.Image;
        if (control.Images is not null)
            foreach (var image in control.Images.Values) yield return image;
    }

    private class ByteSource(string id, byte[] bytes, bool fonts = false) : IThemeSource
    {
        private int _openCount;
        public string SourceId => id;
        public string FallbackDisplayName => id;
        public string Description => $"memory:{id}:{GetHashCode()}";
        public IReadOnlyList<ThemeFontSourceDescriptor> Fonts { get; } = fonts
            ? [new ThemeFontSourceDescriptor(new Uri($"fonts:{id}"),
                new Uri($"avares://Galapa.Launcher/Assets/ThemeSources/{id}/fonts"))]
            : [];
        public int OpenCount => _openCount;
        public virtual ValueTask<Stream> OpenCompiledJsonAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _openCount);
            return ValueTask.FromResult<Stream>(new MemoryStream(bytes, writable: false));
        }
    }

    private sealed class GatedSource(string id, byte[] bytes) : ByteSource(id, bytes)
    {
        public TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<Stream> OpenCompiledJsonAsync(CancellationToken cancellationToken = default)
        {
            Opened.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return await base.OpenCompiledJsonAsync(cancellationToken);
        }
    }

    private sealed class CancelingSource(string id) : IThemeSource
    {
        public TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string SourceId => id;
        public string FallbackDisplayName => id;
        public string Description => id;
        public IReadOnlyList<ThemeFontSourceDescriptor> Fonts => [];
        public async ValueTask<Stream> OpenCompiledJsonAsync(CancellationToken cancellationToken = default)
        {
            Opened.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new UnreachableException();
        }
    }

    private sealed class ThrowingLoader : IThemeLoader
    {
        public LoadedTheme Load(ValidatedTheme theme) => throw new InvalidOperationException("test load failure");
    }

    private sealed class DeployedFailureLoader(IThemeSource deployed) : IThemeLoader
    {
        private readonly ThemeLoader _inner = new();
        public LoadedTheme Load(ValidatedTheme theme)
        {
            if (ReferenceEquals(theme.EffectiveSource, deployed))
                throw new InvalidOperationException("deployed Estella load failed");
            return _inner.Load(theme);
        }
    }

}
