using System.Text.Json;

namespace Galapa.Launcher.Theming;

public sealed class CompiledThemeReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<ValidatedTheme> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
            throw new ThemePackageException($"Compiled theme not found: {path}");

        var fileName = Path.GetFileName(path);
        if (!fileName.EndsWith(".compiled.json", StringComparison.OrdinalIgnoreCase))
            throw new ThemePackageException($"Compiled theme must use the '.compiled.json' suffix: {fileName}");
        var source = new LooseBuiltInThemeSource(path);
        return await ValidateAsync(DiscoveredTheme.From(source), cancellationToken);
    }

    public async Task<ValidatedTheme> ValidateAsync(DiscoveredTheme discovery,
        CancellationToken cancellationToken = default)
    {
        await using var stream = await discovery.Source.OpenCompiledJsonAsync(cancellationToken);
        if (!stream.CanRead)
            throw new ThemePackageException($"Compiled theme source is not readable: {discovery.Source.Description}");
        CompiledTheme theme;
        try
        {
            theme = await JsonSerializer.DeserializeAsync<CompiledTheme>(stream, JsonOptions, cancellationToken)
                    ?? throw new ThemePackageException("Compiled theme is empty.");
        }
        catch (JsonException ex)
        {
            throw new ThemePackageException($"Compiled theme JSON is malformed: {ex.Message}");
        }

        var recoverableMetadata = discovery.Id is { } recoverableId && !string.IsNullOrWhiteSpace(theme.Label)
            ? Manifest(theme, recoverableId)
            : null;
        try { Validate(theme); }
        catch (ThemePackageException exception)
        {
            throw new ThemePackageException(exception.Message, recoverableMetadata, exception);
        }
        if (discovery.Id is not { } id)
            throw new ThemePackageException($"Compiled theme does not contain a safe ID: {discovery.Source.SourceId}");
        var manifest = Manifest(theme, id);
        IReadOnlyDictionary<string, NormalizedCompiledControl> normalized;
        ValidatedThemeAssets assets;
        try
        {
            normalized = CompiledThemeNormalizer.Normalize(theme);
            assets = ParseAssets(theme);
        }
        catch (ThemePackageException exception)
        {
            throw new ThemePackageException(exception.Message, manifest, exception);
        }
        return new ValidatedTheme(discovery, discovery.Source, manifest, theme, normalized,
            ThemeColor.Parse(theme.FocusRing.Color), assets, []);
    }

    private static ValidatedThemeAssets ParseAssets(CompiledTheme theme)
    {
        var parser = new ThemeSvgIrParser();
        var documents = new Dictionary<string, SvgDocumentIr>(StringComparer.Ordinal);
        var slices = new Dictionary<string, NineSliceIr>(StringComparer.Ordinal);
        foreach (var (controlId, control) in theme.Controls)
        {
            foreach (var asset in CompiledThemeContract.EnumerateAssets(controlId, control))
                ParseAsset(asset, parser, documents, slices);
        }
        return new ValidatedThemeAssets(documents, slices, parser.ParseCount);
    }

    private static void ParseAsset(CompiledControlAsset asset,
        ThemeSvgIrParser parser, IDictionary<string, SvgDocumentIr> documents, IDictionary<string, NineSliceIr> slices)
    {
        try
        {
            if (!asset.IsAllowed)
                throw new InvalidDataException("Only Asset controls may contain nine-slice artwork.");
            if (asset.Kind == ThemeControlAssetKind.NineSlice && !slices.ContainsKey(asset.Source))
            {
                slices[asset.Source] = parser.ParseNineSlice(asset.Source);
            }
            else if (asset.Kind == ThemeControlAssetKind.Document && !documents.ContainsKey(asset.Source))
                documents[asset.Source] = parser.ParseDocument(asset.Source);
        }
        catch (Exception exception) when (exception is not ThemePackageException)
        {
            throw new ThemePackageException($"SVG '{asset.Path}' cannot be rendered: {exception.Message}");
        }
    }

    private static ThemeManifest Manifest(CompiledTheme theme, ThemeId id) => new()
    {
        Id = id.Value,
        DisplayName = theme.Label,
        Author = theme.Meta?.Maintainer ?? "Galapa Project",
        PackageVersion = theme.Meta?.Version ?? "compiled",
        BaseVariant = theme.Mode == "dark" ? ThemeBaseVariant.Dark : ThemeBaseVariant.Light
    };

    public static void Validate(CompiledTheme theme)
    {
        if (string.IsNullOrWhiteSpace(theme.Label) || theme.Label.Length > 100)
            throw new ThemePackageException("Compiled theme label is required.");
        if (theme.Mode is not ("light" or "dark"))
            throw new ThemePackageException("Compiled theme mode must be 'light' or 'dark'.");
        if (theme.FocusRing is null)
            throw new ThemePackageException("Compiled theme focus ring style is required.");
        if (string.IsNullOrWhiteSpace(theme.FocusRing.Color))
            throw new ThemePackageException("Focus ring color is required.");
        ValidateColor(theme.FocusRing.Color, "focus ring");
        if (!Finite(theme.FocusRing.Width, 0, 16) || !Finite(theme.FocusRing.Offset, -32, 32))
            throw new ThemePackageException("Focus ring metrics are out of range.");

        if (theme.Controls is null)
            throw new ThemePackageException("Compiled theme controls are required.");

        foreach (var id in CompiledThemeContract.Controls.Keys)
            if (!theme.Controls.ContainsKey(id))
                throw new ThemePackageException($"Compiled theme is missing control '{id}'.");
        foreach (var id in theme.Controls.Keys)
            if (!CompiledThemeContract.Controls.ContainsKey(id))
                throw new ThemePackageException($"Compiled theme contains unknown control '{id}'.");

        foreach (var (id, control) in theme.Controls)
            ValidateControl(id, control);
    }

    private static void ValidateControl(string id, CompiledControl control)
    {
        var expected = CompiledThemeContract.Controls[id].RequiredShape;
        if (control.Shape is not ("Window" or "Path" or "Asset" or "Text") || expected is not null && control.Shape != expected)
            throw new ThemePackageException($"Control '{id}' has invalid shape '{control.Shape}'.");
        if (expected is null && control.Shape is "Window" or "Text")
            throw new ThemePackageException($"Control '{id}' cannot use shape '{control.Shape}'.");

        ValidateOptionalColor(control.Fill, $"{id}.fill", allowInherit: false);
        ValidateOptionalColor(control.Content, $"{id}.content", allowInherit: true);
        ValidateOptionalColor(control.BorderColor, $"{id}.borderColor", allowInherit: false);
        ValidateEdges(control.BorderThickness, $"{id}.borderThickness", 0, 32);
        ValidateEdges(control.Padding, $"{id}.padding", 0, 256);
        ValidateRadius(control.Radius, id);
        if (control.Corner is not null && control.Corner is not ("round" or "bevel" or "scoop" or "notch" or "squircle"))
            throw new ThemePackageException($"Control '{id}' has invalid corner shape '{control.Corner}'.");
        if (control.Opacity is { } opacity && !Finite(opacity, 0, 1))
            throw new ThemePackageException($"Control '{id}' has invalid opacity.");
        if (control.Size?.Width is { } width && !Finite(width, 0, 4096) || control.Size?.Height is { } height && !Finite(height, 0, 4096))
            throw new ThemePackageException($"Control '{id}' has invalid size.");
        ValidateText(control.Text, id);

        if (control.ShowRing is not null)
            throw new ThemePackageException($"Control '{id}' may declare showRing only inside its focused state.");

        ValidateFieldsForShape(id, control);

        if (control.Shape == "Asset")
        {
            if (string.IsNullOrWhiteSpace(control.Art))
                throw new ThemePackageException($"Asset control '{id}' has no art.");
            ValidateSvgEnvelope(control.Art, $"{id}.art");
        }
        else if (control.Art is not null)
            throw new ThemePackageException($"Only Asset controls may declare art ('{id}').");

        if (control.Image is not null) ValidateSvgEnvelope(control.Image, $"{id}.image");
        if (control.Images is not null)
            foreach (var (variant, svg) in control.Images)
                ValidateSvgEnvelope(svg, $"{id}.images.{variant}");

        if (control.States is null) return;
        foreach (var (state, value) in control.States)
        {
            if (!CompiledThemeContract.States.Contains(state))
                throw new ThemePackageException($"Control '{id}' has unknown state '{state}'.");
            ValidateStateFields(id, control.Shape, state, value);
            if (value.ShowRing is not null && state != "focused")
                throw new ThemePackageException($"Control '{id}' state '{state}' cannot declare showRing.");
            if (state == "focused" && value.ShowRing == false && !HasVisibleFocusOverride(value))
                throw new ThemePackageException(
                    $"Control '{id}' focused.showRing=false requires a visible focused-state override.");
            ValidateOptionalColor(value.Fill, $"{id}.{state}.fill", false);
            ValidateOptionalColor(value.Content, $"{id}.{state}.content", true);
            ValidateOptionalColor(value.BorderColor, $"{id}.{state}.borderColor", false);
            ValidateEdges(value.BorderThickness, $"{id}.{state}.borderThickness", 0, 32);
            if (value.Opacity is { } stateOpacity && !Finite(stateOpacity, 0, 1))
                throw new ThemePackageException($"Control '{id}' state '{state}' has invalid opacity.");
            if (value.Image is not null) ValidateSvgEnvelope(value.Image, $"{id}.{state}.image");
            if (value.Art is not null) ValidateSvgEnvelope(value.Art, $"{id}.{state}.art");
        }
    }

    private static void ValidateStateFields(string id, string? parentShape, string state, CompiledControl value)
    {
        if (value.Shape is not null || ThemeMetrics.HasValue(value.Radius) || value.Corner is not null || ThemeMetrics.HasValue(value.Padding) ||
            value.Text is not null || value.Size is not null || value.LeftInset is not null ||
            value.Images is not null || value.States is not null || value.Art is not null && parentShape != "Asset")
            throw new ThemePackageException($"Control '{id}' state '{state}' declares fields that states cannot override.");
    }

    private static bool HasVisibleFocusOverride(CompiledControl value) =>
        value.Fill is not null || value.Content is not null || value.BorderColor is not null ||
        ThemeMetrics.HasValue(value.BorderThickness) || value.Opacity is not null ||
        value.Image is not null || value.Art is not null;

    private static void ValidateText(CompiledTextStyle? text, string id)
    {
        if (text is null) return;
        if (text.Family is { Length: > 200 } || text.Fallback is not null and not ("serif" or "sans-serif") ||
            text.Weight is { } weight && weight is < 1 or > 1000 ||
            text.Style is not null and not ("normal" or "italic" or "oblique") ||
            text.Size is { } size && !Finite(size, 1, 256) ||
            text.LetterSpacing is { } spacing && !Finite(spacing, -32, 128) ||
            text.Case is not null and not ("uppercase" or "lowercase" or "capitalize" or "none"))
            throw new ThemePackageException($"Control '{id}' contains invalid typography.");
    }

    private static void ValidateSvgEnvelope(string svg, string label)
    {
        if (svg.Length > 2_000_000) throw new ThemePackageException($"SVG '{label}' is too large.");
    }

    private static void ValidateOptionalColor(string? value, string label, bool allowInherit)
    {
        if (value is null) return;
        if (allowInherit && value == "inherit") return;
        ValidateColor(value, label);
    }

    private static void ValidateColor(string value, string label)
    {
        try { _ = ThemeColor.Parse(value); }
        catch { throw new ThemePackageException($"'{label}' contains invalid color '{value}'."); }
    }

    private static void ValidateEdges(JsonElement value, string label, double min, double max)
    {
        if (!ThemeMetrics.HasValue(value)) return;
        double[] edges;
        try { edges = ThemeMetrics.ReadEdges(value); }
        catch (Exception ex) { throw new ThemePackageException($"'{label}' is invalid: {ex.Message}"); }
        if (edges.Any(x => !Finite(x, min, max))) throw new ThemePackageException($"'{label}' is out of range.");
    }

    private static void ValidateRadius(JsonElement radius, string id)
    {
        if (!ThemeMetrics.HasValue(radius)) return;
        if (radius.ValueKind == JsonValueKind.String && radius.GetString() == "pill") return;
        if (radius.ValueKind != JsonValueKind.Number || !Finite(radius.GetDouble(), 0, 4096))
            throw new ThemePackageException($"Control '{id}' has invalid radius.");
    }

    private static bool Finite(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;
    private static void ValidateFieldsForShape(string id, CompiledControl control)
    {
        var invalid = control.Shape switch
        {
            "Window" => ThemeMetrics.HasValue(control.BorderThickness) || ThemeMetrics.HasValue(control.Radius) || control.Corner is not null ||
                        ThemeMetrics.HasValue(control.Padding) || control.Opacity is not null || control.Text is not null ||
                        control.Size is not null || control.Image is not null || control.Art is not null ||
                        control.LeftInset is not null || control.Images is not null || control.States is not null,
            "Text" => control.Fill is not null || ThemeMetrics.HasValue(control.BorderThickness) || ThemeMetrics.HasValue(control.Radius) ||
                      control.Corner is not null || ThemeMetrics.HasValue(control.Padding) || control.Opacity is not null ||
                      control.Size is not null || control.Image is not null || control.Art is not null ||
                      control.States is not null,
            "Asset" => control.Fill is not null || control.BorderColor is not null || ThemeMetrics.HasValue(control.BorderThickness) ||
                       ThemeMetrics.HasValue(control.Radius) || control.Corner is not null || ThemeMetrics.HasValue(control.Padding) ||
                       control.Image is not null || control.LeftInset is not null || control.Images is not null,
            "Path" => control.Art is not null || control.LeftInset is not null || control.Images is not null,
            _ => true
        };
        if (invalid)
            throw new ThemePackageException($"Control '{id}' declares fields that are not valid for shape '{control.Shape}'.");
    }

}

public static class ThemeMetrics
{
    public static bool HasValue(JsonElement value) =>
        value.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null);

    public static double[] ReadEdges(JsonElement value, double fallback = 0)
    {
        if (!HasValue(value)) return [fallback, fallback, fallback, fallback];
        if (value.ValueKind == JsonValueKind.Number)
        {
            var number = value.GetDouble();
            return [number, number, number, number];
        }
        if (value.ValueKind == JsonValueKind.Array)
        {
            var values = value.EnumerateArray().Select(x => x.GetDouble()).ToArray();
            if (values.Length == 4) return values;
        }
        throw new ThemePackageException("Expected a number or four-element edge array.");
    }

}
