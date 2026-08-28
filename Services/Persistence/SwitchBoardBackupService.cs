using System.IO.Compression;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using SwitchBoard.Data;
using SwitchBoard.Services.Profiles;
using SwitchBoard.Themes;

namespace SwitchBoard.Services.Persistence;

/// <summary>
/// Creates portable, validated configuration archives. The package intentionally
/// contains configuration only: catalog, settings and custom-theme assets that
/// are referenced by the settings. Runtime Restore sessions and logs stay out of
/// it because they are not a reproducible application configuration.
/// </summary>
public sealed class SwitchBoardBackupService
{
    public const string FormatId = "SwitchBoard.Backup";
    public const int CurrentFormatVersion = 2;
    private const int LegacyFormatVersion = 1;
    private const string ManifestName = "backup.json";
    private const string ThemeAssetEntryRoot = "theme-assets/";
    private const long MaxArchiveBytes = 128L * 1024 * 1024;
    private const long MaxExpandedBytes = 128L * 1024 * 1024;
    private const long MaxAssetBytes = 32L * 1024 * 1024;
    private static readonly HashSet<string> ThemeAssetExtensions =
        [".jpg", ".jpeg", ".png", ".bmp", ".gif"];

    private readonly JsonSerializerOptions _json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public SwitchBoardBackupService()
    {
        _json.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    }

    /// <summary>
    /// Legacy-compatible export. Without paths it safely excludes external
    /// custom-theme assets, just as older SwitchBoard versions did.
    /// </summary>
    public Task ExportAsync(SwitchBoardCatalog catalog, UserSettings settings, string destination,
        CancellationToken cancellationToken = default) =>
        ExportAsync(catalog, settings, destination, paths: null, cancellationToken);

    /// <summary>Exports all durable configuration, including owned theme assets.</summary>
    public async Task ExportAsync(SwitchBoardCatalog catalog, UserSettings settings, string destination,
        AppDataPaths? paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        var snapshot = CloneSettings(settings);
        var assets = paths is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : CollectThemeAssets(snapshot, paths);
        NormalizeThemeReferences(snapshot, assets.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase));

        var document = new SwitchBoardBackupDocument
        {
            FormatVersion = CurrentFormatVersion,
            ApplicationVersion = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown",
            ExportedAtUtc = DateTimeOffset.UtcNow,
            Catalog = catalog,
            Settings = snapshot,
            ThemeAssetPaths = assets.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList()
        };

