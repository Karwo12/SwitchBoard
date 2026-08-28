using System.IO.Compression;
using System.IO;
using System.Text;
using System.Text.Json;
using SwitchBoard.Data;
using SwitchBoard.Services.Activity;

namespace SwitchBoard.Services.Diagnostics;

/// <summary>Creates local, opt-in diagnostic archives without including profiles or settings.</summary>
public sealed class DiagnosticExportService
{
    private readonly AppDataPaths _paths;
    private readonly JsonSerializerOptions _json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public DiagnosticExportService(AppDataPaths paths) => _paths = paths;

    public async Task ExportDiagnosticsAsync(string destination, string diagnostics,
        IReadOnlyList<PersistentActivityRecord> records, CancellationToken cancellationToken = default)
    {
        await WriteArchiveAsync(destination, archive =>
        {
            WriteText(archive, "diagnostics.txt", diagnostics);
            WriteText(archive, "history.json", JsonSerializer.Serialize(records, _json));
            if (Directory.Exists(_paths.LogsDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(_paths.LogsDirectory, "switchboard.log*"))
                    AddLog(archive, file);
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

    private static void AddLog(ZipArchive archive, string file)
    {
        var entry = archive.CreateEntry("logs/" + Path.GetFileName(file), CompressionLevel.Optimal);
        using var target = entry.Open();
        using var source = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        source.CopyTo(target);
    }
}
