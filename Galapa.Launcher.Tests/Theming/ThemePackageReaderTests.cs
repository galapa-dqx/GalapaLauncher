using System.IO.Compression;
using System.Reflection;
using Avalonia.Controls;
using Galapa.Launcher.Theming;
using Galapa.TestUtilities;

namespace Galapa.Launcher.Tests.Theming;

public sealed class ThemePackageReaderTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private static string BuiltInFolder => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "Galapa.Launcher", "Assets", "Themes"));

    public void Dispose() => _temp.Dispose();

    [Fact]
    public async Task BuiltInPackages_AreValidAndHaveUniqueIds()
    {
        var reader = new ThemePackageReader();
        var packages = new List<ThemePackage>();
        foreach (var path in Directory.EnumerateFiles(BuiltInFolder, "*.galapatheme"))
            packages.Add(await reader.ReadAsync(path, Path.Combine(_temp.Path, "cache")));

        Assert.Equal(3, packages.Count);
        Assert.Equal(3, packages.Select(x => x.Manifest.Id).Distinct().Count());
        Assert.Equal(new[] { "duston", "estella", "kyururu" }, packages.Select(x => x.Manifest.Id).OrderBy(x => x));
        Assert.All(packages, package =>
        {
            Assert.Equal(1, package.Manifest.SchemaVersion);
            Assert.True(File.Exists(package.Resolve(package.Manifest.Fonts.Heading.File)));
            Assert.True(File.Exists(package.Resolve(package.Manifest.Fonts.Body.File)));
            Assert.All(package.Assets.Values, path => Assert.True(File.Exists(path), path));
            Assert.Equal(10, package.Manifest.Typography.Count);
            Assert.Equal(9, package.Manifest.Geometry.Count);
            Assert.Equal(16, package.Manifest.Fonts.HeadingBaseSize);
            Assert.Equal(13, package.Manifest.Fonts.BodyBaseSize);

            var buildResources = typeof(ThemeManager).GetMethod("BuildResources", BindingFlags.NonPublic | BindingFlags.Static);
            var resources = Assert.IsType<ResourceDictionary>(buildResources!.Invoke(null, [package]));
            Assert.Equal(20, Assert.IsType<double>(resources["Galapa.Type.brand.Size"]), 6);
            Assert.Equal(16, Assert.IsType<double>(resources["Galapa.Type.navigation.Size"]), 6);
            Assert.Equal(14, Assert.IsType<double>(resources["Galapa.Type.fieldLabel.Size"]), 6);
            Assert.Equal(13, Assert.IsType<double>(resources["Galapa.Type.body.Size"]), 6);
            Assert.Equal(16, Assert.IsType<double>(resources["Galapa.Type.input.Size"]), 6);
            Assert.Equal(14, Assert.IsType<double>(resources["Galapa.Type.settingValue.Size"]), 6);
            Assert.All(package.Manifest.Typography.Keys,
                role => Assert.IsType<string>(resources[$"Galapa.Type.{role}.Transform"]));
        });
    }

    [Fact]
    public async Task UnsafeArchivePath_IsRejected()
    {
        var path = Path.Combine(_temp.Path, "unsafe.galapatheme");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            zip.CreateEntry("../outside.txt");

        var error = await Assert.ThrowsAsync<ThemePackageException>(() =>
            new ThemePackageReader().ReadAsync(path, Path.Combine(_temp.Path, "cache")));
        Assert.Contains("Unsafe", error.Message);
    }

    [Fact]
    public async Task MalformedColor_IsRejected()
    {
        var source = Path.Combine(BuiltInFolder, "estella.galapatheme");
        var target = Path.Combine(_temp.Path, "bad-color.galapatheme");
        RewriteManifest(source, target, json => json.Replace("#EEF4F8", "not-a-color"));

        var error = await Assert.ThrowsAsync<ThemePackageException>(() =>
            new ThemePackageReader().ReadAsync(target, Path.Combine(_temp.Path, "cache")));
        Assert.Contains("Invalid theme color", error.Message);
    }

    [Fact]
    public async Task MissingManifestReference_IsRejected()
    {
        var source = Path.Combine(BuiltInFolder, "estella.galapatheme");
        var target = Path.Combine(_temp.Path, "missing-asset.galapatheme");
        RewriteManifest(source, target, json => json.Replace("assets/ornament-right.svg", "assets/absent.svg"));

        var error = await Assert.ThrowsAsync<ThemePackageException>(() =>
            new ThemePackageReader().ReadAsync(target, Path.Combine(_temp.Path, "cache")));
        Assert.Contains("Missing asset", error.Message);
    }

    private static void RewriteManifest(string sourcePath, string targetPath, Func<string, string> transform)
    {
        using var source = ZipFile.OpenRead(sourcePath);
        using var target = ZipFile.Open(targetPath, ZipArchiveMode.Create);
        foreach (var entry in source.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var copy = target.CreateEntry(entry.FullName, CompressionLevel.Optimal);
            using var input = entry.Open();
            using var output = copy.Open();
            if (entry.FullName == "theme.json")
            {
                using var reader = new StreamReader(input);
                using var writer = new StreamWriter(output);
                writer.Write(transform(reader.ReadToEnd()));
            }
            else input.CopyTo(output);
        }
    }
}
