using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Galapa.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Galapa.Launcher.Theming;

public sealed class ThemeManager(IThemeCatalog catalog, Settings settings, ILogger<ThemeManager> logger) : IThemeManager
{
    private ResourceDictionary? _activeResources;
    public ThemePackage? ActiveTheme { get; private set; }
    public event EventHandler<ThemePackage>? ThemeChanged;

    public async Task<bool> ApplyAsync(string themeId, bool persist = true, CancellationToken cancellationToken = default)
    {
        var package = catalog.Find(themeId);
        if (package is null)
        {
            logger.LogWarning("Theme {ThemeId} was not found; retaining {ActiveTheme}", themeId, ActiveTheme?.Manifest.Id);
            return false;
        }

        try
        {
            var resources = BuildResources(package);
            void ApplyResources()
            {
                var app = Application.Current ?? throw new InvalidOperationException("Avalonia application is not initialized.");
                if (_activeResources is not null) app.Resources.MergedDictionaries.Remove(_activeResources);
                app.Resources.MergedDictionaries.Add(resources);
                app.RequestedThemeVariant = package.Manifest.BaseVariant == ThemeBaseVariant.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
                _activeResources = resources;
                ActiveTheme = package;
            }

            if (Dispatcher.UIThread.CheckAccess())
                ApplyResources();
            else
                await Dispatcher.UIThread.InvokeAsync(ApplyResources);

            if (persist)
            {
                settings.ThemeId = package.Manifest.Id;
                settings.Save();
            }
            ThemeChanged?.Invoke(this, package);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Theme {ThemeId} could not be applied; the active theme was retained", themeId);
            return false;
        }
    }

    private static ResourceDictionary BuildResources(ThemePackage package)
    {
        if (package.Compiled is not null)
            return BuildCompiledResources(package);

        var m = package.Manifest;
        var d = new ResourceDictionary
        {
            ["Galapa.Theme.Id"] = m.Id,
            ["Galapa.Theme.DisplayName"] = m.DisplayName,
            ["Galapa.Brush.Background"] = Brush.Parse(m.Colors.Background),
            ["Galapa.Brush.Surface"] = Brush.Parse(m.Colors.Surface),
            ["Galapa.Brush.SecondarySurface"] = Brush.Parse(m.Colors.SecondarySurface),
            ["Galapa.Brush.Border"] = Brush.Parse(m.Colors.Border),
            ["Galapa.Brush.Text"] = Brush.Parse(m.Colors.Text),
            ["Galapa.Brush.Muted"] = Brush.Parse(m.Colors.Muted),
            ["Galapa.Brush.Accent"] = Brush.Parse(m.Colors.Accent),
            ["Galapa.Brush.Success"] = Brush.Parse(m.Colors.Success),
            ["Galapa.Brush.Danger"] = Brush.Parse(m.Colors.Danger),
            ["Galapa.Font.Heading"] = new FontFamily($"fonts:{m.Id}#{m.Fonts.Heading.Family}"),
            ["Galapa.Font.Body"] = new FontFamily($"fonts:{m.Id}#{m.Fonts.Body.Family}")
        };

        foreach (var (role, token) in m.Typography)
        {
            var prefix = $"Galapa.Type.{role}";
            var family = token.Family == ThemeFontSource.Heading ? m.Fonts.Heading.Family : m.Fonts.Body.Family;
            d[$"{prefix}.Family"] = new FontFamily($"fonts:{m.Id}#{family}");
            d[$"{prefix}.Size"] = (token.Family == ThemeFontSource.Heading ? m.Fonts.HeadingBaseSize : m.Fonts.BodyBaseSize) * token.SizeScale;
            d[$"{prefix}.Weight"] = (FontWeight)token.Weight;
            d[$"{prefix}.Style"] = token.Style switch { ThemeFontStyle.Italic => FontStyle.Italic, ThemeFontStyle.Oblique => FontStyle.Oblique, _ => FontStyle.Normal };
            d[$"{prefix}.LetterSpacing"] = token.LetterSpacing;
            // ThemedTextBlock lives in Galapa.UI and deliberately accepts a string so the
            // shared control library does not depend on launcher theme-model types.
            d[$"{prefix}.Transform"] = token.Transform.ToString();
        }

        foreach (var (role, token) in m.Geometry)
        {
            d[$"Galapa.Geometry.{role}.Radius"] = new CornerRadius(token.Shape is ThemeShapeVariant.Pill or ThemeShapeVariant.Circle ? 999 : token.Radius);
            d[$"Galapa.Geometry.{role}.BorderThickness"] = new Thickness(token.BorderThickness);
            d[$"Galapa.Geometry.{role}.Shape"] = token.Shape;
        }

        // The app-owned ThemedSvg bridge forwards these dynamic resource strings into
        // Svg.Path after XAML initialization, so package artwork remains live-switchable.
        foreach (var (role, path) in package.Assets)
            d[$"Galapa.Asset.{role}"] = path;
        return d;
    }

