using Galapa.Core.Configuration;
using Microsoft.Extensions.Logging;

namespace Galapa.Launcher.Theming;

public sealed class ThemeCatalog : IThemeCatalog
{
    private static readonly ThemeId DefaultId = new(Settings.DefaultThemeId);
    private readonly ThemePipeline _pipeline;
    private readonly ILogger<ThemeCatalog> _logger;
    private readonly Func<IReadOnlyList<IThemeSource>> _sourceFactory;
    private readonly IThemeSource _recoverySource;
    private readonly List<ThemeCatalogEntry> _themes = [];
    private readonly object _sync = new();
    private ValidatedTheme? _recoveryValidation;
    private IReadOnlyList<ThemeDiagnostic> _recoveryDiagnostics = [];

    public ThemeCatalog(ThemePipeline pipeline, ILogger<ThemeCatalog> logger)
        : this(pipeline, logger, ThemeSourceDiscovery.BuiltIns, ThemeSourceDiscovery.Recovery()) { }

    internal ThemeCatalog(ThemePipeline pipeline, ILogger<ThemeCatalog> logger,
        Func<IReadOnlyList<IThemeSource>> sourceFactory, IThemeSource recoverySource)
    {
        _pipeline = pipeline;
        _logger = logger;
        _sourceFactory = sourceFactory;
        _recoverySource = recoverySource;
    }

    public IReadOnlyList<ThemeCatalogEntry> Themes
    {
        get { lock (_sync) return _themes.ToArray(); }
    }

    public event EventHandler? ThemesChanged;

    public ThemeCatalogEntry? Find(ThemeId id)
    {
        lock (_sync)
            return _themes.FirstOrDefault(entry => entry.Id == id);
    }

    public async Task InitializeAsync(ThemeId preferredThemeId,
        CancellationToken cancellationToken = default)
    {
        var sources = _sourceFactory().ToList();
        if (!sources.Any(source => source.SourceId == DefaultId.Value))
            sources.Add(_recoverySource);
        if (!sources.Any(source => source.SourceId == preferredThemeId.Value))
            sources.Add(new MissingThemeSource(preferredThemeId));

        var entries = new List<ThemeCatalogEntry>();
        var duplicateIds = sources
            .Select(source => ThemeId.TryParse(source.SourceId, out var parsed) ? parsed : (ThemeId?)null)
            .Where(id => id is not null)
            .GroupBy(id => id!.Value)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToHashSet();
        foreach (var source in sources.OrderBy(source => source.SourceId == DefaultId.Value ? 0 : 1)
                     .ThenBy(source => source.SourceId, StringComparer.Ordinal)
                     .ThenBy(source => source.Description, StringComparer.Ordinal))
        {
            var id = ThemeId.TryParse(source.SourceId, out var parsed) ? parsed : (ThemeId?)null;
            var discovery = new DiscoveredTheme(source, id, source.FallbackDisplayName);
            var entry = new ThemeCatalogEntry(discovery);
            if (id is null)
            {
                entry.State = new InvalidTheme(discovery, null,
                    [new ThemeDiagnostic(ThemeDiagnosticStage.Discovery, "theme.id.invalid",
                        $"Theme source ID '{source.SourceId}' is invalid.")]);
            }
            else if (duplicateIds.Contains(id.Value))
            {
                entry.State = new InvalidTheme(discovery, null,
                    [new ThemeDiagnostic(ThemeDiagnosticStage.Discovery, "theme.id.duplicate",
                        $"Theme ID '{id.Value}' is provided by more than one source.")]);
            }
            entries.Add(entry);
        }

        lock (_sync)
        {
            _themes.Clear();
            _themes.AddRange(entries);
        }
        ThemesChanged?.Invoke(this, EventArgs.Empty);

        var preferred = Find(preferredThemeId);
        if (preferred is not null) await _pipeline.ValidateAsync(preferred, cancellationToken);
        var estella = Find(DefaultId)
                      ?? throw new ThemePackageException("The Estella catalog entry is missing.");
        if (!ReferenceEquals(estella, preferred)) await _pipeline.ValidateAsync(estella, cancellationToken);
        await PrepareRecoveryAsync(estella, cancellationToken);

        if (estella.State is InvalidTheme invalid &&
            !ReferenceEquals(estella.State.Discovery.Source, _recoverySource))
        {
            _logger.LogWarning("Deployed Estella was invalid; using the embedded recovery source: {Diagnostic}",
                string.Join("; ", invalid.Diagnostics.Select(diagnostic => diagnostic.Message)));
            if (_recoveryValidation is not null)
                await _pipeline.RecoverAsync(estella, _recoveryValidation, invalid.Diagnostics, cancellationToken);
            else
                await ThemePipeline.SetStateAsync(estella, new InvalidTheme(estella.State.Discovery, invalid.Metadata,
                    invalid.Diagnostics.Concat(_recoveryDiagnostics).ToArray()));
        }
        ThemesChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task ValidateRemainingAsync(CancellationToken cancellationToken = default)
    {
        var remaining = Themes.Where(entry => entry.State is DiscoveredTheme).ToArray();
        await Task.WhenAll(remaining.Select(entry => _pipeline.ValidateAsync(entry, cancellationToken)));
        ThemesChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> RecoverDefaultAsync(ThemeCatalogEntry entry,
        IReadOnlyList<ThemeDiagnostic> diagnostics, CancellationToken cancellationToken = default)
    {
        if (entry.Id != DefaultId || ReferenceEquals(EffectiveSource(entry.State), _recoverySource)) return false;
        _logger.LogWarning("Deployed Estella could not be materialized; using the embedded recovery source: {Diagnostic}",
            string.Join("; ", diagnostics.Select(diagnostic => diagnostic.Message)));
        if (_recoveryValidation is null)
        {
            _logger.LogError("Embedded Estella recovery is unavailable: {Diagnostic}",
                string.Join("; ", _recoveryDiagnostics.Select(diagnostic => diagnostic.Message)));
            return false;
        }
        await _pipeline.RecoverAsync(entry, _recoveryValidation, diagnostics, cancellationToken);
        ThemesChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private async Task PrepareRecoveryAsync(ThemeCatalogEntry estella, CancellationToken cancellationToken)
    {
        if (ReferenceEquals(estella.State.Discovery.Source, _recoverySource))
        {
            _recoveryValidation = estella.State as ValidatedTheme;
            _recoveryDiagnostics = estella.Diagnostics;
            return;
        }
        var discovery = new DiscoveredTheme(_recoverySource, DefaultId, _recoverySource.FallbackDisplayName);
        var hiddenEntry = new ThemeCatalogEntry(discovery);
        var state = await _pipeline.ValidateAsync(hiddenEntry, cancellationToken);
        _recoveryValidation = state as ValidatedTheme;
        _recoveryDiagnostics = state.Diagnostics;
    }

    private static IThemeSource? EffectiveSource(ThemeState state) => state switch
    {
        ValidatedTheme validated => validated.EffectiveSource,
        LoadingTheme loading => loading.Validation.EffectiveSource,
        LoadedTheme loaded => loaded.Validation.EffectiveSource,
        LoadFailedTheme failed => failed.Validation.EffectiveSource,
        AppliedTheme applied => applied.Loaded.Validation.EffectiveSource,
        _ => null
    };
}
