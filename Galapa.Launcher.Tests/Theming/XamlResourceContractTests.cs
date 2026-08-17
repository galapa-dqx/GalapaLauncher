using System.Xml.Linq;

namespace Galapa.Launcher.Tests.Theming;

public sealed class XamlResourceContractTests
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
}