    private static ResourceDictionary BuildCompiledResources(ThemePackage package)
    {
        var theme = package.Compiled!;
        var m = package.Manifest;
        var colors = m.Colors;
        var d = new ResourceDictionary
        {
            ["Galapa.Theme.Id"] = m.Id,
            ["Galapa.Theme.DisplayName"] = theme.Label,
            ["Galapa.Brush.Background"] = ThemePaint.Brush(colors.Background)!,
            ["Galapa.Brush.Surface"] = ThemePaint.Brush(colors.Surface)!,
            ["Galapa.Brush.SecondarySurface"] = ThemePaint.Brush(colors.SecondarySurface)!,
            ["Galapa.Brush.Border"] = ThemePaint.Brush(colors.Border)!,
            ["Galapa.Brush.Text"] = ThemePaint.Brush(colors.Text)!,
            ["Galapa.Brush.Muted"] = ThemePaint.Brush(colors.Muted)!,
            ["Galapa.Brush.Accent"] = ThemePaint.Brush(colors.Accent)!,
            ["Galapa.Brush.Success"] = ThemePaint.Brush(colors.Success)!,
            ["Galapa.Brush.Danger"] = ThemePaint.Brush(colors.Danger)!,
            ["Galapa.Font.Heading"] = FontFamilyFor(m.Fonts.Heading.Family),
            ["Galapa.Font.Body"] = FontFamilyFor(m.Fonts.Body.Family)
        };

        foreach (var (id, control) in theme.Controls)
        {
            d[$"Galapa.Part.{id}"] = control;
            var inheritedContent = ThemePaint.Brush(colors.Text)!;
            d[$"Galapa.Part.{id}.ContentBrush"] = ThemePaint.Brush(control.Content, inheritedContent) ?? inheritedContent;
            d[$"Galapa.Part.{id}.FillBrush"] = ThemePaint.Brush(control.Fill) ?? Brushes.Transparent;
            d[$"Galapa.Part.{id}.BorderBrush"] = ThemePaint.Brush(control.BorderColor) ?? Brushes.Transparent;
            if (control.States is not null)
                foreach (var (state, stateControl) in control.States)
                {
                    d[$"Galapa.Part.{id}.{state}.ContentBrush"] = ThemePaint.Brush(stateControl.Content ?? control.Content, inheritedContent) ?? inheritedContent;
                    d[$"Galapa.Part.{id}.{state}.FillBrush"] = ThemePaint.Brush(stateControl.Fill ?? control.Fill) ?? Brushes.Transparent;
                    d[$"Galapa.Part.{id}.{state}.BorderBrush"] = ThemePaint.Brush(stateControl.BorderColor ?? control.BorderColor) ?? Brushes.Transparent;
                    var stateEdges = ThemeMetrics.ReadEdges(
                        stateControl.BorderThickness.ValueKind is not (System.Text.Json.JsonValueKind.Undefined or System.Text.Json.JsonValueKind.Null)
                            ? stateControl.BorderThickness
                            : control.BorderThickness);
                    d[$"Galapa.Part.{id}.{state}.BorderThickness"] = new Thickness(stateEdges[3], stateEdges[0], stateEdges[1], stateEdges[2]);
                    if (stateControl.Image is not null) d[$"Galapa.Image.{id}.{state}"] = stateControl.Image;
                }
            if (control.Image is not null) d[$"Galapa.Image.{id}"] = control.Image;
            if (control.Images is not null)
                foreach (var (variant, image) in control.Images)
                    d[$"Galapa.Image.{id}.{variant}"] = image;
            if (control.Size?.Width is { } width) d[$"Galapa.Metric.{id}.Width"] = width;
            if (control.Size?.Height is { } height) d[$"Galapa.Metric.{id}.Height"] = height;
            if (control.Padding.ValueKind is not (System.Text.Json.JsonValueKind.Undefined or System.Text.Json.JsonValueKind.Null))
            {
                var edges = ThemeMetrics.ReadEdges(control.Padding);
                d[$"Galapa.Metric.{id}.Padding"] = new Thickness(edges[3], edges[0], edges[1], edges[2]);
            }
            if (control.Text is not null)
                AddControlTypography(d, id, control.Text, m);
            if (control.LeftInset is { } leftInset)
                d[$"Galapa.Metric.{id}.LeftInset"] = leftInset;
        }

        var switchTrack = theme.Controls["switch.track"];
        var switchThumb = theme.Controls["switch.thumb"];
        var trackWidth = switchTrack.Size?.Width ?? 34;
        var thumbWidth = switchThumb.Size?.Width ?? 13;
        var switchPadding = ThemeMetrics.ReadEdges(switchTrack.Padding);
        var switchInnerWidth = Math.Max(thumbWidth, trackWidth - switchPadding[1] - switchPadding[3]);
        d["Galapa.Metric.switch.InnerWidth"] = switchInnerWidth;
        d["Galapa.Metric.switch.Travel"] = Math.Max(0, switchInnerWidth - thumbWidth);

        var roleControls = new Dictionary<string, (string Control, double FloorSize, string FloorFamily, int FloorWeight)>
        {
            ["brand"] = ("titlebar.wordmark", 20, m.Fonts.Heading.Family, 600),
            ["navigation"] = ("tab", 16, m.Fonts.Heading.Family, 600),
            ["sectionHeading"] = ("settings.heading", 16, m.Fonts.Heading.Family, 600),
            ["fieldLabel"] = ("input.label", 12, m.Fonts.Heading.Family, 600),
            ["button"] = ("button", 16, m.Fonts.Heading.Family, 600),
            ["cardTitle"] = ("news-item", 16, m.Fonts.Heading.Family, 600),
            ["body"] = ("setting-help.body", 14, m.Fonts.Body.Family, 400),
            ["input"] = ("input", 16, m.Fonts.Body.Family, 400),
            ["settingValue"] = ("setting-row", 14, m.Fonts.Heading.Family, 600),
            ["metadata"] = ("news-item.date", 13, m.Fonts.Body.Family, 400)
        };
        foreach (var (role, source) in roleControls)
            AddTypography(d, role, theme.Controls[source.Control].Text, source.FloorSize, source.FloorFamily, source.FloorWeight);

        AddGeometry(d, "panel", theme.Controls["panel"]);
        AddGeometry(d, "card", theme.Controls["news-item"]);
        AddGeometry(d, "input", theme.Controls["input"]);
        AddGeometry(d, "button", theme.Controls["button"]);
        AddGeometry(d, "iconButton", theme.Controls["carousel.nav"]);
        AddGeometry(d, "switch", theme.Controls["switch.track"]);
        AddGeometry(d, "scrollbarTrack", theme.Controls["scrollbar.track"]);
        AddGeometry(d, "scrollbarThumb", theme.Controls["scrollbar.thumb"]);
        AddGeometry(d, "indicator", theme.Controls["pip"]);

        if (theme.FocusRing is { } focus)
        {
            d["Galapa.Focus.Brush"] = ThemePaint.Brush(focus.Color)!;
            d["Galapa.Focus.Width"] = new Thickness(focus.Width);
            d["Galapa.Focus.Offset"] = focus.Offset;
        }
        else
        {
            d["Galapa.Focus.Brush"] = Brushes.Transparent;
            d["Galapa.Focus.Width"] = new Thickness(0);
            d["Galapa.Focus.Offset"] = 0d;
        }
        return d;
    }

