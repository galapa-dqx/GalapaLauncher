using System.Text.Json.Nodes;
using Galapa.Launcher.Theming;
using Galapa.TestUtilities;

namespace Galapa.Launcher.Tests.Theming;

public sealed class CompiledThemeReaderTests : IDisposable
{
    private static readonly string[] ExpectedBuiltIns =
    [
        "anlucia", "asbal", "aurelia", "duston", "estella", "fostail", "kyururu",
        "lushenda", "maille", "mereade", "rosie", "seraphi", "yuliza"
    ];

    private readonly TempDirectory _temp = new();
    private static string BuiltInFolder => TestPaths.BuiltInThemeRoot;

    public void Dispose() => _temp.Dispose();

    [Theory]
    [InlineData("#11223344", 0x11, 0x22, 0x33, 0x44)]
    [InlineData("#1234", 0x11, 0x22, 0x33, 0x44)]
    public void CssHexAlphaIsRgbaRatherThanAvaloniaArgb(string source, byte red, byte green, byte blue, byte alpha)
    {
        var color = ThemeColor.Parse(source);
        Assert.Equal(red, color.R);
        Assert.Equal(green, color.G);
        Assert.Equal(blue, color.B);
        Assert.Equal(alpha, color.A);
    }

    [Fact]
    public async Task CompiledBuiltIns_MatchRendererContract()
    {
        var reader = new CompiledThemeReader();
        var packages = new List<ValidatedTheme>();
        foreach (var path in Directory.EnumerateFiles(BuiltInFolder, "*.compiled.json"))
        {
            var source = await File.ReadAllTextAsync(path);
            Assert.DoesNotContain("--theme-", source, StringComparison.Ordinal);
            Assert.DoesNotContain("/theme-assets/", source, StringComparison.Ordinal);
            packages.Add(await reader.ReadAsync(path));
        }

        Assert.Equal(ExpectedBuiltIns, packages.Select(x => x.Manifest.Id).OrderBy(x => x));
        Assert.Equal(packages.Count, packages.Select(x => x.Manifest.Id).Distinct().Count());
        Assert.All(packages, package =>
        {
            var compiled = Assert.IsType<CompiledTheme>(package.Compiled);
            Assert.Equal(CompiledThemeContract.Controls.Count, compiled.Controls.Count);
            Assert.All(CompiledThemeContract.Controls.Keys, id => Assert.True(compiled.Controls.ContainsKey(id), id));
            Assert.NotNull(compiled.FocusRing);
            Assert.Equal(false, compiled.Controls["input"].States!["focused"].ShowRing);
            Assert.True(ThemeMetrics.HasValue(
                compiled.Controls["input"].States!["focused"].BorderThickness));

        });

        var aurelia = packages.Single(x => x.Manifest.Id == "aurelia").Compiled!;
        Assert.Equal("Asset", aurelia.Controls["panel"].Shape);
        Assert.Contains("id=\"0_0\"", aurelia.Controls["panel"].Art);
        Assert.NotNull(aurelia.Controls["carousel.nav"].Image);
        Assert.NotNull(aurelia.Controls["pip"].States!["selected"].Image);

        var anlucia = packages.Single(x => x.Manifest.Id == "anlucia").Compiled!;
        Assert.NotNull(anlucia.Controls["play-ornament"].Image);

        var kyururu = packages.Single(x => x.Manifest.Id == "kyururu").Compiled!;
        Assert.NotNull(kyururu.Controls["play-ornament"].Image);
        Assert.Equal(-0.2, kyururu.Controls["titlebar.wordmark"].Text!.LetterSpacing);

        var estella = packages.Single(x => x.Manifest.Id == "estella").Compiled!;
        Assert.Equal(0.3, estella.Controls["tab"].Text!.LetterSpacing);
        var duston = packages.Single(x => x.Manifest.Id == "duston").Compiled!;
        Assert.Equal(0.8, duston.Controls["input.label"].Text!.LetterSpacing);
    }

