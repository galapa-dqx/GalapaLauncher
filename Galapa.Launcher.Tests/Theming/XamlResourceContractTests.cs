using System.Xml.Linq;

namespace Galapa.Launcher.Tests.Theming;

[Collection(SkiaRenderingCollection.Name)]
public sealed class XamlResourceContractTests(SkiaHeadlessFixture skia)
{
    private static readonly string LauncherRoot = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "Galapa.Launcher"));

    [Fact]
    public void ScalarTitleBarHeightIsNotAssignedToGridLengthProperties()
    {
        const string titleBarHeight = "{StaticResource Galapa.TitleBarHeight}";
        var offenders = Directory.EnumerateFiles(LauncherRoot, "*.axaml", SearchOption.AllDirectories)
            .SelectMany(path => XDocument.Load(path).Descendants()
                .Where(element => element.Name.LocalName == "RowDefinition"
                    && element.Attribute("Height")?.Value == titleBarHeight)
                .Select(_ => Path.GetRelativePath(LauncherRoot, path)))
            .ToArray();

        Assert.Empty(offenders);

        var mainWindow = XDocument.Load(Path.Combine(LauncherRoot, "Views", "MainWindow.axaml"));
        var titleBar = mainWindow.Descendants().Single(element =>
            element.Name.LocalName == "ThemePart" && element.Attribute("Name")?.Value == "PART_TitleBar");
        Assert.Equal(titleBarHeight, titleBar.Attribute("Height")?.Value);
    }

    [Fact]
    public async Task MainWindowCompiledXamlCanBeConstructed() =>
        Assert.True(await skia.ConstructMainWindowAsync());

    [Fact]
    public async Task DerivedSettingsShellReceivesSharedTemplate() =>
        Assert.True(await skia.DerivedSettingsShellReceivesSharedTemplateAsync());

    [Fact]
    public async Task SettingListItemsStretchAcrossContentColumn() =>
        Assert.True(await skia.SettingListItemsStretchAsync());

    [Fact]
    public void SwitchPositionUsesARealTransition()
    {
        var styles = XDocument.Load(Path.Combine(LauncherRoot, "Styles", "CompiledThemeStyles.axaml"));
        Assert.Contains(styles.Descendants(), element =>
            element.Name.LocalName == "DoubleTransition" && element.Attribute("Property")?.Value == "Position");
    }
}
