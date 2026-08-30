using System.IO.Compression;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SwitchBoard.Data;
using SwitchBoard.Services.Activity;

namespace SwitchBoard.Services.Diagnostics;

/// <summary>Creates local, opt-in diagnostic archives without including profiles or settings.</summary>
public sealed class DiagnosticExportService
{
    private const int DiagnosticHistoryRecordLimit = 200;
    private const long DiagnosticLogByteLimit = 256 * 1024;
    private static readonly Regex SensitiveLogValue = new(
        @"(?im)(\b(?:password|passwd|token|api[_-]?key|authorization|cookie|secret)\b\s*[:=]\s*)[^\s,;]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly AppDataPaths _paths;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public DiagnosticExportService(AppDataPaths paths) => _paths = paths;

    public async Task ExportDiagnosticsAsync(string destination, string diagnostics,
        IReadOnlyList<PersistentActivityRecord> records, CancellationToken cancellationToken = default)
    {
        await WriteArchiveAsync(destination, archive =>
        {
            WriteText(archive, "diagnostics.txt", diagnostics);
            var recentRecords = records.OrderByDescending(record => record.Timestamp)
                .Take(DiagnosticHistoryRecordLimit)
                .OrderBy(record => record.Timestamp)
                .ToList();
            WriteText(archive, "history.json", JsonSerializer.Serialize(recentRecords, _json));
            if (Directory.Exists(_paths.LogsDirectory))
            {
                var remainingBytes = DiagnosticLogByteLimit;
                foreach (var file in Directory.EnumerateFiles(_paths.LogsDirectory, "switchboard.log*")
                             .OrderByDescending(path => File.GetLastWriteTimeUtc(path)))
                {
                    if (remainingBytes <= 0) break;
                    var addedBytes = AddLogTail(archive, file, remainingBytes);
                    remainingBytes -= addedBytes;
                }
            }
        }, cancellationToken);
    }

    public Task ExportHistoryAsync(string destination, IReadOnlyList<PersistentActivityRecord> records,
        CancellationToken cancellationToken = default) =>
        WriteArchiveAsync(destination, archive =>
            WriteText(archive, "history.json", JsonSerializer.Serialize(records, _json)), cancellationToken);

    private static async Task WriteArchiveAsync(string destination, Action<ZipArchive> writer,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        var directory = Path.GetDirectoryName(destination);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        var temporary = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
                writer(archive);
            }, cancellationToken);
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static void WriteText(ZipArchive archive, string name, string text)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(text);
    }

    private static long AddLogTail(ZipArchive archive, string file, long maximumBytes)
    {
        var length = new FileInfo(file).Length;
        var bytesToCopy = Math.Min(length, maximumBytes);
        if (bytesToCopy <= 0) return 0;
        using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (source.Length > bytesToCopy) source.Seek(-bytesToCopy, SeekOrigin.End);
        using var reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: true,
            bufferSize: 81920, leaveOpen: false);
        var tail = reader.ReadToEnd();
        var sanitized = SensitiveLogValue.Replace(tail, "$1[redacted]");
        WriteText(archive, "logs/" + Path.GetFileName(file), sanitized);
        return bytesToCopy;
    }
}
