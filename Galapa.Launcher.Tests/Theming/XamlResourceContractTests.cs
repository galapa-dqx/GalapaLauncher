using System.Xml.Linq;
using System.Text.RegularExpressions;
using Galapa.Launcher.Theming;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.VisualTree;
using Galapa.Launcher.Views.Controls;

namespace Galapa.Launcher.Tests.Theming;

[Collection(SkiaRenderingCollection.Name)]
public sealed class XamlResourceContractTests(SkiaHeadlessFixture skia)
{
    private static string LauncherRoot => TestPaths.LauncherProjectRoot;

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
    public async Task DisabledFieldFrameDoesNotFocusItsEditor() =>
        Assert.False(await skia.ClickThemedFieldFrameAsync(isEnabled: false));

    [Fact]
    public async Task ReadOnlyFieldFrameMayFocusItsEditor() =>
        Assert.True(await skia.ClickThemedFieldFrameAsync(isReadOnly: true));

    [Fact]
    public async Task DisabledFieldRoutesToTheCompiledDisabledState()
    {
        var state = await skia.DispatchAsync(() =>
        {
            _ = SkiaHeadlessFixture.EnsureThemeStyles();
            var field = new ThemedField { Label = "Username", Width = 240, IsEnabled = false };
            return SkiaHeadlessFixture.WithWindow(field, new Size(280, 100), _ =>
                field.GetVisualDescendants().OfType<NotchedThemePart>().Single().State);
        });

        Assert.Equal(ThemePartState.Disabled, state);
    }

    [Fact]
    public async Task BareButtonTextReceivesCompiledButtonTypography()
    {
        var typography = await skia.DispatchAsync(() =>
        {
            using var appState = ApplicationStateScope.Capture();
            var app = SkiaHeadlessFixture.EnsureThemeStyles();
            app.Resources["Galapa.Part.button"] = ThemePartPresentation.Create(new CompiledControl { Shape = "Path" });
            app.Resources["Galapa.Part.button.ShowFocusRing"] = false;
            app.Resources["Galapa.Type.Control.button.Family"] = FontFamily.Default;
            app.Resources["Galapa.Type.Control.button.Size"] = 23d;
            app.Resources["Galapa.Type.Control.button.Weight"] = FontWeight.Bold;
            app.Resources["Galapa.Type.Control.button.Style"] = FontStyle.Italic;
            app.Resources["Galapa.Type.Control.button.LetterSpacing"] = 1.5d;
            app.Resources["Galapa.Type.Control.button.Transform"] = "Original";
            var label = new TextBlock { Text = "Save" };
            var button = new Button { Content = label };
            button.Classes.Add("themed");
            return SkiaHeadlessFixture.WithWindow(button, new Size(160, 60), _ =>
                (label.FontSize, label.FontWeight, label.FontStyle, label.LetterSpacing));
        });

        Assert.Equal((23d, FontWeight.Bold, FontStyle.Italic, 1.5d), typography);
    }

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

        var referencedIds = resourceValues.SelectMany(value =>
                Regex.Matches(value, @"Galapa\.Type\.Control\.([a-z0-9.-]+)\.")
                    .Select(match => match.Groups[1].Value))
            .Distinct(StringComparer.Ordinal);
        Assert.All(referencedIds, id => Assert.Contains(id, CompiledThemeContract.Controls.Keys));
    }

    [Fact]
    public void NativeCaptionHitTestingIsDeclaredInMarkupWithoutManualDragHandling()
    {
        var path = Path.Combine(LauncherRoot, "Views", "MainWindow.axaml");
        var document = XDocument.Load(path);
        var titleBar = document.Descendants().Single(element =>
            element.Name.LocalName == "ThemePart" && element.Attribute("Name")?.Value == "PART_TitleBar");
        Assert.Contains(titleBar.Attributes(), attribute =>
            attribute.Name.LocalName.EndsWith("NonClientHitTestResult", StringComparison.Ordinal) &&
            attribute.Value == "Caption");
        var tabStrip = titleBar.Descendants().Single(element => element.Name.LocalName == "TabStrip");
        Assert.Contains(tabStrip.Attributes(), attribute =>
            attribute.Name.LocalName.EndsWith("NonClientHitTestResult", StringComparison.Ordinal) &&
            attribute.Value == "Client");

        var codeBehind = File.ReadAllText(Path.ChangeExtension(path, ".axaml.cs"));
        Assert.DoesNotContain("BeginMoveDrag", codeBehind, StringComparison.Ordinal);
        Assert.DoesNotContain("PointerPressedEvent", codeBehind, StringComparison.Ordinal);
    }

    [Fact]
    public void SwitchPositionUsesARealTransition()
    {
        var styles = XDocument.Load(Path.Combine(LauncherRoot, "Styles", "CompiledThemeStyles.axaml"));
        Assert.Contains(styles.Descendants(), element =>
            element.Name.LocalName == "DoubleTransition" && element.Attribute("Property")?.Value == "Position");
    }
}

