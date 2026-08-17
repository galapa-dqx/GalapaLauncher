using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Galapa.Core.Configuration;
using Galapa.Launcher.Views.Controls;
using Microsoft.Extensions.Logging;

namespace Galapa.Launcher.Theming;

public sealed class ThemeManager(IThemeCatalog catalog, Settings settings, ILogger<ThemeManager> logger) : IThemeManager
{
    private readonly SemaphoreSlim _applyLock = new(1, 1);
    private ResourceDictionary? _activeResources;
    public ThemePackage? ActiveTheme { get; private set; }

    public async Task<bool> ApplyAsync(string themeId, bool persist = true, CancellationToken cancellationToken = default)
    {
        await _applyLock.WaitAsync(cancellationToken);
        try
        {
            var package = catalog.Find(themeId);
            if (package is null)
            {
                logger.LogWarning("Theme {ThemeId} was not found; retaining {ActiveTheme}", themeId, ActiveTheme?.Manifest.Id);
                return false;
            }

            ResourceDictionary resources = null!;
            var previousResources = _activeResources;
            var previousTheme = ActiveTheme;
            var previousThemeId = settings.ThemeId;
            ThemeVariant? previousVariant = null;

            void InstallNext()
            {
                var app = Application.Current ?? throw new InvalidOperationException("Avalonia application is not initialized.");
                ThemeRenderAssets.Prepare(package);
                resources = BuildResources(package);
                previousVariant = app.RequestedThemeVariant;
                app.Resources.MergedDictionaries.Add(resources);
                try
                {
                    app.RequestedThemeVariant = package.Manifest.BaseVariant == ThemeBaseVariant.Dark
                        ? ThemeVariant.Dark
                        : ThemeVariant.Light;
                    if (previousResources is not null)
                        app.Resources.MergedDictionaries.Remove(previousResources);
                    _activeResources = resources;
                    ActiveTheme = package;
                }
                catch
                {
                    app.Resources.MergedDictionaries.Remove(resources);
                    app.RequestedThemeVariant = previousVariant;
                    throw;
                }
            }

            void RestorePrevious()
            {
                var app = Application.Current ?? throw new InvalidOperationException("Avalonia application is not initialized.");
                if (previousResources is not null && !app.Resources.MergedDictionaries.Contains(previousResources))
                    app.Resources.MergedDictionaries.Add(previousResources);
                app.RequestedThemeVariant = previousVariant ?? ThemeVariant.Default;
                app.Resources.MergedDictionaries.Remove(resources);
                _activeResources = previousResources;
                ActiveTheme = previousTheme;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (Dispatcher.UIThread.CheckAccess())
                InstallNext();
            else
                await Dispatcher.UIThread.InvokeAsync(InstallNext);

            try
            {
                if (persist)
                {
                    settings.ThemeId = package.Manifest.Id;
                    settings.Save();
                }
                return true;
            }
            catch
            {
                settings.ThemeId = previousThemeId;
                if (Dispatcher.UIThread.CheckAccess())
                    RestorePrevious();
                else
                    await Dispatcher.UIThread.InvokeAsync(RestorePrevious);
                throw;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Theme {ThemeId} could not be applied; the active theme was retained", themeId);
            return false;
        }
        finally
        {
            _applyLock.Release();
        }
    }

    internal static ResourceDictionary BuildResources(ThemePackage package)
    {
        var theme = package.Compiled;
        var m = package.Manifest;
        var window = theme.Controls["window"];
        var inheritedContent = ThemePaint.Brush(window.Content) ?? Brushes.Black;
        var headingFamily = theme.Controls["titlebar.wordmark"].Text?.Family ?? "Inter";
        var bodyFamily = theme.Controls["input"].Text?.Family ?? "Inter";
        var d = new ResourceDictionary
        {
            ["Galapa.Theme.Id"] = m.Id,
            ["Galapa.Theme.DisplayName"] = theme.Label,
            ["Galapa.Font.Heading"] = FontFamilyFor(m.Id, headingFamily),
            ["Galapa.Font.Body"] = FontFamilyFor(m.Id, bodyFamily)
        };

        foreach (var (id, control) in theme.Controls)
        {
            d[$"Galapa.Part.{id}"] = control;
            d[$"Galapa.Part.{id}.ContentBrush"] = ThemePaint.Brush(control.Content, inheritedContent) ?? inheritedContent;
            d[$"Galapa.Part.{id}.FillBrush"] = ThemePaint.Brush(control.Fill) ?? Brushes.Transparent;
            d[$"Galapa.Part.{id}.BorderBrush"] = ThemePaint.Brush(control.BorderColor) ?? Brushes.Transparent;
            d[$"Galapa.Part.{id}.BorderThickness"] = ThemeMetrics.ToThickness(control.BorderThickness);
            if (control.States is not null)
                foreach (var (state, stateControl) in control.States)
                {
                    d[$"Galapa.Part.{id}.{state}.ContentBrush"] = ThemePaint.Brush(stateControl.Content ?? control.Content, inheritedContent) ?? inheritedContent;
                    d[$"Galapa.Part.{id}.{state}.FillBrush"] = ThemePaint.Brush(stateControl.Fill ?? control.Fill) ?? Brushes.Transparent;
                    d[$"Galapa.Part.{id}.{state}.BorderBrush"] = ThemePaint.Brush(stateControl.BorderColor ?? control.BorderColor) ?? Brushes.Transparent;
                    var stateBorder = stateControl.BorderThickness.ValueKind is not (System.Text.Json.JsonValueKind.Undefined or System.Text.Json.JsonValueKind.Null)
                        ? stateControl.BorderThickness
                        : control.BorderThickness;
                    d[$"Galapa.Part.{id}.{state}.BorderThickness"] = ThemeMetrics.ToThickness(stateBorder);
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
                d[$"Galapa.Metric.{id}.Padding"] = ThemeMetrics.ToThickness(control.Padding);
            }
            AddControlTypography(d, id, control.Text, m.Id, headingFamily, bodyFamily);
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

        foreach (var (role, source) in CompiledThemeContract.TypographyRoles)
        {
            var floorFamily = source.FontRole == ThemeFontRole.Body ? bodyFamily : headingFamily;
            AddTypography(d, role, theme.Controls[source.ControlId].Text, source.FloorSize, floorFamily,
                source.FloorWeight, m.Id);
            d[$"Galapa.Type.{role}.ContentBrush"] = d[$"Galapa.Part.{source.ControlId}.ContentBrush"];
        }

        if (theme.FocusRing is { } focus)
        {
            d["Galapa.Focus.Brush"] = ThemePaint.Brush(focus.Color)!;
            d["Galapa.Focus.Width"] = new Thickness(focus.Width);
            d["Galapa.Focus.Margin"] = new Thickness(-focus.Offset);
        }
        else
        {
            d["Galapa.Focus.Brush"] = Brushes.Transparent;
            d["Galapa.Focus.Width"] = new Thickness(0);
            d["Galapa.Focus.Margin"] = new Thickness(0);
        }
        return d;
    }

    private static void AddControlTypography(ResourceDictionary d, string id, CompiledTextStyle? text,
        string themeId, string headingFamily, string bodyFamily)
    {
        var bodyRole = CompiledThemeContract.Controls[id].DefaultFont == ThemeFontRole.Body;
        var prefix = $"Galapa.Type.Control.{id}";
        d[$"{prefix}.Family"] = FontFamilyFor(themeId, text?.Family ?? (bodyRole ? bodyFamily : headingFamily));
        d[$"{prefix}.Size"] = text?.Size ?? (bodyRole ? 14d : 16d);
        d[$"{prefix}.Weight"] = (FontWeight)(text?.Weight ?? (bodyRole ? 400 : 600));
        d[$"{prefix}.Style"] = ThemeTypography.ToFontStyle(text?.Style);
        d[$"{prefix}.Transform"] = ThemeTypography.ToTransform(text?.Case);
    }

    private static void AddTypography(ResourceDictionary d, string role, CompiledTextStyle? text,
        double floorSize, string floorFamily, int floorWeight, string themeId)
    {
        var prefix = $"Galapa.Type.{role}";
        d[$"{prefix}.Family"] = FontFamilyFor(themeId, text?.Family ?? floorFamily);
        d[$"{prefix}.Size"] = text?.Size ?? floorSize;
        d[$"{prefix}.Weight"] = (FontWeight)(text?.Weight ?? floorWeight);
        d[$"{prefix}.Style"] = ThemeTypography.ToFontStyle(text?.Style);
        d[$"{prefix}.LetterSpacing"] = 0d;
        d[$"{prefix}.Transform"] = ThemeTypography.ToTransform(text?.Case);
    }

    internal static FontFamily FontFamilyFor(string themeId, string family) =>
        new($"fonts:{themeId}#{family}");
}
