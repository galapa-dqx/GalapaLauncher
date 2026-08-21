using Galapa.Core.Configuration;
using Galapa.Launcher.Theming;
using SkiaSharp;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using System.Runtime.CompilerServices;
using Xunit.Sdk;

namespace Galapa.Launcher.Tests.Theming;

internal static class TestPaths
{
    internal static readonly string ProjectRoot = Path.GetFullPath(Path.Combine(SourceDirectory(), ".."));
    internal static readonly string LauncherProjectRoot = Path.GetFullPath(Path.Combine(ProjectRoot, "..", "Galapa.Launcher"));
    internal static readonly string FixtureRoot = Path.Combine(ProjectRoot, "Fixtures");
    internal static readonly string ResultRoot = Path.Combine(ProjectRoot, "TestResults");
    internal static readonly string BuiltInThemeRoot = Path.GetFullPath(Path.Combine(
        ProjectRoot, "..", "Galapa.Launcher", "Assets", "Themes"));

    internal static string Fixtures(string suite) => Path.Combine(FixtureRoot, suite);
    internal static string Results(string suite) => Path.Combine(ResultRoot, suite);
    internal static string BuiltInTheme(string id) => Path.Combine(BuiltInThemeRoot, $"{id}.compiled.json");
    internal static IEnumerable<string> BuiltInThemes() =>
        Directory.EnumerateFiles(BuiltInThemeRoot, $"*{ThemeLocations.CompiledSuffix}")
            .OrderBy(path => path, StringComparer.Ordinal);

    private static string SourceDirectory([CallerFilePath] string sourcePath = "") =>
        Path.GetDirectoryName(sourcePath)!;
}

internal sealed class ApplicationStateScope : IDisposable
{
    private readonly Application _application;
    private readonly ThemeVariant? _variant;
    private readonly IReadOnlyList<IResourceProvider> _mergedDictionaries;
    private readonly IReadOnlyDictionary<object, object?> _localResources;

    private ApplicationStateScope(Application application)
    {
        Dispatcher.UIThread.VerifyAccess();
        _application = application;
        _variant = application.RequestedThemeVariant;
        _mergedDictionaries = application.Resources.MergedDictionaries.ToArray();
        _localResources = application.Resources.ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    public static ApplicationStateScope Capture() => new(
        Application.Current ?? throw new InvalidOperationException("The test application is not running."));

    public void Dispose()
    {
        Dispatcher.UIThread.VerifyAccess();
        foreach (var key in _application.Resources.Keys.ToArray()) _application.Resources.Remove(key);
        foreach (var (key, value) in _localResources) _application.Resources[key] = value;
        _application.Resources.MergedDictionaries.Clear();
        foreach (var dictionary in _mergedDictionaries)
            _application.Resources.MergedDictionaries.Add(dictionary);
        _application.RequestedThemeVariant = _variant;
    }
}

internal static class ThemeTestFactory
{
    public static ThemePipeline Pipeline(IThemeLoader? loader = null) => new(
        new CompiledThemeReader(), loader ?? new ThemeLoader(),
        Microsoft.Extensions.Logging.Abstractions.NullLogger<ThemePipeline>.Instance);

    public static ThemeCatalogEntry Entry(ValidatedTheme validated) =>
        new(validated.Discovery) { State = validated };
}

internal static class BitmapTestSupport
{
    public static SKBitmap Decode(byte[] png, string description) =>
        SKBitmap.Decode(png) ?? throw new Xunit.Sdk.XunitException($"Could not decode {description}.");
}

internal static class GoldenImage
{
    private const string UpdateVariable = "GALAPA_UPDATE_RENDER_FIXTURES";

