using System.IO;
using System.Text.Json;
using SwitchBoard.Data;

namespace SwitchBoard.Services.Persistence;

public sealed class JsonSettingsRepository : ISettingsRepository, IDisposable
{
    private readonly AppDataPaths _paths;
    private readonly JsonSerializerOptions _serializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public JsonSettingsRepository(AppDataPaths paths)
    {
        _paths = paths;
    }

    public async Task<UserSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_paths.SettingsFilePath))
            {
                return new UserSettings();
            }

            await using var stream = new FileStream(
                _paths.SettingsFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var settings = await JsonSerializer.DeserializeAsync<UserSettings>(
                stream,
                _serializerOptions,
                cancellationToken);

            return settings is null || settings.SchemaVersion > SettingsSchema.CurrentVersion
                ? new UserSettings()
                : settings;
        }
        catch (JsonException)
        {
            return new UserSettings();
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task SaveAsync(UserSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await _fileLock.WaitAsync(cancellationToken);
        string? temporaryFilePath = null;
        try
        {
            Directory.CreateDirectory(_paths.RootDirectory);
            temporaryFilePath = Path.Combine(
                _paths.RootDirectory,
                $".settings.{Guid.NewGuid():N}.tmp");

            await using (var stream = new FileStream(
                temporaryFilePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, settings, _serializerOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_paths.SettingsFilePath))
            {
                File.Replace(
                    temporaryFilePath,
                    _paths.SettingsFilePath,
                    _paths.SettingsBackupFilePath,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryFilePath, _paths.SettingsFilePath);
            }

            temporaryFilePath = null;
        }
        finally
        {
            if (temporaryFilePath is not null && File.Exists(temporaryFilePath))
            {
                File.Delete(temporaryFilePath);
            }

            _fileLock.Release();
        }
    }

    public void Dispose() => _fileLock.Dispose();
}
