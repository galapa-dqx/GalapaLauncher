using Galapa.Core.Configuration;
using Microsoft.Extensions.Logging;
using System.Security;

namespace Galapa.Launcher.Theming;

public sealed class ThemeCatalog : IThemeCatalog
{
    private readonly ThemePipeline _pipeline;
    private readonly ILogger<ThemeCatalog> _logger;
    private readonly Func<IReadOnlyList<IThemeSource>> _sourceFactory;
    private readonly IThemeSource _recoverySource;
    private readonly List<ThemeCatalogEntry> _themes = [];
    private readonly object _sync = new();
    private Task<ThemeState>? _recoveryPreparation;

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
        List<IThemeSource> sources;
        IReadOnlyList<ThemeDiagnostic> discoveryDiagnostics = [];
        try
        {
            sources = _sourceFactory().ToList();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException)
        {
            _logger.LogError(exception, "Built-in themes could not be discovered; embedded Estella recovery will be used");
            sources = [];
            discoveryDiagnostics =
            [
                new ThemeDiagnostic(ThemeDiagnosticStage.Discovery, "theme.discovery.failed", exception.Message,
                    ThemeLocations.BuiltInFolder)
            ];
        }
        if (!sources.Any(source => source.SourceId == ThemeId.Default.Value))
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
        foreach (var source in sources.OrderBy(source => source.SourceId == ThemeId.Default.Value ? 0 : 1)
                     .ThenBy(source => source.SourceId, StringComparer.Ordinal)
                     .ThenBy(source => source.Description, StringComparer.Ordinal))
        {
            var discovery = DiscoveredTheme.From(source);
            var id = discovery.Id;
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
        var estella = Find(ThemeId.Default)
                      ?? throw new ThemePackageException("The Estella catalog entry is missing.");
        if (!ReferenceEquals(estella.State.Discovery.Source, _recoverySource))
            _recoveryPreparation = _pipeline.ValidateDetachedAsync(DiscoveredTheme.From(_recoverySource), CancellationToken.None);

        var required = ReferenceEquals(estella, preferred)
            ? [_pipeline.ValidateAsync(estella, cancellationToken)]
            : new[] { _pipeline.ValidateAsync(preferred!, cancellationToken), _pipeline.ValidateAsync(estella, cancellationToken) };
        await Task.WhenAll(required);
        if (discoveryDiagnostics.Count > 0 && estella.State is ValidatedTheme recovered)
            await ThemePipeline.SetStateAsync(estella, recovered with
            {
                Warnings = recovered.Warnings.Concat(discoveryDiagnostics).ToArray()
            });

        if (estella.State is InvalidTheme invalid &&
            !ReferenceEquals(estella.State.Discovery.Source, _recoverySource))
        {
            _logger.LogWarning("Deployed Estella was invalid; using the embedded recovery source: {Diagnostic}",
                string.Join("; ", invalid.Diagnostics.Select(diagnostic => diagnostic.Message)));
            var recovery = await RecoveryAsync(cancellationToken);
            if (recovery is ValidatedTheme validated)
                await _pipeline.RecoverAsync(estella, validated, invalid.Diagnostics, cancellationToken);
            else if (recovery is not null)
                await ThemePipeline.SetStateAsync(estella, new InvalidTheme(estella.State.Discovery, invalid.Metadata,
                    invalid.Diagnostics.Concat(recovery.Diagnostics).ToArray()));
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
        if (entry.Id != ThemeId.Default || ReferenceEquals(entry.State.Validation?.EffectiveSource, _recoverySource)) return false;
        _logger.LogWarning("Deployed Estella could not be materialized; using the embedded recovery source: {Diagnostic}",
            string.Join("; ", diagnostics.Select(diagnostic => diagnostic.Message)));
        var recovery = await RecoveryAsync(cancellationToken);
        if (recovery is not ValidatedTheme validated)
        {
            _logger.LogError("Embedded Estella recovery is unavailable: {Diagnostic}",
                string.Join("; ", recovery?.Diagnostics.Select(diagnostic => diagnostic.Message) ?? []));
            return false;
        }
        await _pipeline.RecoverAsync(entry, validated, diagnostics, cancellationToken);
        ThemesChanged?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private async Task<ThemeState?> RecoveryAsync(CancellationToken cancellationToken)
    {
        if (_recoveryPreparation is null)
            _recoveryPreparation = _pipeline.ValidateDetachedAsync(DiscoveredTheme.From(_recoverySource), CancellationToken.None);
        return await _recoveryPreparation.WaitAsync(cancellationToken);
    }
}
