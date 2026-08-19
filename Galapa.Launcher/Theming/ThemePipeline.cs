using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Media;
using Avalonia.Threading;
using Galapa.Launcher.Views.Controls;
using Microsoft.Extensions.Logging;

namespace Galapa.Launcher.Theming;

public interface IThemeLoader
{
    LoadedTheme Load(ValidatedTheme theme);
}

public sealed class ThemeLoader : IThemeLoader
{
    public LoadedTheme Load(ValidatedTheme theme)
    {
        Dispatcher.UIThread.VerifyAccess();
        ThemeFontRegistrar.Validate(theme);
        var documents = theme.Assets.Documents.ToDictionary(
            pair => pair.Key, pair => InlineSvgDocument.Load(pair.Value), StringComparer.Ordinal);
        var slices = theme.Assets.NineSlices.ToDictionary(
            pair => pair.Key, pair => NineSliceSvg.Load(pair.Value), StringComparer.Ordinal);
        var controls = theme.Controls.ToDictionary(
            pair => pair.Key,
            pair => ThemePartPresentation.Load(pair.Value, slices, documents),
            StringComparer.Ordinal);
        return new LoadedTheme(theme, FontManager.Current,
            new LoadedThemeResources(controls, documents, slices));
    }
}

public sealed class ThemePipeline(
    CompiledThemeReader reader,
    IThemeLoader loader,
    ILogger<ThemePipeline> logger)
{
    private readonly ConcurrentDictionary<ThemeCatalogEntry, SemaphoreSlim> _transitionLocks = new();

    public async Task<ThemeState> ValidateAsync(ThemeCatalogEntry entry,
        CancellationToken cancellationToken = default)
    {
        var gate = _transitionLocks.GetOrAdd(entry, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (ValidationFrom(entry.State) is { } cached) return cached;
            if (entry.State is InvalidTheme) return entry.State;
            var discovery = entry.State.Discovery;
            await SetStateAsync(entry, new ValidatingTheme(discovery));
            try
            {
                var validated = await Task.Run(() => reader.ValidateAsync(discovery, cancellationToken),
                    cancellationToken);
                await SetStateAsync(entry, validated);
                return validated;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await SetStateAsync(entry, discovery);
                throw;
            }
            catch (Exception exception)
            {
                var invalid = new InvalidTheme(discovery, (exception as ThemePackageException)?.Metadata,
                    [Diagnostic(ThemeDiagnosticStage.Validation, "theme.validation.failed", exception)]);
                logger.LogError(exception, "Theme source {ThemeSource} failed validation", discovery.Source.Description);
                await SetStateAsync(entry, invalid);
                return invalid;
            }
        }
        finally { gate.Release(); }
    }

    public async Task<ThemeState> LoadAsync(ThemeCatalogEntry entry,
        CancellationToken cancellationToken = default)
    {
        var validatedState = await ValidateAsync(entry, cancellationToken);
        if (validatedState is InvalidTheme) return validatedState;
        var validated = ValidationFrom(validatedState)!;
        var gate = _transitionLocks.GetOrAdd(entry, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (entry.State is LoadedTheme loaded && await BelongsToCurrentApplicationAsync(loaded))
                return loaded;
            if (entry.State is AppliedTheme applied && await BelongsToCurrentApplicationAsync(applied.Loaded))
                return applied.Loaded;
            if (entry.State is LoadFailedTheme) return entry.State;
            await SetStateAsync(entry, new LoadingTheme(validated));
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var next = Dispatcher.UIThread.CheckAccess()
                    ? loader.Load(validated)
                    : await Dispatcher.UIThread.InvokeAsync(() => loader.Load(validated));
                cancellationToken.ThrowIfCancellationRequested();
                await SetStateAsync(entry, next);
                return next;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await SetStateAsync(entry, validated);
                throw;
            }
            catch (Exception exception)
            {
                var failed = new LoadFailedTheme(validated,
                    [Diagnostic(ThemeDiagnosticStage.Loading, "theme.loading.failed", exception)]);
                logger.LogError(exception, "Theme {ThemeId} failed to load", validated.Manifest.Id);
                await SetStateAsync(entry, failed);
                return failed;
            }
        }
        finally { gate.Release(); }
    }

    internal async Task<ValidatedTheme> RecoverAsync(ThemeCatalogEntry entry, ValidatedTheme recovery,
        IReadOnlyList<ThemeDiagnostic> deployedDiagnostics, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var visibleDiscovery = entry.State.Discovery;
        var recovered = recovery with
        {
            Previous = visibleDiscovery,
            Warnings = deployedDiagnostics.Concat(recovery.Diagnostics).ToArray()
        };
        await SetStateAsync(entry, recovered);
        return recovered;
    }

    internal static async Task SetStateAsync(ThemeCatalogEntry entry, ThemeState state)
    {
        if (Application.Current is null || Dispatcher.UIThread.CheckAccess()) entry.State = state;
        else await Dispatcher.UIThread.InvokeAsync(() => entry.State = state);
    }

    private static ValidatedTheme? ValidationFrom(ThemeState state) => state switch
    {
        ValidatedTheme validated => validated,
        LoadingTheme loading => loading.Validation,
        LoadedTheme loaded => loaded.Validation,
        LoadFailedTheme failed => failed.Validation,
        AppliedTheme applied => applied.Loaded.Validation,
        _ => null
    };

    private static async Task<bool> BelongsToCurrentApplicationAsync(LoadedTheme theme) =>
        Dispatcher.UIThread.CheckAccess()
            ? ReferenceEquals(theme.FontManager, FontManager.Current)
            : await Dispatcher.UIThread.InvokeAsync(() => ReferenceEquals(theme.FontManager, FontManager.Current));

    private static ThemeDiagnostic Diagnostic(ThemeDiagnosticStage stage, string code, Exception exception) =>
        new(stage, code, exception.Message);
}
