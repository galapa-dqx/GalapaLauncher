using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Galapa.Launcher.Views.Controls;

namespace Galapa.Launcher.Theming;

public readonly record struct ThemeId
{
    public string Value { get; }

    public ThemeId(string value)
    {
        if (!TryParse(value, out var parsed))
            throw new ArgumentException("Theme IDs must contain 1-100 lowercase ASCII letters, digits, or hyphens.", nameof(value));
        Value = parsed.Value;
    }

    private ThemeId(string value, bool _) => Value = value;

    public static bool TryParse(string? value, out ThemeId id)
    {
        if (value is { Length: > 0 and <= 100 } &&
            value.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-'))
        {
            id = new ThemeId(value, true);
            return true;
        }
        id = default;
        return false;
    }

    public override string ToString() => Value ?? string.Empty;
}

public enum ThemeDiagnosticStage { Discovery, Validation, Loading, Application, Persistence }

public sealed record ThemeDiagnostic(
    ThemeDiagnosticStage Stage,
    string Code,
    string Message,
    string? Path = null);

public sealed record ThemeFontSourceDescriptor(Uri CollectionUri, Uri AssetsUri);

/// <summary>
/// A renderer-independent source boundary. Archive-backed packages can implement
/// this interface later without changing catalog, validation, or application.
/// </summary>
public interface IThemeSource
{
    string SourceId { get; }
    string FallbackDisplayName { get; }
    string Description { get; }
    IReadOnlyList<ThemeFontSourceDescriptor> Fonts { get; }
    ValueTask<Stream> OpenCompiledJsonAsync(CancellationToken cancellationToken = default);
}

public abstract record ThemeState
{
    public abstract DiscoveredTheme Discovery { get; }
    public virtual IReadOnlyList<ThemeDiagnostic> Diagnostics => [];
}

public sealed record DiscoveredTheme(IThemeSource Source, ThemeId? Id, string FallbackDisplayName)
    : ThemeState
{
    public override DiscoveredTheme Discovery => this;
}

public sealed record ValidatingTheme(DiscoveredTheme Previous) : ThemeState
{
    public override DiscoveredTheme Discovery => Previous;
}

public sealed record ValidatedTheme(
    DiscoveredTheme Previous,
    IThemeSource EffectiveSource,
    ThemeManifest Manifest,
    CompiledTheme Compiled,
    IReadOnlyDictionary<string, NormalizedCompiledControl> Controls,
    ThemeColor FocusRingColor,
    ValidatedThemeAssets Assets,
    IReadOnlyList<ThemeDiagnostic> Warnings) : ThemeState
{
    public override DiscoveredTheme Discovery => Previous;
    public override IReadOnlyList<ThemeDiagnostic> Diagnostics => Warnings;
}

public sealed record InvalidTheme(
    DiscoveredTheme Previous,
    ThemeManifest? Metadata,
    IReadOnlyList<ThemeDiagnostic> Errors) : ThemeState
{
    public override DiscoveredTheme Discovery => Previous;
    public override IReadOnlyList<ThemeDiagnostic> Diagnostics => Errors;
}

public sealed record LoadingTheme(ValidatedTheme Previous) : ThemeState
{
    public override DiscoveredTheme Discovery => Previous.Discovery;
    public ValidatedTheme Validation => Previous;
}

public sealed record LoadedTheme(
    ValidatedTheme Previous,
    FontManager FontManager,
    LoadedThemeResources Resources) : ThemeState
{
    public override DiscoveredTheme Discovery => Previous.Discovery;
    public ValidatedTheme Validation => Previous;
    public ThemeManifest Manifest => Previous.Manifest;
    public CompiledTheme Compiled => Previous.Compiled;
    public override IReadOnlyList<ThemeDiagnostic> Diagnostics => Previous.Diagnostics;
}

public sealed record LoadFailedTheme(
    ValidatedTheme Previous,
    IReadOnlyList<ThemeDiagnostic> Errors) : ThemeState
{
    public override DiscoveredTheme Discovery => Previous.Discovery;
    public ValidatedTheme Validation => Previous;
    public override IReadOnlyList<ThemeDiagnostic> Diagnostics => Errors;
}

public sealed record AppliedTheme(
    LoadedTheme Previous,
    ResourceDictionary Dictionary,
    ThemeVariant BaseVariant) : ThemeState
{
    public override DiscoveredTheme Discovery => Previous.Discovery;
    public LoadedTheme Loaded => Previous;
    public ThemeManifest Manifest => Previous.Manifest;
    public override IReadOnlyList<ThemeDiagnostic> Diagnostics => Previous.Diagnostics;
}

/// <summary>A stable binding object; State alone describes its current lifecycle.</summary>
public sealed class ThemeCatalogEntry : INotifyPropertyChanged
{
    private ThemeState _state;
    private bool _isActive;

    public ThemeCatalogEntry(DiscoveredTheme discovered) => _state = discovered;

    public ThemeState State
    {
        get => _state;
        internal set
        {
            if (ReferenceEquals(_state, value)) return;
            _state = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(State)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasError)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Diagnostics)));
        }
    }

    public ThemeId? Id => State.Discovery.Id;
    public string DisplayName => State switch
    {
        ValidatedTheme validated => validated.Manifest.DisplayName,
        LoadingTheme loading => loading.Validation.Manifest.DisplayName,
        LoadedTheme loaded => loaded.Manifest.DisplayName,
        LoadFailedTheme failed => failed.Validation.Manifest.DisplayName,
        AppliedTheme applied => applied.Manifest.DisplayName,
        InvalidTheme { Metadata: not null } invalid => invalid.Metadata.DisplayName,
        _ => State.Discovery.FallbackDisplayName
    };
    public string StatusText => State switch
    {
        DiscoveredTheme or ValidatingTheme => "Checking…",
        ValidatedTheme validated => validated.Manifest.BaseVariant.ToString(),
        LoadingTheme loading => loading.Validation.Manifest.BaseVariant.ToString(),
        LoadedTheme loaded => loaded.Manifest.BaseVariant.ToString(),
        AppliedTheme applied => applied.Manifest.BaseVariant.ToString(),
        InvalidTheme or LoadFailedTheme => "Error",
        _ => string.Empty
    };
    public bool HasError => State is InvalidTheme or LoadFailedTheme;
    public bool IsActive
    {
        get => _isActive;
        internal set
        {
            if (_isActive == value) return;
            _isActive = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsActive)));
        }
    }
    public IReadOnlyList<ThemeDiagnostic> Diagnostics => State.Diagnostics;

    public event PropertyChangedEventHandler? PropertyChanged;

}

