using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using SwitchBoard.Data;
using SwitchBoard.Models.Execution;

namespace SwitchBoard.Services.Persistence;

public sealed class JsonExecutionSessionRepository(AppDataPaths paths) : IExecutionSessionRepository, IDisposable
{
    private readonly SemaphoreSlim _fileLock = new(1, 1);
    private readonly JsonSerializerOptions _options = CreateOptions();

    public async Task SaveAsync(PersistentExecutionSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        await _fileLock.WaitAsync(cancellationToken);
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(paths.SessionsDirectory);
            session.UpdatedAt = DateTimeOffset.UtcNow;
            var target = GetPath(session.SessionId);
            temporary = Path.Combine(paths.SessionsDirectory, $".{session.SessionId:N}.{Guid.NewGuid():N}.tmp");
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, session, _options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }

            if (File.Exists(target)) File.Replace(temporary, target, null, true);
            else File.Move(temporary, target);
            temporary = null;
        }
        finally
        {
            if (temporary is not null && File.Exists(temporary)) File.Delete(temporary);
            _fileLock.Release();
        }
    }

    public async Task<PersistentExecutionSession?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try { return await ReadAsync(GetPath(sessionId), cancellationToken); }
        finally { _fileLock.Release(); }
    }

    public async Task<PersistentExecutionSession?> GetLatestPendingAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            if (!Directory.Exists(paths.SessionsDirectory)) return null;
            PersistentExecutionSession? latest = null;
            foreach (var file in Directory.EnumerateFiles(paths.SessionsDirectory, "*.json"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                PersistentExecutionSession? candidate;
                try { candidate = await ReadAsync(file, cancellationToken); }
                catch (JsonException) { continue; }
                catch (InvalidDataException) { continue; }
                if (candidate?.ProfileId != profileId || candidate.PendingRestoreCount == 0) continue;
                if (latest is null || candidate.UpdatedAt > latest.UpdatedAt) latest = candidate;
            }
            return latest;
        }
        finally { _fileLock.Release(); }
    }

    public async Task<PersistentExecutionSession?> GetLatestAttentionAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            if (!Directory.Exists(paths.SessionsDirectory)) return null;
            PersistentExecutionSession? latest = null;
            foreach (var file in Directory.EnumerateFiles(paths.SessionsDirectory, "*.json"))
            {
                PersistentExecutionSession? candidate;
                try { candidate = await ReadAsync(file, cancellationToken); }
                catch (Exception exception) when (exception is JsonException or InvalidDataException) { continue; }
                if (candidate?.ProfileId != profileId || candidate.Status != PersistentSessionStatus.RecoveryRequired) continue;
                if (latest is null || candidate.UpdatedAt > latest.UpdatedAt) latest = candidate;
            }
            return latest;
        }
        finally { _fileLock.Release(); }
    }

    public async Task MaintainAsync(TimeSpan restoredRetention, CancellationToken cancellationToken = default)
    {
        await _fileLock.WaitAsync(cancellationToken);
        try
        {
            if (!Directory.Exists(paths.SessionsDirectory)) return;
            var cutoff = DateTimeOffset.UtcNow - restoredRetention;
            foreach (var file in Directory.EnumerateFiles(paths.SessionsDirectory, "*.json").ToList())
            {
                cancellationToken.ThrowIfCancellationRequested();
                PersistentExecutionSession? session;
                try { session = await ReadAsync(file, cancellationToken); }
                catch (Exception exception) when (exception is JsonException or InvalidDataException) { continue; }
                if (session is null) continue;
                if (session.Status is PersistentSessionStatus.Preparing or PersistentSessionStatus.Executing or PersistentSessionStatus.Restoring)
                {
                    session.Status = PersistentSessionStatus.RecoveryRequired;
                    await WriteUnsafeAsync(session, cancellationToken);
                    continue;
                }
                if (session.Status == PersistentSessionStatus.Restored && session.PendingRestoreCount == 0 && session.UpdatedAt < cutoff)
                {
                    File.Delete(file);
                }
            }
        }
        finally { _fileLock.Release(); }
    }

    private async Task<PersistentExecutionSession?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var session = await JsonSerializer.DeserializeAsync<PersistentExecutionSession>(stream, _options, cancellationToken);
        if (session is null) throw new InvalidDataException($"Session file '{path}' is empty.");
        session.Actions ??= [];
        return session;
    }

    private string GetPath(Guid id) => Path.Combine(paths.SessionsDirectory, $"{id:N}.json");

    private async Task WriteUnsafeAsync(PersistentExecutionSession session, CancellationToken cancellationToken)
    {
        session.UpdatedAt = DateTimeOffset.UtcNow;
        var target = GetPath(session.SessionId);
        var temporary = Path.Combine(paths.SessionsDirectory, $".{session.SessionId:N}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, session, _options, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }
            if (File.Exists(target)) File.Replace(temporary, target, null, true);
            else File.Move(temporary, target);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var result = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
        result.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return result;
    }

    public void Dispose() => _fileLock.Dispose();
}