    [Fact]
    public async Task MissingControl_IsRejected()
    {
        var source = await File.ReadAllTextAsync(Path.Combine(BuiltInFolder, "estella.compiled.json"));
        var target = Path.Combine(_temp.Path, "missing.compiled.json");
        await File.WriteAllTextAsync(target, source.Replace("\"play-row\": {", "\"not-play-row\": {"));

        var error = await Assert.ThrowsAsync<ThemePackageException>(() => new CompiledThemeReader().ReadAsync(target));
        Assert.Contains("missing control 'play-row'", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExternalSvgReference_IsRejected()
    {
        var source = await File.ReadAllTextAsync(Path.Combine(BuiltInFolder, "aurelia.compiled.json"));
        var target = Path.Combine(_temp.Path, "unsafe.compiled.json");
        await File.WriteAllTextAsync(target, source.Replace("<circle cx=", "<image href=\\\"https://example.invalid/a.svg\\\"/><circle cx=", StringComparison.Ordinal));

        var error = await Assert.ThrowsAsync<ThemePackageException>(() => new CompiledThemeReader().ReadAsync(target));
        Assert.Contains("external reference", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OutOfRangeColor_IsRejectedInsteadOfClamped()
    {
        var target = await ModifiedTheme("estella", "bad-color", controls =>
            controls["input.error"]!["content"] = "rgba(999, 20, 30, 1.2)");

        var error = await Assert.ThrowsAsync<ThemePackageException>(() => new CompiledThemeReader().ReadAsync(target));
        Assert.Contains("invalid color", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MalformedEdgeArray_IsRejected()
    {
        var target = await ModifiedTheme("estella", "bad-edges", controls =>
            controls["input"]!["borderThickness"] = new JsonArray(1, 2, 3));

        var error = await Assert.ThrowsAsync<ThemePackageException>(() => new CompiledThemeReader().ReadAsync(target));
        Assert.Contains("borderThickness", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StateCannotOverrideStructuralFields()
    {
        var target = await ModifiedTheme("estella", "bad-state", controls =>
            controls["tab"]!["states"]!["selected"]!["radius"] = 4);

        var error = await Assert.ThrowsAsync<ThemePackageException>(() => new CompiledThemeReader().ReadAsync(target));
        Assert.Contains("states cannot override", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ShowRing_IsOnlyValidInsideFocusedState()
    {
        var target = await ModifiedTheme("estella", "bad-show-ring", controls =>
            controls["button"]!["states"]!["hover"]!["showRing"] = false);

        var error = await Assert.ThrowsAsync<ThemePackageException>(() => new CompiledThemeReader().ReadAsync(target));
        Assert.Contains("cannot declare showRing", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SuppressedFocusRing_RequiresAVisibleFocusedOverride()
    {
        var target = await ModifiedTheme("estella", "invisible-focus", controls =>
            controls["input"]!["states"]!["focused"] = new JsonObject
            {
                ["showRing"] = false
            });

        var error = await Assert.ThrowsAsync<ThemePackageException>(() => new CompiledThemeReader().ReadAsync(target));
        Assert.Contains("requires a visible", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GlobalFocusRingStyle_CannotDisableTheIndicatorWithNull()
    {
        var target = await ModifiedThemeRoot("estella", "null-focus-ring", root =>
            root["focusRing"] = null);

        var error = await Assert.ThrowsAsync<ThemePackageException>(() => new CompiledThemeReader().ReadAsync(target));
        Assert.Contains("focus ring style is required", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DuplicateNineSliceCell_IsRejected()
    {
        var target = await ModifiedTheme("aurelia", "bad-slices", controls =>
        {
            var panel = controls["panel"]!.AsObject();
            panel["art"] = panel["art"]!.GetValue<string>()
                .Replace("id=\"1_0\"", "id=\"0_0\"", StringComparison.Ordinal);
        });

        var error = await Assert.ThrowsAsync<ThemePackageException>(() => new CompiledThemeReader().ReadAsync(target));
        Assert.Contains("duplicate slice", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnsafeFileNameCannotBecomeAThemeId()
    {
        var source = await File.ReadAllTextAsync(Path.Combine(BuiltInFolder, "estella.compiled.json"));
        var target = Path.Combine(_temp.Path, "Unsafe_ID.compiled.json");
        await File.WriteAllTextAsync(target, source);

        var error = await Assert.ThrowsAsync<ThemePackageException>(() => new CompiledThemeReader().ReadAsync(target));
        Assert.Contains("safe ID", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("rgba(56, 120, 170, 0.08)", 20)]
    [InlineData("transparent", 0)]
    [InlineData("#a83232", 255)]
    public void CompiledColors_AreParsed(string source, byte expectedAlpha)
    {
        Assert.Equal(expectedAlpha, ThemeColor.Parse(source).A);
    }

    [Theory]
    [InlineData("rgb(256, 0, 0)")]
    [InlineData("rgba(0, 0, 0, 1.01)")]
    public void OutOfRangeCompiledColors_AreRejected(string source) =>
        Assert.Throws<FormatException>(() => ThemeColor.Parse(source));

    private async Task<string> ModifiedTheme(string sourceId, string targetId, Action<JsonObject> modify)
        => await ModifiedThemeRoot(sourceId, targetId, root => modify(root["controls"]!.AsObject()));

    private async Task<string> ModifiedThemeRoot(string sourceId, string targetId, Action<JsonObject> modify)
    {
        var source = await File.ReadAllTextAsync(Path.Combine(BuiltInFolder, $"{sourceId}.compiled.json"));
        var root = JsonNode.Parse(source)!.AsObject();
        modify(root);
        var target = Path.Combine(_temp.Path, $"{targetId}.compiled.json");
        await File.WriteAllTextAsync(target, root.ToJsonString());
        return target;
    }
}
