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

            // Parse immutable SVG/path assets before entering the UI-thread
            // transaction. A theme switch then only swaps prepared objects and
            // resources; hover/render paths never discover or parse artwork.
            if (!ThemeRenderAssets.IsPrepared(package))
                await Task.Run(() => ThemeRenderAssets.Prepare(package), cancellationToken).ConfigureAwait(false);

            ResourceDictionary resources = null!;
            var previousResources = _activeResources;
            var previousTheme = ActiveTheme;
            var previousThemeId = settings.ThemeId;
            ThemeVariant? previousVariant = null;

            void InstallNext()
            {
                var app = Application.Current ?? throw new InvalidOperationException("Avalonia application is not initialized.");
                ThemeFontRegistrar.Validate(package);
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

    public async Task<bool> ApplyInitialAsync(string themeId, CancellationToken cancellationToken = default)
    {
        if (await ApplyAsync(themeId, persist: false, cancellationToken)) return true;
        if (!await ApplyAsync(Settings.DefaultThemeId, persist: false, cancellationToken)) return false;

        settings.ThemeId = Settings.DefaultThemeId;
        try { settings.Save(); }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The recovery theme was applied, but its selection could not be persisted");
        }
        return true;
    }

    internal static ResourceDictionary BuildResources(ThemePackage package)
    {
        var theme = package.Compiled;
        var m = package.Manifest;
        var window = theme.Controls["window"];
        var inheritedContent = ThemePaint.Brush(window.Content) ?? Brushes.Black;
        var familyNames = CompiledThemeContract.FontSources.ToDictionary(
            pair => pair.Key,
            pair => theme.Controls[pair.Value].Text?.Family ?? "Inter");
        var d = new ResourceDictionary
        {
            ["Galapa.Theme.Id"] = m.Id,
            ["Galapa.Theme.DisplayName"] = theme.Label,
            ["Galapa.Font.Body"] = FontFamilyFor(m.Id, familyNames[ThemeFontRole.Body])
        };

        foreach (var (id, control) in theme.Controls)
        {
            var normal = CompiledThemeContract.ResolveVisual(control);
            d[$"Galapa.Part.{id}"] = control;
            AddVisualResources(d, $"Galapa.Part.{id}", normal, inheritedContent);
            if (control.States is not null)
                foreach (var (state, stateControl) in control.States)
                {
                    var visual = CompiledThemeContract.ResolveVisual(control, stateControl);
                    AddVisualResources(d, $"Galapa.Part.{id}.{state}", visual, inheritedContent);
                    if (visual.Image is not null) d[$"Galapa.Image.{id}.{state}"] = visual.Image;
                }
            if (control.Image is not null) d[$"Galapa.Image.{id}"] = control.Image;
            if (control.Images is not null)
                foreach (var (variant, image) in control.Images)
                    d[$"Galapa.Image.{id}.{variant}"] = image;
            if (control.Size?.Width is { } width) d[$"Galapa.Metric.{id}.Width"] = width;
            if (control.Size?.Height is { } height) d[$"Galapa.Metric.{id}.Height"] = height;
            if (ThemeMetrics.HasValue(control.Padding))
            {
                d[$"Galapa.Metric.{id}.Padding"] = ThemeMetrics.ToThickness(control.Padding);
            }
            AddControlTypography(d, id, control.Text, m.Id, familyNames);
            if (control.LeftInset is { } leftInset)
                d[$"Galapa.Metric.{id}.LeftInset"] = leftInset;
        }

        foreach (var (role, source) in CompiledThemeContract.TypographyRoles)
        {
            var floorFamily = familyNames[source.FontRole];
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

    private static void AddVisualResources(ResourceDictionary dictionary, string prefix,
        ResolvedControlVisual visual, IBrush inheritedContent)
    {
        dictionary[$"{prefix}.ContentBrush"] = ThemePaint.Brush(visual.Content, inheritedContent) ?? inheritedContent;
        dictionary[$"{prefix}.FillBrush"] = ThemePaint.Brush(visual.Fill) ?? Brushes.Transparent;
        dictionary[$"{prefix}.BorderBrush"] = ThemePaint.Brush(visual.BorderColor) ?? Brushes.Transparent;
        dictionary[$"{prefix}.BorderThickness"] = ThemeMetrics.ToThickness(visual.BorderThickness);
    }

    private static void AddControlTypography(ResourceDictionary d, string id, CompiledTextStyle? text,
        string themeId, IReadOnlyDictionary<ThemeFontRole, string> familyNames)
    {
        var role = CompiledThemeContract.Controls[id].DefaultFont;
        var bodyRole = role == ThemeFontRole.Body;
        AddTypographyResources(d, $"Galapa.Type.Control.{id}", text, themeId, familyNames[role],
            bodyRole ? 14d : 16d, bodyRole ? 400 : 600);
    }

    private static void AddTypography(ResourceDictionary d, string role, CompiledTextStyle? text,
        double floorSize, string floorFamily, int floorWeight, string themeId)
    {
        AddTypographyResources(d, $"Galapa.Type.{role}", text, themeId, floorFamily, floorSize, floorWeight);
    }

    private static void AddTypographyResources(ResourceDictionary dictionary, string prefix, CompiledTextStyle? text,
        string themeId, string fallbackFamily, double fallbackSize, int fallbackWeight)
    {
        dictionary[$"{prefix}.Family"] = FontFamilyFor(themeId, text?.Family ?? fallbackFamily);
        dictionary[$"{prefix}.Size"] = text?.Size ?? fallbackSize;
        dictionary[$"{prefix}.Weight"] = (FontWeight)(text?.Weight ?? fallbackWeight);
        dictionary[$"{prefix}.Style"] = ThemeTypography.ToFontStyle(text?.Style);
        dictionary[$"{prefix}.LetterSpacing"] = text?.LetterSpacing ?? 0d;
        dictionary[$"{prefix}.Transform"] = ThemeTypography.ToTransform(text?.Case);
    }

    internal static FontFamily FontFamilyFor(string themeId, string family) =>
        new(ThemeLocations.FontFamilyName(themeId, family));
}