        var directory = Path.GetDirectoryName(Path.GetFullPath(destination));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temporary = destination + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var archive = ZipFile.Open(temporary, ZipArchiveMode.Create))
            {
                var manifest = archive.CreateEntry(ManifestName, CompressionLevel.Fastest);
                await using (var stream = manifest.Open())
                    await JsonSerializer.SerializeAsync(stream, document, _json, cancellationToken);

                foreach (var (relativePath, sourcePath) in assets)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var entry = archive.CreateEntry(ThemeAssetEntryRoot + relativePath, CompressionLevel.Fastest);
                    await using var input = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                        81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                    await using var output = entry.Open();
                    await input.CopyToAsync(output, cancellationToken);
                }
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public async Task<string> CreateAutomaticBackupAsync(SwitchBoardCatalog catalog, UserSettings settings,
        AppDataPaths paths, int keepCount, CancellationToken cancellationToken = default)
    {
        var directory = paths.AutoBackupsDirectory;
        Directory.CreateDirectory(directory);
        var destination = Path.Combine(directory,
            $"SwitchBoard-auto-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}-{Guid.NewGuid():N}.sbbackup");
        await ExportAsync(catalog, settings, destination, paths, cancellationToken);
        await RotateAutomaticBackupsAsync(paths, keepCount, cancellationToken);
        return destination;
    }

    /// <summary>Creates a non-rotated safety archive for destructive operations.</summary>
    public async Task<string> CreateSafetyBackupAsync(SwitchBoardCatalog catalog, UserSettings settings,
        AppDataPaths paths, string operation, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(paths.AutoBackupsDirectory);
        var safeOperation = string.Concat((operation ?? "operation").Where(char.IsLetterOrDigit));
        if (string.IsNullOrWhiteSpace(safeOperation)) safeOperation = "operation";
        var destination = Path.Combine(paths.AutoBackupsDirectory,
            $"SwitchBoard-safety-{safeOperation}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmssfff}.sbbackup");
        await ExportAsync(catalog, settings, destination, paths, cancellationToken);
        // Reopen and validate the just-created package before destructive work is allowed.
        await ImportPackageAsync(destination, cancellationToken);
        return destination;
    }

    public async Task RotateAutomaticBackupsAsync(AppDataPaths paths, int keepCount,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(paths.AutoBackupsDirectory)) return;
        var keep = Math.Clamp(keepCount, 1, 50);
        var obsolete = Directory.EnumerateFiles(paths.AutoBackupsDirectory, "SwitchBoard-auto-*.sbbackup")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.CreationTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .Skip(keep)
            .ToList();
        foreach (var file in obsolete)
        {
            cancellationToken.ThrowIfCancellationRequested();
            file.Delete();
        }

        await Task.CompletedTask;
    }

    public async Task<SwitchBoardBackupDocument> ImportAsync(string source,
        CancellationToken cancellationToken = default) =>
        (await ImportPackageAsync(source, cancellationToken)).Document;

    /// <summary>Validates the archive before any current data is touched.</summary>
    public async Task<SwitchBoardBackupPackage> ImportPackageAsync(string source,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(source)) throw new FileNotFoundException("The backup file was not found.", source);
        if (new FileInfo(source).Length > MaxArchiveBytes)
            throw new InvalidDataException("The backup package is too large.");

        using var archive = ZipFile.OpenRead(source);
        var manifest = archive.GetEntry(ManifestName) ?? throw new InvalidDataException("backup.json is missing.");
        if (manifest.Length > MaxExpandedBytes) throw new InvalidDataException("The backup manifest is too large.");

        SwitchBoardBackupDocument document;
        await using (var stream = manifest.Open())
        {
            document = await JsonSerializer.DeserializeAsync<SwitchBoardBackupDocument>(stream, _json, cancellationToken)
                ?? throw new InvalidDataException("The backup manifest is empty.");
        }

        if (document.FormatId != FormatId || document.FormatVersion is < LegacyFormatVersion or > CurrentFormatVersion ||
            document.Catalog is null || document.Settings is null)
            throw new InvalidDataException("Unsupported or invalid SwitchBoard backup format.");

        ProfileCatalogService.ValidateForImport(document.Catalog);
        document.ThemeAssetPaths ??= [];
        var declaredAssets = document.ThemeAssetPaths
            .Select(NormalizeAssetRelativePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (declaredAssets.Count != document.ThemeAssetPaths.Count)
            throw new InvalidDataException("The backup declares duplicate theme assets.");

        var assets = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        long totalExpanded = manifest.Length;
        if (document.FormatVersion == LegacyFormatVersion && archive.Entries.Count != 1)
            throw new InvalidDataException("The legacy backup package contains unsupported files.");

        foreach (var relativePath in declaredAssets)
        {
            var entry = archive.GetEntry(ThemeAssetEntryRoot + relativePath)
                ?? throw new InvalidDataException("A declared theme asset is missing.");
            if (entry.Length > MaxAssetBytes) throw new InvalidDataException("A theme asset is too large.");
            totalExpanded += entry.Length;
            if (totalExpanded > MaxExpandedBytes) throw new InvalidDataException("The backup expands to too much data.");
            await using var stream = entry.Open();
            await using var memory = new MemoryStream((int)entry.Length);
            await stream.CopyToAsync(memory, cancellationToken);
            assets.Add(relativePath, memory.ToArray());
        }

        NormalizeSettings(document.Settings, declaredAssets);
        document.ThemeAssetPaths = declaredAssets.Order(StringComparer.OrdinalIgnoreCase).ToList();
        return new SwitchBoardBackupPackage(document, assets);
    }

    /// <summary>
    /// Writes imported theme assets to a sibling staging directory. Call Commit
    /// only after catalog/settings persistence has succeeded; Rollback preserves
    /// the original directory if any later operation fails.
    /// </summary>
    public async Task<ThemeAssetStaging> StageThemeAssetsAsync(SwitchBoardBackupPackage package,
        AppDataPaths paths, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(package);
        var parent = Path.GetDirectoryName(paths.CustomThemeDirectory)
            ?? throw new InvalidOperationException("The custom-theme directory has no parent.");
        Directory.CreateDirectory(parent);
        var stagingPath = Path.Combine(parent, $".restore-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(stagingPath);
            foreach (var (relativePath, content) in package.ThemeAssets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var target = Path.Combine(stagingPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
                var fullTarget = Path.GetFullPath(target);
                if (!IsWithinDirectory(fullTarget, stagingPath))
                    throw new InvalidDataException("The backup contains an unsafe theme asset path.");
                Directory.CreateDirectory(Path.GetDirectoryName(fullTarget)!);
                await File.WriteAllBytesAsync(fullTarget, content, cancellationToken);
            }

            return new ThemeAssetStaging(paths.CustomThemeDirectory, stagingPath);
        }
        catch
        {
            if (Directory.Exists(stagingPath)) Directory.Delete(stagingPath, recursive: true);
            throw;
        }
    }

    public static UserSettings CloneSettings(UserSettings source)
    {
        var json = JsonSerializer.Serialize(source);
        return JsonSerializer.Deserialize<UserSettings>(json) ?? new UserSettings();
    }

    private static Dictionary<string, string> CollectThemeAssets(UserSettings settings, AppDataPaths paths)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var theme in settings.CustomThemes)
        {
            theme.Colors ??= CustomThemeSettings.CreateDefault();
            theme.Colors.PreviewBackgroundPath = null;
            var reference = theme.Colors.BackgroundAssetFileName;
            if (string.IsNullOrWhiteSpace(reference)) continue;
            string relative;
            try { relative = NormalizeAssetRelativePath(reference); }
            catch (InvalidDataException)
            {
                theme.Colors.BackgroundAssetFileName = null;
                continue;
            }

            var path = Path.GetFullPath(Path.Combine(paths.CustomThemeDirectory,
                relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsWithinDirectory(path, paths.CustomThemeDirectory) || !File.Exists(path) ||
                new FileInfo(path).Length > MaxAssetBytes)
            {
                theme.Colors.BackgroundAssetFileName = null;
                continue;
            }

            result[relative] = path;
        }

        return result;
    }

    private static void NormalizeThemeReferences(UserSettings settings, IReadOnlySet<string> availableAssets)
    {
        settings.CustomThemes ??= [];
        settings.CustomThemes.RemoveAll(theme => theme is null);
        foreach (var theme in settings.CustomThemes)
        {
            theme.Colors ??= CustomThemeSettings.CreateDefault();
            theme.Colors.PreviewBackgroundPath = null;
            if (string.IsNullOrWhiteSpace(theme.Colors.BackgroundAssetFileName)) continue;
            try
            {
                var path = NormalizeAssetRelativePath(theme.Colors.BackgroundAssetFileName);
                theme.Colors.BackgroundAssetFileName = availableAssets.Contains(path) ? path : null;
            }
            catch (InvalidDataException)
            {
                theme.Colors.BackgroundAssetFileName = null;
            }
        }
    }

    private static void NormalizeSettings(UserSettings settings, IReadOnlySet<string> availableAssets)
    {
        if (settings.SchemaVersion > SettingsSchema.CurrentVersion)
            throw new InvalidDataException("The backup contains a newer settings schema.");
        settings.SchemaVersion = SettingsSchema.CurrentVersion;
        NormalizeThemeReferences(settings, availableAssets);
        settings.CloseBehavior = string.Equals(settings.CloseBehavior, "tray", StringComparison.OrdinalIgnoreCase)
            ? "tray" : "close";
        settings.AutomaticBackupCount = Math.Clamp(settings.AutomaticBackupCount, 1, 50);
        settings.LastMainView = settings.LastMainView is "Home" or "Activity" or "Settings"
            ? settings.LastMainView : "Home";
        settings.InterfaceDensity = string.Equals(settings.InterfaceDensity, "compact", StringComparison.OrdinalIgnoreCase)
            ? "compact" : "standard";
    }

    private static string NormalizeAssetRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathFullyQualified(path))
            throw new InvalidDataException("Invalid theme asset path.");
        var normalized = path.Replace('\\', '/').TrimStart('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or "..") ||
            !ThemeAssetExtensions.Contains(Path.GetExtension(normalized)))
            throw new InvalidDataException("Invalid theme asset path.");
        return string.Join('/', segments);
    }

    private static bool IsWithinDirectory(string candidate, string root)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        return candidate.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class SwitchBoardBackupDocument
{
    public string FormatId { get; set; } = SwitchBoardBackupService.FormatId;
    public int FormatVersion { get; set; } = SwitchBoardBackupService.CurrentFormatVersion;
    public DateTimeOffset ExportedAtUtc { get; set; }
    public string ApplicationVersion { get; set; } = string.Empty;
    public SwitchBoardCatalog Catalog { get; set; } = SwitchBoardCatalog.Empty();
    public UserSettings Settings { get; set; } = new();
    public List<string> ThemeAssetPaths { get; set; } = [];
}