    internal static async Task AssertMatchesOrUpdate(string expectedPath, byte[] actualPng,
        string name, string artifactGroup)
    {
        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")) ||
                string.Equals(Environment.GetEnvironmentVariable("GITHUB_ACTIONS"), "true",
                    StringComparison.OrdinalIgnoreCase))
                throw new XunitException($"{UpdateVariable}=1 is forbidden in CI; no fixture was written.");
            Directory.CreateDirectory(Path.GetDirectoryName(expectedPath)!);
            await File.WriteAllBytesAsync(expectedPath, actualPng);
            return;
        }
        if (!File.Exists(expectedPath))
            throw new XunitException(
                $"Missing render fixture '{Path.GetFileName(expectedPath)}'. Run locally with {UpdateVariable}=1 to create it.");
        ComparePixels(expectedPath, actualPng, name, artifactGroup);
    }

    internal static int MaxChannelDelta(SKColor expected, SKColor actual) =>
        Math.Max(Math.Max(Math.Abs(expected.Red - actual.Red), Math.Abs(expected.Green - actual.Green)),
            Math.Max(Math.Abs(expected.Blue - actual.Blue), Math.Abs(expected.Alpha - actual.Alpha)));

    private static void ComparePixels(string expectedPath, byte[] actualPng, string name, string artifactGroup)
    {
        using var expected = SKBitmap.Decode(expectedPath)
            ?? throw new XunitException($"Could not decode expected image: {expectedPath}");
        using var actual = SKBitmap.Decode(actualPng)
            ?? throw new XunitException($"Could not decode actual image for {name}.");
        var mismatchCount = 0;
        var maximumDelta = 0;
        var diffWidth = Math.Max(expected.Width, actual.Width);
        var diffHeight = Math.Max(expected.Height, actual.Height);
        using var diff = new SKBitmap(diffWidth, diffHeight, SKColorType.Rgba8888, SKAlphaType.Opaque);
        for (var y = 0; y < diffHeight; y++)
            for (var x = 0; x < diffWidth; x++)
            {
                if (x >= expected.Width || y >= expected.Height || x >= actual.Width || y >= actual.Height)
                {
                    mismatchCount++;
                    maximumDelta = 255;
                    diff.SetPixel(x, y, new SKColor(255, 0, 255, 255));
                    continue;
                }
                var wanted = expected.GetPixel(x, y);
                var rendered = actual.GetPixel(x, y);
                var delta = MaxChannelDelta(wanted, rendered);
                maximumDelta = Math.Max(maximumDelta, delta);
                if (delta == 0)
                    diff.SetPixel(x, y, new SKColor((byte)(wanted.Red / 3), (byte)(wanted.Green / 3),
                        (byte)(wanted.Blue / 3), 255));
                else
                {
                    mismatchCount++;
                    diff.SetPixel(x, y, new SKColor(255, 0, 255, 255));
                }
            }
        if (mismatchCount == 0) return;
        var artifactRoot = TestPaths.Results(artifactGroup);
        Directory.CreateDirectory(artifactRoot);
        var actualPath = Path.Combine(artifactRoot, $"{name}.actual.png");
        var diffPath = Path.Combine(artifactRoot, $"{name}.diff.png");
        File.WriteAllBytes(actualPath, actualPng);
        Save(diff, diffPath);
        var sizeMessage = expected.Width == actual.Width && expected.Height == actual.Height
            ? string.Empty
            : $" Expected {expected.Width}x{expected.Height}, rendered {actual.Width}x{actual.Height}.";
        throw new XunitException(
            $"{name}: {mismatchCount} pixels differ (maximum channel delta {maximumDelta}).{sizeMessage} " +
            $"Actual: {actualPath}; diff: {diffPath}");
    }

    private static void Save(SKBitmap bitmap, string path)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        using var stream = File.Create(path);
        data.SaveTo(stream);
    }
}

internal sealed class FailingSettingsPersistence(Exception? failure = null) : ISettingsPersistence
{
    public Task SaveAsync(Settings settings, CancellationToken cancellationToken = default) =>
        Task.FromException(failure ?? new IOException("Settings persistence failed for the test."));
}

internal sealed class RecordingSettingsPersistence : ISettingsPersistence
{
    public int SaveCount { get; private set; }
    public Task SaveAsync(Settings settings, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SaveCount++;
        return Task.CompletedTask;
    }
}
