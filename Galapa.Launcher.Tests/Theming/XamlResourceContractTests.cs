using System.Xml.Linq;

namespace Galapa.Launcher.Tests.Theming;

[Collection(SkiaRenderingCollection.Name)]
public sealed class XamlResourceContractTests(SkiaHeadlessFixture skia)
{
    private static readonly string LauncherRoot = Path.GetFullPath(Path.Combine(
        TestPaths.ProjectRoot, "..", "Galapa.Launcher"));

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
    public async Task ThemedFieldAppliesFocusedVisualToItsNotchedFrame()
    {
        var inspection = await skia.InspectThemedFieldFocusAsync();

        Assert.True(inspection.EditorFocused);
        Assert.False(inspection.RingVisible);
        Assert.Equal(Galapa.Launcher.Theming.ThemePartState.Normal, inspection.Before);
        Assert.Equal(Galapa.Launcher.Theming.ThemePartState.Focused, inspection.After);
        Assert.All(inspection.BorderEdges, edge => Assert.Equal(2, edge));
    }

    [Fact]
    public async Task ClickingThemedFieldPaddingFocusesItsEditor() =>
        Assert.True(await skia.ClickThemedFieldFrameAsync());

    [Fact]
    public void LoginViewsBindToGeneratedCommands()
    {
        var playerSelect = XDocument.Load(Path.Combine(LauncherRoot, "Views", "LoginFrame", "PlayerSelectPage.axaml"));
        Assert.Contains(playerSelect.Descendants().Attributes("Command"), attribute =>
            attribute.Value.Contains("SelectPlayerCommand", StringComparison.Ordinal));
        var loginFrame = XDocument.Load(Path.Combine(LauncherRoot, "Views", "LoginFrame", "LoginFrame.axaml"));
        Assert.Contains(loginFrame.Descendants().Attributes("Command"), attribute =>
            attribute.Value.Contains("ReturnToPlayerSelectCommand", StringComparison.Ordinal));
    }

    [Fact]
    public void SemanticTypographyStylesUseCompiledControlResourcesDirectly()
    {
        var themePath = Path.GetFullPath(Path.Combine(LauncherRoot, "..", "Galapa.UI", "Themes", "Launcher.axaml"));
        var resourceValues = Directory.EnumerateFiles(Path.GetFullPath(Path.Combine(LauncherRoot, "..")), "*.axaml",
                SearchOption.AllDirectories)
            .SelectMany(path => XDocument.Load(path).Descendants().Attributes()
                .Select(attribute => attribute.Value))
            .Where(value => value.Contains("Galapa.Type.", StringComparison.Ordinal))
            .ToArray();
        Assert.NotEmpty(resourceValues);
        Assert.All(resourceValues, value => Assert.Contains("Galapa.Type.Control.", value, StringComparison.Ordinal));

        var source = File.ReadAllText(themePath);
        foreach (var control in new[]
                 {
                     "titlebar.wordmark", "tab", "settings.heading", "input.label", "news-item",
                     "setting-help.body", "setting-row", "news-item.date"
                 })
            Assert.Contains($"Galapa.Type.Control.{control}.", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SwitchPositionUsesARealTransition()
    {
        var styles = XDocument.Load(Path.Combine(LauncherRoot, "Styles", "CompiledThemeStyles.axaml"));
        Assert.Contains(styles.Descendants(), element =>
            element.Name.LocalName == "DoubleTransition" && element.Attribute("Property")?.Value == "Position");
    }
}
