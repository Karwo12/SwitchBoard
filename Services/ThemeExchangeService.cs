using System.IO.Compression;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Media.Imaging;
using SwitchBoard.Data;
using SwitchBoard.Themes;

namespace SwitchBoard.Services;

public sealed class ThemeExchangeService(AppDataPaths paths)
{
    public const string FormatId = "SwitchBoard.Theme";
    public const int CurrentFormatVersion = 1;
    private const long MaxArchiveBytes = 512L * 1024 * 1024;
    private const long MaxExpandedBytes = 768L * 1024 * 1024;
    private static readonly HashSet<string> AssetExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };
    private readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public void Export(CustomThemeDefinition theme, string destination)
    {
        ArgumentNullException.ThrowIfNull(theme);
        var settings = theme.Colors.Clone();
        var assetPath = ResolveAsset(settings.BackgroundAssetFileName);
        var assetName = assetPath is null ? null : $"assets/background{Path.GetExtension(assetPath).ToLowerInvariant()}";
        settings.BackgroundAssetFileName = assetName;
        var document = new ThemePackageDocument
        {
            FormatId = FormatId, FormatVersion = CurrentFormatVersion,
            ApplicationVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown",
            ExportedAtUtc = DateTimeOffset.UtcNow, Theme = theme.Clone(theme.Id)
        };
        document.Theme.Colors = settings;
        using var archive = ZipFile.Open(destination, ZipArchiveMode.Create);
        var json = archive.CreateEntry("theme.json", CompressionLevel.Fastest);
        using (var writer = new StreamWriter(json.Open())) writer.Write(JsonSerializer.Serialize(document, _json));
        if (assetPath is not null) archive.CreateEntryFromFile(assetPath, assetName!);
    }

    public CustomThemeDefinition Import(string packagePath, IReadOnlyCollection<CustomThemeDefinition> existing)
    {
        if (new FileInfo(packagePath).Length > MaxArchiveBytes) throw new InvalidDataException("Theme package is too large.");
        using var archive = ZipFile.OpenRead(packagePath);
        var jsonEntry = archive.GetEntry("theme.json") ?? throw new InvalidDataException("theme.json is missing.");
        if (archive.Entries.Count > 32) throw new InvalidDataException("Theme package contains too many files.");
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            var parts = entry.FullName.Split('/');
            if (entry.FullName.StartsWith('/') || entry.FullName.Contains('\\') || entry.FullName.Contains(':') || parts.Any(part => part == ".."))
                throw new InvalidDataException("Unsafe archive path.");
            if (entry.FullName != "theme.json" && (!entry.FullName.StartsWith("assets/", StringComparison.Ordinal) || !AssetExtensions.Contains(Path.GetExtension(entry.FullName))))
            {
                if (string.Equals(Path.GetExtension(entry.FullName), ".mp4", StringComparison.OrdinalIgnoreCase))
                    throw new UnsupportedThemeAssetException();
                throw new InvalidDataException("Unsupported package entry.");
            }
            expanded += entry.Length;
            if (expanded > MaxExpandedBytes) throw new InvalidDataException("Theme package expands beyond the allowed limit.");
        }
        ThemePackageDocument? document;
        using (var stream = jsonEntry.Open()) document = JsonSerializer.Deserialize<ThemePackageDocument>(stream, _json);
        if (document is null || document.FormatId != FormatId || document.FormatVersion != CurrentFormatVersion || document.Theme is null)
            throw new InvalidDataException("Unsupported or invalid theme format.");
        var theme = document.Theme;
        theme.Id = CustomThemeDefinition.CreateId();
        theme.Name = UniqueName(theme.Name, existing.Select(item => item.Name));
        theme.IsBuiltIn = false;
        theme.Colors ??= CustomThemeSettings.CreateDefault();
        theme.Colors.NormalizeLegacy();
        var declared = theme.Colors.BackgroundAssetFileName;
        ZipArchiveEntry? asset = null;
        if (!string.IsNullOrWhiteSpace(declared))
        {
            if (string.Equals(Path.GetExtension(declared), ".mp4", StringComparison.OrdinalIgnoreCase))
                throw new UnsupportedThemeAssetException();
            if (!declared.StartsWith("assets/", StringComparison.Ordinal) || !AssetExtensions.Contains(Path.GetExtension(declared))) throw new InvalidDataException("Invalid background asset reference.");
            asset = archive.GetEntry(declared) ?? throw new InvalidDataException("Declared background asset is missing.");
        }
        var temp = Path.Combine(paths.CustomThemeDirectory, $".import-{Guid.NewGuid():N}");
        var final = Path.Combine(paths.CustomThemeDirectory, theme.Id);
        var committed = false;
        try
        {
            Directory.CreateDirectory(temp);
            if (asset is not null)
            {
                var fileName = "background" + Path.GetExtension(asset.Name).ToLowerInvariant();
                var target = Path.Combine(temp, fileName);
                // Keep the file handles scoped to extraction only. Directory.Move below
                // must run after the destination file has been closed on Windows.
                using (var input = asset.Open())
                using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    input.CopyTo(output);
                }
                ValidateAsset(target);
                theme.Colors.BackgroundAssetFileName = $"{theme.Id}/{fileName}";
            }
            else theme.Colors.BackgroundAssetFileName = null;
            Directory.CreateDirectory(paths.CustomThemeDirectory);
            Directory.Move(temp, final);
            committed = true;
            return theme;
        }
        finally
        {
            // A failed import must not hide its original error with a cleanup error. All
            // handles opened by this method are already closed before this point.
            if (!committed) TryDeleteDirectory(temp);
        }
    }

    public void DeleteOwnedAssets(string themeId)
    {
        if (string.IsNullOrWhiteSpace(themeId) || themeId.Contains(Path.DirectorySeparatorChar) || themeId.Contains(Path.AltDirectorySeparatorChar)) return;
        var folder = Path.Combine(paths.CustomThemeDirectory, themeId);
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
    }

    private string? ResolveAsset(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        var path = Path.IsPathFullyQualified(reference) ? reference : Path.Combine(paths.CustomThemeDirectory, reference);
        return File.Exists(path) && AssetExtensions.Contains(Path.GetExtension(path)) ? path : throw new FileNotFoundException("Theme background asset was not found.", path);
    }

    private static void ValidateAsset(string path)
    {
        using (var imageStream = File.OpenRead(path))
        {
            var decoder = BitmapDecoder.Create(imageStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) throw new InvalidDataException("The image asset contains no frames.");
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        try { Directory.Delete(path, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public sealed class UnsupportedThemeAssetException() : Exception(
        "This theme contains an MP4 background, which is no longer supported.");

    private static string UniqueName(string name, IEnumerable<string> names)
    {
        var baseName = string.IsNullOrWhiteSpace(name) ? "Imported theme" : name.Trim();
        var used = new HashSet<string>(names, StringComparer.CurrentCultureIgnoreCase);
        var candidate = baseName; for (var i = 2; !used.Add(candidate); i++) candidate = $"{baseName} ({i})";
        return candidate;
    }

    private sealed class ThemePackageDocument
    {
        public string FormatId { get; set; } = string.Empty;
        public int FormatVersion { get; set; }
        public string ApplicationVersion { get; set; } = string.Empty;
        public DateTimeOffset ExportedAtUtc { get; set; }
        public CustomThemeDefinition Theme { get; set; } = new();
    }
}