public sealed record SwitchBoardBackupPackage(
    SwitchBoardBackupDocument Document,
    IReadOnlyDictionary<string, byte[]> ThemeAssets);

/// <summary>Recoverable directory replacement used while importing or resetting configuration.</summary>
public sealed class ThemeAssetStaging : IDisposable
{
    private readonly string _targetDirectory;
    private readonly string _stagingDirectory;
    private string? _previousDirectory;
    private bool _committed;
    private bool _completed;

    internal ThemeAssetStaging(string targetDirectory, string stagingDirectory)
    {
        _targetDirectory = targetDirectory;
        _stagingDirectory = stagingDirectory;
    }

    public void Commit()
    {
        if (_committed) return;
        var parent = Path.GetDirectoryName(_targetDirectory)!;
        if (Directory.Exists(_targetDirectory))
        {
            _previousDirectory = Path.Combine(parent, $".previous-{Guid.NewGuid():N}");
            Directory.Move(_targetDirectory, _previousDirectory);
        }

        try
        {
            Directory.Move(_stagingDirectory, _targetDirectory);
            _committed = true;
        }
        catch
        {
            if (_previousDirectory is not null && Directory.Exists(_previousDirectory) && !Directory.Exists(_targetDirectory))
                Directory.Move(_previousDirectory, _targetDirectory);
            throw;
        }
    }

    public void Rollback()
    {
        if (!_committed) return;
        if (Directory.Exists(_targetDirectory)) Directory.Delete(_targetDirectory, recursive: true);
        if (_previousDirectory is not null && Directory.Exists(_previousDirectory))
            Directory.Move(_previousDirectory, _targetDirectory);
        _previousDirectory = null;
        _committed = false;
    }

    public void Complete()
    {
        if (!_committed || _completed) return;
        if (_previousDirectory is not null && Directory.Exists(_previousDirectory))
            Directory.Delete(_previousDirectory, recursive: true);
        _previousDirectory = null;
        _completed = true;
    }

    public void Dispose()
    {
        if (!_completed && _committed) Rollback();
        if (Directory.Exists(_stagingDirectory)) Directory.Delete(_stagingDirectory, recursive: true);
    }
}