    private static void AddControlTypography(ResourceDictionary d, string id, CompiledTextStyle text,
        ThemeManifest manifest)
    {
        var bodyRole = id is "input" or "news-item.date" or "setting-help.body" or "input.placeholder";
        var prefix = $"Galapa.Type.Control.{id}";
        d[$"{prefix}.Family"] = FontFamilyFor(text.Family ??
            (bodyRole ? manifest.Fonts.Body.Family : manifest.Fonts.Heading.Family));
        d[$"{prefix}.Size"] = text.Size ?? (bodyRole ? manifest.Fonts.BodyBaseSize : manifest.Fonts.HeadingBaseSize);
        d[$"{prefix}.Weight"] = (FontWeight)(text.Weight ?? (bodyRole ? 400 : 600));
        d[$"{prefix}.Style"] = text.Style switch
        {
            "italic" => FontStyle.Italic,
            "oblique" => FontStyle.Oblique,
            _ => FontStyle.Normal
        };
        d[$"{prefix}.Transform"] = text.Case switch
        {
            "uppercase" => "Upper",
            "lowercase" => "Lower",
            "capitalize" => "Title",
            _ => "Original"
        };
    }

    private static void AddTypography(ResourceDictionary d, string role, CompiledTextStyle? text,
        double floorSize, string floorFamily, int floorWeight)
    {
        var prefix = $"Galapa.Type.{role}";
        d[$"{prefix}.Family"] = FontFamilyFor(text?.Family ?? floorFamily);
        d[$"{prefix}.Size"] = text?.Size ?? floorSize;
        d[$"{prefix}.Weight"] = (FontWeight)(text?.Weight ?? floorWeight);
        d[$"{prefix}.Style"] = text?.Style switch
        {
            "italic" => FontStyle.Italic,
            "oblique" => FontStyle.Oblique,
            _ => FontStyle.Normal
        };
        d[$"{prefix}.LetterSpacing"] = 0d;
        d[$"{prefix}.Transform"] = text?.Case switch
        {
            "uppercase" => "Upper",
            "lowercase" => "Lower",
            "capitalize" => "Title",
            _ => "Original"
        };
    }