internal static class XamlResourceContractScenarios
{
    public static Task<bool> ConstructMainWindowAsync(this SkiaHeadlessFixture skia) =>
        skia.DispatchAsync(() =>
        {
            _ = SkiaHeadlessFixture.EnsureThemeStyles();
            var window = new Galapa.Launcher.Views.MainWindow(true);
            try { return window.FindControl<ThemePart>("PART_TitleBar") is not null; }
            finally { window.Close(); }
        });

    public static Task<bool> SettingListItemsStretchAsync(this SkiaHeadlessFixture skia) =>
        skia.DispatchAsync(() =>
        {
            _ = SkiaHeadlessFixture.EnsureThemeStyles();
            var item = new Button { Height = 24, Content = "Item" };
            item.Classes.Add("themed");
            item.Classes.Add("setting-row");
            var items = new ItemsControl
            {
                Width = 350,
                Height = 100,
                ItemsSource = new[] { "Item" },
                ItemTemplate = new FuncDataTemplate<string>((_, _) => item)
            };
            items.Classes.Add("stretch-items");
            return SkiaHeadlessFixture.WithWindow(items, new Size(350, 100), _ => item.Bounds.Width > 300);
        });

    public static Task<(bool EditorFocused, bool RingVisible, ThemePartState Before, ThemePartState After,
        double[] BorderEdges)> InspectThemedFieldFocusAsync(this SkiaHeadlessFixture skia) =>
        skia.DispatchAsync(() =>
        {
            _ = SkiaHeadlessFixture.EnsureThemeStyles();
            var field = new ThemedField { Label = "Username", Width = 240 };
            field.Resources["Galapa.Part.input.ShowFocusRing"] = false;
            return SkiaHeadlessFixture.WithWindow(field, new Size(280, 100), window =>
            {
                var frame = field.GetVisualDescendants().OfType<NotchedThemePart>().Single();
                var ring = field.GetVisualDescendants().OfType<ThemeFocusRing>().Single();
                var editor = field.GetVisualDescendants().OfType<TextBox>().Single();
                frame.PartStyle = ThemePartPresentation.Create(new CompiledControl
                {
                    Shape = "Path",
                    BorderThickness = System.Text.Json.JsonSerializer.SerializeToElement(1),
                    States = new Dictionary<string, CompiledControl>(StringComparer.Ordinal)
                    {
                        ["focused"] = new()
                        {
                            BorderThickness = System.Text.Json.JsonSerializer.SerializeToElement(2)
                        }
                    }
                });
                var before = frame.State;
                editor.Focus();
                window.UpdateLayout();
                return (editor.IsFocused, ring.IsVisible, before, frame.State,
                    frame.PartStyle.Visual(frame.State).BorderEdges);
            });
        });

    public static Task<bool> ClickThemedFieldFrameAsync(this SkiaHeadlessFixture skia, bool isEnabled = true,
        bool isReadOnly = false) => skia.DispatchAsync(() =>
    {
        _ = SkiaHeadlessFixture.EnsureThemeStyles();
        var field = new ThemedField
        {
            Label = "Username",
            Width = 240,
            IsEnabled = isEnabled,
            IsReadOnly = isReadOnly
        };
        return SkiaHeadlessFixture.WithWindow(field, new Size(280, 100), window =>
        {
            var editor = field.FindControl<TextBox>("PART_Editor")!;
            var origin = field.TranslatePoint(default, window) ?? default;
            var point = origin + new Vector(2, 19);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            return editor.IsFocused;
        });
    });
}
