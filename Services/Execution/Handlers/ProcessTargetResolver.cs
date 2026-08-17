using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace SwitchBoard.Services.Execution.Handlers;

internal static class ProcessTargetResolver
{
    public static string NormalizeName(string value) => Path.GetFileNameWithoutExtension(value.Trim());

    public static List<Process> Find(string processName, string? executablePath = null, int? processIdHint = null)
    {
        var result = new List<Process>();
        var normalized = NormalizeName(processName);
        if (string.IsNullOrWhiteSpace(normalized)) return result;

        if (processIdHint is > 0)
        {
            try
            {
                var hinted = Process.GetProcessById(processIdHint.Value);
                if (!hinted.HasExited && NameMatches(hinted, normalized) && PathMatches(hinted, executablePath))
                    result.Add(hinted);
                else hinted.Dispose();
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }

        foreach (var process in Process.GetProcessesByName(normalized))
        {
            if (result.Any(item => item.Id == process.Id)) { process.Dispose(); continue; }
            if (PathMatches(process, executablePath)) result.Add(process);
            else process.Dispose();
        }
        return result;
    }

    private static bool NameMatches(Process process, string normalized)
    {
        try { return string.Equals(process.ProcessName, normalized, StringComparison.OrdinalIgnoreCase); }
        catch (InvalidOperationException) { return false; }
    }

    private static bool PathMatches(Process process, string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return true;
        try
        {
            return string.Equals(Path.GetFullPath(process.MainModule?.FileName ?? string.Empty),
                Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or
                                           NotSupportedException or ArgumentException) { return false; }
    }
}
