using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Galapa.Core.Configuration;
using Galapa.Launcher.Views.Controls;
using Microsoft.Extensions.Logging;

namespace Galapa.Launcher.Theming;

public sealed class ThemeManager(
    IThemeCatalog catalog,
    ThemePipeline pipeline,
    Settings settings,
    ISettingsPersistence persistence,
    ILogger<ThemeManager> logger) : IThemeManager
{
    private static readonly ThemeId DefaultId = new(Settings.DefaultThemeId);
    private readonly SemaphoreSlim _applyLock = new(1, 1);

    public AppliedTheme? ActiveTheme { get; private set; }

    public Task<ThemeApplyResult> ApplyAsync(ThemeId themeId, bool persist = true,
        CancellationToken cancellationToken = default)
    {
        var entry = catalog.Find(themeId);
        return entry is null
            ? Task.FromResult(new ThemeApplyResult(ThemeApplyStatus.NotFound, ErrorDiagnostics:
                [new ThemeDiagnostic(ThemeDiagnosticStage.Application, "theme.not-found",
                    $"Theme '{themeId.Value}' was not found.")]))
            : ApplyAsync(entry, persist, cancellationToken);
    }

    public async Task<ThemeApplyResult> ApplyAsync(ThemeCatalogEntry entry, bool persist = true,
        CancellationToken cancellationToken = default)
    {
        await _applyLock.WaitAsync(cancellationToken);
        try
        {
            if (entry.State is InvalidTheme)
                return new ThemeApplyResult(ThemeApplyStatus.Invalid, entry);
            if (entry.State is LoadFailedTheme)
                return new ThemeApplyResult(ThemeApplyStatus.LoadFailed, entry);

            var loadedState = await pipeline.LoadAsync(entry, cancellationToken);
            if (loadedState is InvalidTheme)
                return new ThemeApplyResult(ThemeApplyStatus.Invalid, entry);
            if (loadedState is LoadFailedTheme)
                return new ThemeApplyResult(ThemeApplyStatus.LoadFailed, entry);
            var loaded = loadedState as LoadedTheme ?? (loadedState as AppliedTheme)?.Loaded;
            if (loaded is null)
                return new ThemeApplyResult(ThemeApplyStatus.LoadFailed, entry, loadedState.Diagnostics);

            var previous = ActiveTheme;
            var previousThemeId = settings.ThemeId;
            AppliedTheme applied;
            try
            {
                applied = Dispatcher.UIThread.CheckAccess()
                    ? Install(entry, loaded, previous)
                    : await Dispatcher.UIThread.InvokeAsync(() => Install(entry, loaded, previous));
            }
            catch (Exception exception)
            {
                var diagnostic = new ThemeDiagnostic(ThemeDiagnosticStage.Application,
                    "theme.application.failed", exception.Message);
                logger.LogError(exception, "Theme {ThemeId} could not be applied; the active theme was retained",
                    loaded.Manifest.Id);
                return new ThemeApplyResult(ThemeApplyStatus.ApplyFailed, entry, [diagnostic]);
            }

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (persist)
                {
                    settings.ThemeId = loaded.Manifest.Id;
                    await persistence.SaveAsync(settings, cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                }
                ActiveTheme = applied;
                return new ThemeApplyResult(ThemeApplyStatus.Applied, entry);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                settings.ThemeId = previousThemeId;
                await RollBackAsync(entry, applied, previous);
                throw;
            }
            catch (Exception exception)
            {
                settings.ThemeId = previousThemeId;
                await RollBackAsync(entry, applied, previous);
                logger.LogError(exception, "Theme {ThemeId} was rolled back because settings could not be saved",
                    loaded.Manifest.Id);
                return new ThemeApplyResult(ThemeApplyStatus.PersistenceFailed, entry,
                    [new ThemeDiagnostic(ThemeDiagnosticStage.Persistence, "theme.persistence.failed", exception.Message)]);
            }
        }
        finally { _applyLock.Release(); }
    }

    public async Task<ThemeApplyResult> ApplyInitialAsync(ThemeId themeId,
        CancellationToken cancellationToken = default)
    {
        var selected = await ApplyAsync(themeId, persist: false, cancellationToken);
        if (selected.Succeeded) return selected;
        if (themeId == DefaultId) return await RecoverDefaultForStartupAsync(selected, cancellationToken);
        var fallback = await ApplyAsync(DefaultId, persist: false, cancellationToken);
        return fallback.Succeeded ? fallback : await RecoverDefaultForStartupAsync(fallback, cancellationToken);
    }

    private async Task<ThemeApplyResult> RecoverDefaultForStartupAsync(ThemeApplyResult failed,
        CancellationToken cancellationToken)
    {
        if (failed.Status is not (ThemeApplyStatus.LoadFailed or ThemeApplyStatus.ApplyFailed) ||
            failed.Entry is null ||
            !await catalog.RecoverDefaultAsync(failed.Entry, failed.Diagnostics, cancellationToken))
            return failed;
        return await ApplyAsync(failed.Entry, persist: false, cancellationToken);
    }

    private AppliedTheme Install(ThemeCatalogEntry entry, LoadedTheme loaded, AppliedTheme? previous)
    {
        Dispatcher.UIThread.VerifyAccess();
        var app = Application.Current ?? throw new InvalidOperationException("Avalonia application is not initialized.");
        var dictionary = BuildResources(loaded);
        var variant = loaded.Manifest.BaseVariant == ThemeBaseVariant.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        var oldVariant = app.RequestedThemeVariant;
        var entryState = entry.State;
        var entries = catalog.Themes;
        var activeStates = entries.Select(catalogEntry => catalogEntry.IsActive).ToArray();
        var previousEntry = previous is null ? null : catalog.Find(new ThemeId(previous.Manifest.Id));
        var previousEntryState = previousEntry?.State;
        app.Resources.MergedDictionaries.Add(dictionary);
        try
        {
            app.RequestedThemeVariant = variant;
            if (previous is not null) app.Resources.MergedDictionaries.Remove(previous.Dictionary);
            if (previous is not null && !ReferenceEquals(previous.Discovery, entry.State.Discovery))
            {
                if (previousEntry is not null) previousEntry.State = previous.Loaded;
            }
            var applied = new AppliedTheme(loaded, dictionary, variant);
            entry.State = applied;
            foreach (var catalogEntry in catalog.Themes) catalogEntry.IsActive = ReferenceEquals(catalogEntry, entry);
            ActiveTheme = applied;
            return applied;
        }
        catch
        {
            app.Resources.MergedDictionaries.Remove(dictionary);
            if (previous is not null && !app.Resources.MergedDictionaries.Contains(previous.Dictionary))
                app.Resources.MergedDictionaries.Add(previous.Dictionary);
            app.RequestedThemeVariant = oldVariant;
            entry.State = entryState;
            if (previousEntry is not null && previousEntryState is not null)
                previousEntry.State = previousEntryState;
            for (var index = 0; index < entries.Count; index++) entries[index].IsActive = activeStates[index];
            ActiveTheme = previous;
            throw;
        }
    }

    private async Task RollBackAsync(ThemeCatalogEntry entry, AppliedTheme installed, AppliedTheme? previous)
    {
        void RollBack()
        {
            var app = Application.Current ?? throw new InvalidOperationException("Avalonia application is not initialized.");
            app.Resources.MergedDictionaries.Remove(installed.Dictionary);
            if (previous is not null && !app.Resources.MergedDictionaries.Contains(previous.Dictionary))
                app.Resources.MergedDictionaries.Add(previous.Dictionary);
            app.RequestedThemeVariant = previous?.BaseVariant ?? ThemeVariant.Default;
            entry.State = installed.Loaded;
            if (previous is not null)
            {
                var previousEntry = catalog.Find(new ThemeId(previous.Manifest.Id));
                if (previousEntry is not null) previousEntry.State = previous;
            }
            foreach (var catalogEntry in catalog.Themes)
                catalogEntry.IsActive = previous is not null && catalogEntry.Id?.Value == previous.Manifest.Id;
            ActiveTheme = previous;
        }
        if (Dispatcher.UIThread.CheckAccess()) RollBack();
        else await Dispatcher.UIThread.InvokeAsync(RollBack);
    }

    internal static ResourceDictionary BuildResources(LoadedTheme loaded)
    {
        Dispatcher.UIThread.VerifyAccess();
        var theme = loaded.Compiled;
        var id = new ThemeId(loaded.Manifest.Id);
        var inheritedContent = loaded.Resources.Controls["window"].Visual(ThemePartState.Normal).Content ?? Brushes.Black;
        var dictionary = new ResourceDictionary
        {
            ["Galapa.Theme.Id"] = id.Value,
            ["Galapa.Theme.DisplayName"] = theme.Label
        };

        foreach (var (controlId, presentation) in loaded.Resources.Controls)
        {
            var control = presentation.Control;
            var normalized = presentation.Normalized;
            var normal = presentation.Visual(ThemePartState.Normal);
            dictionary[$"Galapa.Part.{controlId}"] = presentation;
            dictionary[$"Galapa.Part.{controlId}.ShowFocusRing"] =
                control.States?.GetValueOrDefault("focused")?.ShowRing != false;
            AddVisualResources(dictionary, $"Galapa.Part.{controlId}", normal, inheritedContent);
            if (control.States is not null)
                foreach (var stateName in control.States.Keys)
                {
                    var state = CompiledThemeContract.PartState(stateName);
                    var visual = presentation.Visual(state);
                    AddVisualResources(dictionary, $"Galapa.Part.{controlId}.{stateName}", visual, inheritedContent);
                    var source = control.States[stateName].Image ?? control.Image;
                    if (source is not null && loaded.Resources.Documents.TryGetValue(source, out var stateImage))
                        dictionary[$"Galapa.Image.{controlId}.{stateName}"] = stateImage;
                }
            if (control.Image is not null && loaded.Resources.Documents.TryGetValue(control.Image, out var image))
                dictionary[$"Galapa.Image.{controlId}"] = image;
            if (control.Images is not null)
                foreach (var (variantName, source) in control.Images)
                    dictionary[$"Galapa.Image.{controlId}.{variantName}"] = loaded.Resources.Documents[source];
            if (control.Size?.Width is { } width) dictionary[$"Galapa.Metric.{controlId}.Width"] = width;
            if (control.Size?.Height is { } height) dictionary[$"Galapa.Metric.{controlId}.Height"] = height;
            if (ThemeMetrics.HasValue(control.Padding))
                dictionary[$"Galapa.Metric.{controlId}.Padding"] = new Thickness(
                    normalized.Padding.Left, normalized.Padding.Top, normalized.Padding.Right, normalized.Padding.Bottom);
            AddControlTypography(dictionary, theme, controlId, control.Text, id);
            if (control.LeftInset is { } leftInset)
                dictionary[$"Galapa.Metric.{controlId}.LeftInset"] = leftInset;
        }

        dictionary["Galapa.Focus.Brush"] = ThemePaint.Brush(loaded.Validation.FocusRingColor)!;
        dictionary["Galapa.Focus.Width"] = new Thickness(theme.FocusRing.Width);
        dictionary["Galapa.Focus.Margin"] = new Thickness(-theme.FocusRing.Offset);
        return dictionary;
    }

    private static void AddVisualResources(ResourceDictionary dictionary, string prefix,
        ThemePartVisual visual, IBrush inheritedContent)
    {
        dictionary[$"{prefix}.ContentBrush"] = visual.ContentInherited
            ? inheritedContent
            : visual.Content ?? inheritedContent;
        dictionary[$"{prefix}.FillBrush"] = visual.Fill ?? Brushes.Transparent;
        dictionary[$"{prefix}.BorderBrush"] = visual.Border ?? Brushes.Transparent;
        dictionary[$"{prefix}.BorderThickness"] = new Thickness(
            visual.BorderEdges[3], visual.BorderEdges[0], visual.BorderEdges[1], visual.BorderEdges[2]);
    }

    private static void AddControlTypography(ResourceDictionary dictionary, CompiledTheme theme,
        string controlId, CompiledTextStyle? text, ThemeId themeId)
    {
        var defaults = CompiledThemeContract.Controls[controlId].Typography ??
                       new TypographyDefaults("titlebar.wordmark", 16, 600);
        var family = text?.Family ?? theme.Controls[defaults.FamilySourceControlId].Text?.Family ?? "Inter";
        var prefix = $"Galapa.Type.Control.{controlId}";
        dictionary[$"{prefix}.Family"] = FontFamilyFor(themeId, family);
        dictionary[$"{prefix}.Size"] = text?.Size ?? defaults.Size;
        dictionary[$"{prefix}.Weight"] = (FontWeight)(text?.Weight ?? defaults.Weight);
        dictionary[$"{prefix}.Style"] = ThemeTypography.ToFontStyle(text?.Style);
        dictionary[$"{prefix}.LetterSpacing"] = text?.LetterSpacing ?? 0d;
        dictionary[$"{prefix}.Transform"] = ThemeTypography.ToTransform(text?.Case);
    }

    internal static FontFamily FontFamilyFor(ThemeId themeId, string family) =>
        new(ThemeLocations.FontFamilyName(themeId, family));
}
