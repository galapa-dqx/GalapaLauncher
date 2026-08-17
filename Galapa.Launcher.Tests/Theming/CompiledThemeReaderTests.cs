using System.Reflection;
using Avalonia.Controls;
using Avalonia.Media;
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
    private static string BuiltInFolder => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "Galapa.Launcher", "Assets", "Themes"));

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task CompiledBuiltIns_MatchRendererContract()
    {
        var reader = new CompiledThemeReader();
        var packages = new List<ThemePackage>();
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
            Assert.Equal(CompiledThemeContract.ControlIds.Length, compiled.Controls.Count);
            Assert.All(CompiledThemeContract.ControlIds, id => Assert.True(compiled.Controls.ContainsKey(id), id));

            var buildResources = typeof(ThemeManager).GetMethod("BuildResources", BindingFlags.NonPublic | BindingFlags.Static);
            var resources = Assert.IsType<ResourceDictionary>(buildResources!.Invoke(null, [package]));
            Assert.Same(compiled.Controls["panel"], resources["Galapa.Part.panel"]);
            Assert.Equal(20, Assert.IsType<double>(resources["Galapa.Type.brand.Size"]));
            Assert.Equal(16, Assert.IsType<double>(resources["Galapa.Type.button.Size"]));
            Assert.IsAssignableFrom<IBrush>(resources["Galapa.Part.button.ContentBrush"]);
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

    [Theory]
    [InlineData("rgba(56, 120, 170, 0.08)", 20)]
    [InlineData("transparent", 0)]
    [InlineData("#a83232", 255)]
    public void CompiledColors_AreParsed(string source, byte expectedAlpha)
    {
        Assert.Equal(expectedAlpha, ThemePaint.ParseColor(source).A);
    }
}
