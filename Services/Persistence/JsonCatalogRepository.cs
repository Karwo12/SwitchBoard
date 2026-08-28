using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SwitchBoard.Data;

namespace SwitchBoard.Services.Persistence;

public sealed class JsonCatalogRepository : ICatalogRepository, IDisposable
{
    private readonly AppDataPaths _paths;
    private readonly JsonSerializerOptions _serializerOptions;
    private readonly SemaphoreSlim _fileLock = new(1, 1);

    public JsonCatalogRepository(AppDataPaths paths)
    {
        _paths = paths;
        _serializerOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };
        _serializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
    }

    public async Task<SwitchBoardCatalog> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_paths.CatalogFilePath))
            {
                return SwitchBoardCatalog.Empty();
            }

            await using var stream = new FileStream(
                _paths.CatalogFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var catalog = await JsonSerializer.DeserializeAsync<SwitchBoardCatalog>(
                stream,
                _serializerOptions,
                cancellationToken);

            if (catalog is null)
            {
                throw new InvalidDataException("catalog.json does not contain a valid catalog.");
            }

            if (catalog.SchemaVersion > CatalogSchema.CurrentVersion)
            {
                throw new InvalidDataException(
                    $"Catalog schema {catalog.SchemaVersion} is newer than supported schema {CatalogSchema.CurrentVersion}.");
            }

            catalog.Categories ??= [];
            catalog.Profiles ??= [];
            foreach (var profile in catalog.Profiles)
            {
                profile.Actions ??= [];
            }

            return catalog;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("catalog.json is malformed and could not be loaded.", exception);
        }
        finally
        {
            _fileLock.Release();
        }
    }

    public async Task SaveAsync(SwitchBoardCatalog catalog, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        await _fileLock.WaitAsync(cancellationToken);
        string? temporaryFilePath = null;

        try
        {
            Directory.CreateDirectory(_paths.RootDirectory);
            temporaryFilePath = Path.Combine(
                _paths.RootDirectory,
                $".catalog.{Guid.NewGuid():N}.tmp");

            await using (var stream = new FileStream(
                temporaryFilePath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    catalog,
                    _serializerOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_paths.CatalogFilePath))
            {
                File.Replace(
                    temporaryFilePath,
                    _paths.CatalogFilePath,
                    _paths.CatalogBackupFilePath,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryFilePath, _paths.CatalogFilePath);
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