public enum ThemeApplyStatus
{
    Applied,
    NotFound,
    Invalid,
    LoadFailed,
    ApplyFailed,
    PersistenceFailed
}

public sealed record ThemeApplyResult(
    ThemeApplyStatus Status,
    ThemeCatalogEntry? Entry = null,
    IReadOnlyList<ThemeDiagnostic>? ErrorDiagnostics = null)
{
    public bool Succeeded => Status == ThemeApplyStatus.Applied;
    public IReadOnlyList<ThemeDiagnostic> Diagnostics => ErrorDiagnostics ?? Entry?.Diagnostics ?? [];
}

public interface IThemeCatalog
{
    IReadOnlyList<ThemeCatalogEntry> Themes { get; }
    event EventHandler? ThemesChanged;
    ThemeCatalogEntry? Find(ThemeId id);
    Task InitializeAsync(ThemeId preferredThemeId, CancellationToken cancellationToken = default);
    Task ValidateRemainingAsync(CancellationToken cancellationToken = default);
    Task<bool> RecoverDefaultAsync(ThemeCatalogEntry entry, IReadOnlyList<ThemeDiagnostic> diagnostics,
        CancellationToken cancellationToken = default);
}

public interface IThemeManager
{
    AppliedTheme? ActiveTheme { get; }
    Task<ThemeApplyResult> ApplyAsync(ThemeId themeId, bool persist = true,
        CancellationToken cancellationToken = default);
    Task<ThemeApplyResult> ApplyAsync(ThemeCatalogEntry entry, bool persist = true,
        CancellationToken cancellationToken = default);
    Task<ThemeApplyResult> ApplyInitialAsync(ThemeId themeId, CancellationToken cancellationToken = default);
}

public sealed class ThemePackageException : Exception
{
    public ThemePackageException(string message, ThemeManifest? metadata = null, Exception? innerException = null)
        : base(message, innerException) => Metadata = metadata;

    public ThemeManifest? Metadata { get; }
}

public sealed record LoadedThemeResources(
    IReadOnlyDictionary<string, ThemePartPresentation> Controls,
    IReadOnlyDictionary<string, InlineSvgDocument> Documents,
    IReadOnlyDictionary<string, NineSliceSvg> NineSlices);
