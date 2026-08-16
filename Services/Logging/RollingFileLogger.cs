using System.IO;
using System.Text;
using SwitchBoard.Data;

namespace SwitchBoard.Services.Logging;

public sealed class RollingFileLogger(AppDataPaths paths) : IAppLogger
{
    private const long MaxBytes = 1024 * 1024;
    private readonly object _gate = new();

    public void Info(string area, string message) => Write("INFO", area, message);
    public void Warning(string area, string message) => Write("WARN", area, message);
    public void Error(string area, Exception exception, string? message = null) =>
        Write("ERROR", area, $"{message ?? exception.Message} | {exception.GetType().Name}: {exception.Message}");

    private void Write(string level, string area, string message)
    {
        try
        {
            lock (_gate)
            {
                Directory.CreateDirectory(paths.LogsDirectory);
                var current = Path.Combine(paths.LogsDirectory, "switchboard.log");
                if (File.Exists(current) && new FileInfo(current).Length >= MaxBytes) Rotate(current);
                var safe = Sanitize(message);
                File.AppendAllText(current,
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] [{Sanitize(area)}] {safe}{Environment.NewLine}",
                    new UTF8Encoding(false));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Logging must never take down the application.
        }
    }

    private static void Rotate(string current)
    {
        var third = current + ".3";
        var second = current + ".2";
        var first = current + ".1";
        if (File.Exists(third)) File.Delete(third);
        if (File.Exists(second)) File.Move(second, third);
        if (File.Exists(first)) File.Move(first, second);
        File.Move(current, first);
    }

    private static string Sanitize(string? value)
    {
        var clean = (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Replace('\0', ' ');
        return clean.Length <= 4000 ? clean : clean[..4000] + "…";
    }
}
