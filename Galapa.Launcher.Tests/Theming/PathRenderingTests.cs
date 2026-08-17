using System.Security.Cryptography;
using System.Text.Json;
using Galapa.Launcher.Theming;
using SkiaSharp;
using Xunit.Sdk;

namespace Galapa.Launcher.Tests.Theming;

[Collection(SkiaRenderingCollection.Name)]
public sealed class PathRenderingTests(SkiaHeadlessFixture skia)
{
    private const string UpdateVariable = "GALAPA_UPDATE_RENDER_FIXTURES";
    private static readonly string SourceFixtureRoot = Path.Combine(GoldenImage.ProjectRoot, "Fixtures", "Path");
    private static readonly string[] Corners = ["round", "bevel", "notch", "scoop", "squircle"];

    public static TheoryData<string, string, int, int, double> GoldenCases => new()
    {
        { "round-radius-8", "round", 36, 28, 8 },
        { "bevel-radius-8", "bevel", 36, 28, 8 },
        { "notch-radius-8", "notch", 36, 28, 8 },
        { "scoop-radius-8", "scoop", 36, 28, 8 },
        { "squircle-radius-8", "squircle", 36, 28, 8 },
        { "round-clamped", "round", 18, 10, 999 },
        { "bevel-clamped", "bevel", 18, 10, 999 },
        { "notch-clamped", "notch", 18, 10, 999 },
        { "scoop-clamped", "scoop", 18, 10, 999 },
        { "squircle-clamped", "squircle", 18, 10, 999 }
    };

    public static TheoryData<string> CornerCases
    {
        get
        {
            var cases = new TheoryData<string>();
            foreach (var corner in Corners) cases.Add(corner);
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(GoldenCases))]
    public async Task RenderingMatchesApprovedFixture(
        string name, string corner, int width, int height, double radius)
    {
        var actual = await skia.RenderPathAsync(PathStyle(corner, radius), width, height);
        var expectedName = $"{name}-{width}x{height}.png";
        var expectedRoot = Path.Combine(SourceFixtureRoot, "Expected");

        if (Environment.GetEnvironmentVariable(UpdateVariable) == "1")
        {
            Directory.CreateDirectory(expectedRoot);
            await File.WriteAllBytesAsync(Path.Combine(expectedRoot, expectedName), actual);
            return;
        }

        var expectedPath = Path.Combine(expectedRoot, expectedName);
        if (!File.Exists(expectedPath))
            throw new XunitException(
                $"Missing render fixture '{expectedName}'. Run the test with {UpdateVariable}=1 to create it.");

        GoldenImage.ComparePixels(expectedPath, actual, name, "Path");
    }

    [Theory]
    [MemberData(nameof(CornerCases))]
    public async Task CornerSilhouetteIsHorizontallyAndVerticallySymmetric(string corner)
    {
        var png = await skia.RenderPathAsync(PathStyle(corner, 8), 36, 28);
        using var bitmap = Decode(png, corner);
        var maximumDelta = 0;

        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            maximumDelta = Math.Max(maximumDelta,
                PixelDelta(bitmap.GetPixel(x, y), bitmap.GetPixel(bitmap.Width - 1 - x, y)));
            maximumDelta = Math.Max(maximumDelta,
                PixelDelta(bitmap.GetPixel(x, y), bitmap.GetPixel(x, bitmap.Height - 1 - y)));
        }

        // Skia's analytic antialiasing can quantize mirrored edge coverage a
        // few channel values apart. A broken or self-intersecting corner is
        // separated by the full fill/background delta, not this small fringe.
        Assert.True(maximumDelta <= 20,
            $"{corner} is asymmetric; mirrored pixels differ by as much as {maximumDelta}.");
    }

    [Theory]
    [MemberData(nameof(CornerCases))]
    public async Task RadiusIsClampedToHalfTheShortEdge(string corner)
    {
        var oversized = await skia.RenderPathAsync(PathStyle(corner, 999), 36, 28);
        var capped = await skia.RenderPathAsync(PathStyle(corner, 14), 36, 28);

        Assert.Equal(PixelFingerprint(capped), PixelFingerprint(oversized));
    }

    [Theory]
    [MemberData(nameof(CornerCases))]
    public async Task ChangingRadiusChangesTheCornerSilhouette(string corner)
    {
        var small = await skia.RenderPathAsync(PathStyle(corner, 4), 36, 28);
        var large = await skia.RenderPathAsync(PathStyle(corner, 10), 36, 28);

        Assert.NotEqual(PixelFingerprint(small), PixelFingerprint(large));
    }

    [Fact]
    public async Task CornerShapesProduceFiveDistinctSilhouettes()
    {
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var corner in Corners)
        {
            var png = await skia.RenderPathAsync(PathStyle(corner, 8), 36, 28);
            fingerprints.Add(PixelFingerprint(png));
        }

        Assert.Equal(Corners.Length, fingerprints.Count);
    }

    [Fact]
    public async Task ZeroRadiusProducesTheSameRectangleForEveryCornerShape()
    {
        var fingerprints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var corner in Corners)
        {
            var png = await skia.RenderPathAsync(PathStyle(corner, 0), 36, 28);
            fingerprints.Add(PixelFingerprint(png));
        }

        Assert.Single(fingerprints);
    }

    private static CompiledControl PathStyle(string corner, double radius) => new()
    {
        Shape = "Path",
        Fill = "#56B4E9",
        BorderColor = "#F6D55C",
        BorderThickness = JsonSerializer.SerializeToElement(2),
        Radius = JsonSerializer.SerializeToElement(radius),
        Corner = corner
    };

    private static string PixelFingerprint(byte[] png)
    {
        using var bitmap = Decode(png, "fingerprint");
        var pixels = new byte[bitmap.Width * bitmap.Height * 4];
        var offset = 0;
        for (var y = 0; y < bitmap.Height; y++)
        for (var x = 0; x < bitmap.Width; x++)
        {
            var pixel = bitmap.GetPixel(x, y);
            pixels[offset++] = pixel.Red;
            pixels[offset++] = pixel.Green;
            pixels[offset++] = pixel.Blue;
            pixels[offset++] = pixel.Alpha;
        }

        return Convert.ToHexString(SHA256.HashData(pixels));
    }

    private static SKBitmap Decode(byte[] png, string name) =>
        SKBitmap.Decode(png) ?? throw new XunitException($"Could not decode the rendered {name} path fixture.");

    private static int PixelDelta(SKColor expected, SKColor actual) =>
        Math.Max(
            Math.Max(Math.Abs(expected.Red - actual.Red), Math.Abs(expected.Green - actual.Green)),
            Math.Max(Math.Abs(expected.Blue - actual.Blue), Math.Abs(expected.Alpha - actual.Alpha)));
}