    private static void AddGeometry(ResourceDictionary d, string role, CompiledControl control)
    {
        var radius = control.Radius.ValueKind == System.Text.Json.JsonValueKind.String ? 999d :
            control.Radius.ValueKind == System.Text.Json.JsonValueKind.Number ? control.Radius.GetDouble() : 0d;
        var edges = ThemeMetrics.ReadEdges(control.BorderThickness);
        d[$"Galapa.Geometry.{role}.Radius"] = new CornerRadius(radius);
        d[$"Galapa.Geometry.{role}.BorderThickness"] = new Thickness(edges[3], edges[0], edges[1], edges[2]);
        d[$"Galapa.Geometry.{role}.Shape"] = control.Shape == "Asset" ? ThemeShapeVariant.Asset :
            control.Radius.ValueKind == System.Text.Json.JsonValueKind.String ? ThemeShapeVariant.Pill :
            control.Corner is "bevel" or "notch" or "scoop" ? ThemeShapeVariant.Angular : ThemeShapeVariant.Rounded;
    }

    internal static FontFamily FontFamilyFor(string family) => family switch
    {
        "Playfair Display" => new FontFamily("fonts:estella#Playfair Display"),
        "Source Sans 3" => new FontFamily("fonts:estella#Source Sans 3"),
        "Crimson Pro" => new FontFamily("fonts:duston#Crimson Pro"),
        "Space Grotesk" => new FontFamily("fonts:kyururu#Space Grotesk"),
        _ => new FontFamily(family)
    };
}
