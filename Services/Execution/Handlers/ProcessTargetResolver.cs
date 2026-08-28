using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace SwitchBoard.Services.Execution.Handlers;

internal enum ProcessTargetMatch
{
    Match,
    NoMatch,
    PathUnavailable
}

internal sealed class ProcessLookupResult
{
    public ProcessLookupResult(List<Process> processes, List<Process> pathUnverifiedProcesses,
        string? errorMessage)
    {
        Processes = processes;
        PathUnverifiedProcesses = pathUnverifiedProcesses;
        ErrorMessage = errorMessage;
    }

    public List<Process> Processes { get; }
    public List<Process> PathUnverifiedProcesses { get; }
    public int InspectionFailures => PathUnverifiedProcesses.Count;
    public string? ErrorMessage { get; }
    public bool CanSafelyConcludeNoMatch => ErrorMessage is null && Processes.Count == 0 && InspectionFailures == 0;

    public void DisposePathUnverified()
    {
        foreach (var process in PathUnverifiedProcesses) process.Dispose();
        PathUnverifiedProcesses.Clear();
    }

    public void DisposeAll()
    {
        foreach (var process in Processes) process.Dispose();
        Processes.Clear();
        DisposePathUnverified();
    }
}

/// <summary>
/// Resolves a persistent process target: exact normalized process name and an optional full EXE path.
/// A PID is accepted only as an enumeration hint and never bypasses target verification.
/// </summary>
internal static class ProcessTargetResolver
{
    public static string NormalizeName(string value)
    {
        var fileName = Path.GetFileName(value?.Trim() ?? string.Empty);
        return fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^4]
            : fileName;
    }

    public static bool NamesEqual(string first, string second)
    {
        var normalizedFirst = NormalizeName(first);
        return !string.IsNullOrWhiteSpace(normalizedFirst) &&
               string.Equals(normalizedFirst, NormalizeName(second), StringComparison.OrdinalIgnoreCase);
    }

    public static bool PathsEqual(string? first, string? second)
    {
        var normalizedFirst = RuntimeProcessIdentityService.NormalizePath(first);
        var normalizedSecond = RuntimeProcessIdentityService.NormalizePath(second);
        return normalizedFirst is not null && normalizedSecond is not null &&
               string.Equals(normalizedFirst, normalizedSecond, StringComparison.OrdinalIgnoreCase);
    }

    public static string? NormalizeConfiguredPath(string? executablePath)
    {
        if (string.IsNullOrWhiteSpace(executablePath)) return null;
        var trimmed = executablePath.Trim();
        if (!Path.IsPathRooted(trimmed) ||
            !string.Equals(Path.GetExtension(trimmed), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The optional process path must be a full path to an EXE.");
        return RuntimeProcessIdentityService.NormalizePath(trimmed) ??
               throw new InvalidOperationException("The executable path is invalid.");
    }

    public static List<Process> Find(string processName, string? executablePath = null, int? processIdHint = null)
    {
        var lookup = FindWithDiagnostics(processName, executablePath, processIdHint);
        lookup.DisposePathUnverified();
        return lookup.Processes;
    }

    public static ProcessLookupResult FindWithDiagnostics(string processName, string? executablePath = null,
        int? processIdHint = null)
    {
        var normalizedName = NormalizeName(processName);
        if (string.IsNullOrWhiteSpace(normalizedName))
            return new ProcessLookupResult([], [], null);

        string? normalizedPath;
        try
        {
            normalizedPath = NormalizeConfiguredPath(executablePath);
        }
        catch (InvalidOperationException exception)
        {
            return new ProcessLookupResult([], [], exception.Message);
        }

        var matching = new List<Process>();
        var pathUnverified = new List<Process>();
        var seenProcessIds = new HashSet<int>();

        if (processIdHint is > 0)
        {
            try
            {
                var hinted = Process.GetProcessById(processIdHint.Value);
                AddCandidate(hinted, normalizedName, normalizedPath, matching, pathUnverified, seenProcessIds);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
            {
                // The hinted instance disappeared or is inaccessible. Normal exact-name discovery still runs.
            }
        }

        Process[] candidates;
        try { candidates = Process.GetProcessesByName(normalizedName); }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            return new ProcessLookupResult(matching, pathUnverified, null);
        }

        foreach (var process in candidates)
            AddCandidate(process, normalizedName, normalizedPath, matching, pathUnverified, seenProcessIds);

        return new ProcessLookupResult(matching, pathUnverified, null);
    }

    internal static ProcessTargetMatch MatchesSnapshot(string configuredProcessName, string? configuredExecutablePath,
        string actualProcessName, string? actualExecutablePath, bool pathAvailable)
    {
        if (!NamesEqual(configuredProcessName, actualProcessName)) return ProcessTargetMatch.NoMatch;
        if (string.IsNullOrWhiteSpace(configuredExecutablePath)) return ProcessTargetMatch.Match;
        if (!pathAvailable) return ProcessTargetMatch.PathUnavailable;
        return PathsEqual(configuredExecutablePath, actualExecutablePath)
            ? ProcessTargetMatch.Match
            : ProcessTargetMatch.NoMatch;
    }

    private static void AddCandidate(Process process, string normalizedName, string? normalizedPath,
        ICollection<Process> matching, ICollection<Process> pathUnverified, ISet<int> seenProcessIds)
    {
        try
        {
            var processId = process.Id;
            if (!seenProcessIds.Add(processId))
            {
                process.Dispose();
                return;
            }

            // Never expose the current SwitchBoard process as a destructive target, even if
            // a malformed name/path or a stale PID hint happens to point at it.
            if (ProcessTerminationGuard.IsCurrentProcessId(processId))
            {
                process.Dispose();
                return;
            }

            if (process.HasExited)
            {
                process.Dispose();
                return;
            }

            var actualName = process.ProcessName;
            string? actualPath = null;
            var pathAvailable = string.IsNullOrWhiteSpace(normalizedPath) ||
                                RuntimeProcessIdentityService.TryReadPath(process, out actualPath);
            switch (MatchesSnapshot(normalizedName, normalizedPath, actualName, actualPath, pathAvailable))
            {
                case ProcessTargetMatch.Match:
                    matching.Add(process);
                    break;
                case ProcessTargetMatch.PathUnavailable:
                    pathUnverified.Add(process);
                    break;
                default:
                    process.Dispose();
                    break;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception or
                                           NotSupportedException)
        {
            process.Dispose();
        }
    }
}
