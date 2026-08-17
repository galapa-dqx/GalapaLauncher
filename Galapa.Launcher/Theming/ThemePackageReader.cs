using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Media;

namespace Galapa.Launcher.Theming;

public sealed class ThemePackageException(string message) : Exception(message);

public sealed class ThemePackageReader
{
    private static readonly string[] RequiredTypography =
    ["brand", "navigation", "sectionHeading", "fieldLabel", "button", "cardTitle", "body", "input", "settingValue", "metadata"];

    private static readonly string[] RequiredGeometry =
    ["panel", "card", "input", "button", "iconButton", "switch", "scrollbarTrack", "scrollbarThumb", "indicator"];

    private static readonly HashSet<string> FontExtensions = new(StringComparer.OrdinalIgnoreCase) { ".ttf", ".otf" };
    private static readonly HashSet<string> AssetExtensions = new(StringComparer.OrdinalIgnoreCase) { ".svg", ".png", ".jpg", ".jpeg", ".webp" };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public async Task<ThemePackage> ReadAsync(string archivePath, string cacheRoot, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(archivePath)) throw new ThemePackageException($"Theme package not found: {archivePath}");

        await using var file = File.OpenRead(archivePath);
        using var archive = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
        ValidateEntries(archive);

        var manifestEntry = FindEntry(archive, "theme.json")
            ?? throw new ThemePackageException("Theme package is missing theme.json.");
        ThemeManifest manifest;
        await using (var manifestStream = manifestEntry.Open())
        {
            manifest = await JsonSerializer.DeserializeAsync<ThemeManifest>(manifestStream, JsonOptions, cancellationToken)
                ?? throw new ThemePackageException("theme.json is empty.");
        }

        ValidateManifest(manifest, archive);
        file.Position = 0;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, cancellationToken)).ToLowerInvariant()[..16];
        var destination = Path.Combine(cacheRoot, manifest.Id, hash);
        Directory.CreateDirectory(destination);

        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue;
            var normalized = entry.FullName.Replace('\\', '/');
            var path = Path.GetFullPath(Path.Combine(destination, normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(Path.GetFullPath(destination) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ThemePackageException($"Unsafe archive path: {entry.FullName}");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var source = entry.Open();
            await using var target = File.Create(path);
            await source.CopyToAsync(target, cancellationToken);
        }

        var assets = manifest.Assets.ToDictionary(x => x.Key, x => Path.Combine(destination, x.Value.Replace('/', Path.DirectorySeparatorChar)));
        return new ThemePackage(manifest, archivePath, destination, assets);
    }

    public static void ValidateManifest(ThemeManifest manifest, ZipArchive archive)
    {
        if (manifest.SchemaVersion != 1) throw new ThemePackageException($"Unsupported theme schema {manifest.SchemaVersion}.");
        if (string.IsNullOrWhiteSpace(manifest.Id) || manifest.Id.Any(c => !(char.IsLower(c) || char.IsDigit(c) || c is '-' or '.')))
            throw new ThemePackageException("Theme ID must contain only lowercase letters, digits, hyphens, and periods.");
        if (string.IsNullOrWhiteSpace(manifest.DisplayName) || string.IsNullOrWhiteSpace(manifest.Author) || string.IsNullOrWhiteSpace(manifest.PackageVersion))
            throw new ThemePackageException("Theme metadata is incomplete.");

        foreach (var color in typeof(ThemeColors).GetProperties().Select(p => (string?)p.GetValue(manifest.Colors)))
            if (color is null || !Color.TryParse(color, out _)) throw new ThemePackageException($"Invalid theme color: {color ?? "null"}");

        ValidateFont(manifest.Fonts.Heading, archive);
        ValidateFont(manifest.Fonts.Body, archive);
        if (manifest.Fonts.HeadingBaseSize is < 8 or > 72 || manifest.Fonts.BodyBaseSize is < 8 or > 72)
            throw new ThemePackageException("Base font sizes must be between 8 and 72.");

        foreach (var role in RequiredTypography)
        {
            if (!manifest.Typography.TryGetValue(role, out var token)) throw new ThemePackageException($"Missing typography role: {role}");
            if (token.SizeScale is < .5 or > 8 || token.Weight is < 100 or > 900 || token.LetterSpacing is < -5 or > 20)
                throw new ThemePackageException($"Typography role '{role}' contains out-of-range values.");
        }

        foreach (var role in RequiredGeometry)
        {
            if (!manifest.Geometry.TryGetValue(role, out var token)) throw new ThemePackageException($"Missing geometry role: {role}");
            if (token.Radius is < 0 or > 100 || token.BorderThickness is < 0 or > 10)
                throw new ThemePackageException($"Geometry role '{role}' contains out-of-range values.");
        }

        foreach (var (role, path) in manifest.Assets)
        {
            ValidateRelativePath(path, $"asset '{role}'");
            if (!AssetExtensions.Contains(Path.GetExtension(path))) throw new ThemePackageException($"Unsupported asset format: {path}");
            if (FindEntry(archive, path) is null) throw new ThemePackageException($"Missing asset referenced by '{role}': {path}");
        }
    }

    private static void ValidateFont(ThemeFont font, ZipArchive archive)
    {
        ValidateRelativePath(font.File, "font");
        if (!FontExtensions.Contains(Path.GetExtension(font.File))) throw new ThemePackageException($"Unsupported font format: {font.File}");
        if (string.IsNullOrWhiteSpace(font.Family)) throw new ThemePackageException("A font family name is required.");
        if (FindEntry(archive, font.File) is null) throw new ThemePackageException($"Missing font: {font.File}");
        if (font.License is not null)
        {
            ValidateRelativePath(font.License, "font license");
            if (FindEntry(archive, font.License) is null) throw new ThemePackageException($"Missing font license: {font.License}");
        }
    }

    private static void ValidateEntries(ZipArchive archive)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in archive.Entries)
        {
            ValidateRelativePath(entry.FullName, "archive entry");
            var normalized = entry.FullName.Replace('\\', '/');
            if (!names.Add(normalized)) throw new ThemePackageException($"Duplicate archive entry: {entry.FullName}");
        }
    }

    private static void ValidateRelativePath(string path, string label)
    {
        var normalized = path.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || normalized.Split('/').Any(x => x is ".." or "." or ""))
            throw new ThemePackageException($"Unsafe {label} path: {path}");
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string path)
    {
        var normalized = path.Replace('\\', '/');
        return archive.Entries.FirstOrDefault(x => string.Equals(x.FullName.Replace('\\', '/'), normalized, StringComparison.OrdinalIgnoreCase));
    }
}
