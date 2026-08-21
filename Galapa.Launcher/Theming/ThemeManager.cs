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
            var loaded = loadedState.LoadedState;
            if (loaded is null)
                return new ThemeApplyResult(ThemeApplyStatus.LoadFailed, entry, loadedState.Diagnostics);

            InstalledTheme installed;
            try
            {
                installed = Dispatcher.UIThread.CheckAccess()
                    ? Install(entry, loaded)
                    : await Dispatcher.UIThread.InvokeAsync(() => Install(entry, loaded));
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
                return new ThemeApplyResult(ThemeApplyStatus.Applied, entry);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await RestoreAsync(installed.Snapshot);
                throw;
            }
            catch (Exception exception)
            {
                await RestoreAsync(installed.Snapshot);
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
        if (themeId == ThemeId.Default) return await RecoverDefaultForStartupAsync(selected, cancellationToken);
        var fallback = await ApplyAsync(ThemeId.Default, persist: false, cancellationToken);
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

    private InstalledTheme Install(ThemeCatalogEntry entry, LoadedTheme loaded)
    {
        Dispatcher.UIThread.VerifyAccess();
        var app = Application.Current ?? throw new InvalidOperationException("Avalonia application is not initialized.");
        var snapshot = Capture(app);
        var dictionary = BuildResources(loaded);
        var variant = loaded.Manifest.BaseVariant == ThemeBaseVariant.Dark ? ThemeVariant.Dark : ThemeVariant.Light;
        app.Resources.MergedDictionaries.Add(dictionary);
        try
        {
            app.RequestedThemeVariant = variant;
            if (snapshot.ActiveTheme is not null) app.Resources.MergedDictionaries.Remove(snapshot.ActiveTheme.Dictionary);
            if (snapshot.ActiveTheme is not null && !ReferenceEquals(snapshot.ActiveTheme.Discovery, entry.State.Discovery))
            {
                var previousEntry = catalog.Find(new ThemeId(snapshot.ActiveTheme.Manifest.Id));
                if (previousEntry is not null) previousEntry.State = snapshot.ActiveTheme.Loaded;
            }
            var applied = new AppliedTheme(loaded, dictionary, variant);
            entry.State = applied;
            foreach (var catalogEntry in catalog.Themes) catalogEntry.IsActive = ReferenceEquals(catalogEntry, entry);
            ActiveTheme = applied;
            return new InstalledTheme(snapshot);
        }
        catch
        {
            Restore(snapshot);
            throw;
        }
    }

    private InstallSnapshot Capture(Application app) => new(
        app.Resources.MergedDictionaries.ToArray(),
        app.RequestedThemeVariant,
        catalog.Themes.Select(entry => new CatalogEntrySnapshot(entry, entry.State, entry.IsActive)).ToArray(),
        ActiveTheme,
        settings.ThemeId);

    private async Task RestoreAsync(InstallSnapshot snapshot)
    {
        if (Dispatcher.UIThread.CheckAccess()) Restore(snapshot);
        else await Dispatcher.UIThread.InvokeAsync(() => Restore(snapshot));
    }

    private void Restore(InstallSnapshot snapshot)
    {
        Dispatcher.UIThread.VerifyAccess();
        var app = Application.Current ?? throw new InvalidOperationException("Avalonia application is not initialized.");
        app.Resources.MergedDictionaries.Clear();
        foreach (var dictionary in snapshot.Dictionaries) app.Resources.MergedDictionaries.Add(dictionary);
        app.RequestedThemeVariant = snapshot.Variant;
        foreach (var item in snapshot.Entries)
        {
            item.Entry.State = item.State;
            item.Entry.IsActive = item.IsActive;
        }
        ActiveTheme = snapshot.ActiveTheme;
        settings.ThemeId = snapshot.SettingsId;
    }

    private sealed record InstalledTheme(InstallSnapshot Snapshot);
    private sealed record InstallSnapshot(IReadOnlyList<IResourceProvider> Dictionaries, ThemeVariant? Variant,
        IReadOnlyList<CatalogEntrySnapshot> Entries, AppliedTheme? ActiveTheme, string SettingsId);
    private sealed record CatalogEntrySnapshot(ThemeCatalogEntry Entry, ThemeState State, bool IsActive);

    internal static ResourceDictionary BuildResources(LoadedTheme loaded)
    {
        Dispatcher.UIThread.VerifyAccess();
        var theme = loaded.Compiled;
        var inheritedContent = loaded.Resources.Controls["window"].Visual(ThemePartState.Normal).Content ?? Brushes.Black;
        var dictionary = new ResourceDictionary
        {
            ["Galapa.Theme.Id"] = loaded.Manifest.Id,
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
            foreach (var (stateName, state) in CompiledThemeContract.StateNames)
            {
                var visual = presentation.Visual(state);
                AddVisualResources(dictionary, $"Galapa.Part.{controlId}.{stateName}", visual, inheritedContent);
                var source = control.States?.GetValueOrDefault(stateName)?.Image ?? control.Image;
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
                dictionary[$"Galapa.Metric.{controlId}.Padding"] = normalized.Padding.ToThickness();
            AddControlTypography(dictionary, controlId, presentation.Typography);
            if ((control.LeftInset ?? CompiledThemeContract.Controls[controlId].DefaultLeftInset) is { } leftInset)
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
        dictionary[$"{prefix}.Opacity"] = visual.Opacity;
    }

    private static void AddControlTypography(ResourceDictionary dictionary, string controlId,
        ThemeTypographyPresentation typography)
    {
        var prefix = $"Galapa.Type.Control.{controlId}";
        dictionary[$"{prefix}.Family"] = typography.Family;
        dictionary[$"{prefix}.Size"] = typography.Size;
        dictionary[$"{prefix}.Weight"] = typography.Weight;
        dictionary[$"{prefix}.Style"] = typography.Style;
        dictionary[$"{prefix}.LetterSpacing"] = typography.LetterSpacing;
        dictionary[$"{prefix}.Transform"] = typography.Transform;
    }
}
