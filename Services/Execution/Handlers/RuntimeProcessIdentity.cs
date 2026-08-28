using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;

namespace SwitchBoard.Services.Execution.Handlers;

internal enum RuntimeProcessMatch
{
    NoMatch,
    Match,
    Unknown
}

internal readonly record struct RuntimeProcessIdentityKey(int ProcessId, long StartedAtUtcTicks);

/// <summary>
/// Identifies one concrete process instance. PID is never sufficient on its own because Windows can reuse it.
/// </summary>
internal sealed record RuntimeProcessIdentity(
    int ProcessId,
    int ParentProcessId,
    long StartedAtUtcTicks,
    string? ExecutablePath,
    string ProcessName)
{
    public RuntimeProcessIdentityKey Key => new(ProcessId, StartedAtUtcTicks);
}

internal static class RuntimeProcessIdentityService
{
    public static RuntimeProcessIdentity? TryCapture(Process process, int parentProcessId = 0)
    {
        try
        {
            if (process.HasExited) return null;
            var processId = process.Id;
            var startedAtUtcTicks = process.StartTime.ToUniversalTime().Ticks;
            var processName = ProcessTargetResolver.NormalizeName(process.ProcessName);
            if (processId <= 0 || startedAtUtcTicks <= 0 || string.IsNullOrWhiteSpace(processName)) return null;
            TryReadPath(process, out var executablePath);
            return new RuntimeProcessIdentity(processId, parentProcessId, startedAtUtcTicks,
                executablePath, processName);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    public static RuntimeProcessIdentity? TryCapture(int processId, int parentProcessId = 0)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return TryCapture(process, parentProcessId);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
    }

    public static RuntimeProcessMatch Match(Process process, RuntimeProcessIdentity identity)
    {
        try
        {
            if (process.HasExited) return RuntimeProcessMatch.NoMatch;
            var processId = process.Id;
            var startedAtUtcTicks = process.StartTime.ToUniversalTime().Ticks;
            var processName = process.ProcessName;
            var pathAvailable = TryReadPath(process, out var executablePath);
            return MatchesSnapshot(identity, processId, startedAtUtcTicks, processName,
                executablePath, pathAvailable)
                ? RuntimeProcessMatch.Match
                : RuntimeProcessMatch.NoMatch;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return RuntimeProcessMatch.NoMatch;
        }
        catch (Win32Exception)
        {
            return RuntimeProcessMatch.Unknown;
        }
    }

    internal static bool MatchesSnapshot(RuntimeProcessIdentity identity, int processId,
        long startedAtUtcTicks, string processName, string? executablePath, bool pathAvailable)
    {
        if (identity.ProcessId != processId || identity.StartedAtUtcTicks <= 0 ||
            identity.StartedAtUtcTicks != startedAtUtcTicks)
            return false;
        if (!string.IsNullOrWhiteSpace(identity.ProcessName) &&
            !ProcessTargetResolver.NamesEqual(identity.ProcessName, processName))
            return false;

        // Exact PID + StartTime + name identify the instance. A readable path must also agree,
        // but access denied at restore time must not turn a known instance into another process.
        return string.IsNullOrWhiteSpace(identity.ExecutablePath) || !pathAvailable ||
               ProcessTargetResolver.PathsEqual(identity.ExecutablePath, executablePath);
    }

    public static bool SameInstance(RuntimeProcessIdentity first, RuntimeProcessIdentity second)
    {
        if (first.ProcessId != second.ProcessId || first.StartedAtUtcTicks <= 0 ||
            first.StartedAtUtcTicks != second.StartedAtUtcTicks)
            return false;
        if (!string.IsNullOrWhiteSpace(first.ProcessName) && !string.IsNullOrWhiteSpace(second.ProcessName) &&
            !ProcessTargetResolver.NamesEqual(first.ProcessName, second.ProcessName))
            return false;
        return string.IsNullOrWhiteSpace(first.ExecutablePath) || string.IsNullOrWhiteSpace(second.ExecutablePath) ||
               ProcessTargetResolver.PathsEqual(first.ExecutablePath, second.ExecutablePath);
    }

    public static bool WasPresentBefore(RuntimeProcessIdentity identity,
        IReadOnlyCollection<RuntimeProcessIdentity> baseline) =>
        baseline.Any(existing => SameInstance(existing, identity));

    public static RuntimeProcessMatch GetLiveMatch(RuntimeProcessIdentity identity)
    {
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            return Match(process, identity);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return RuntimeProcessMatch.NoMatch;
        }
        catch (Win32Exception)
        {
            return RuntimeProcessMatch.Unknown;
        }
    }

    public static JsonObject ToJson(RuntimeProcessIdentity identity) => new()
    {
        ["processId"] = identity.ProcessId,
        ["parentProcessId"] = identity.ParentProcessId,
        ["startedAtUtcTicks"] = identity.StartedAtUtcTicks,
        ["executablePath"] = identity.ExecutablePath,
        ["processName"] = identity.ProcessName
    };

    public static List<RuntimeProcessIdentity> ReadIdentities(JsonArray? array)
    {
        var result = new List<RuntimeProcessIdentity>();
        if (array is null) return result;
        foreach (var node in array.OfType<JsonObject>())
        {
            if (ReadIdentity(node) is { } identity) result.Add(identity);
        }
        return result;
    }

    public static RuntimeProcessIdentity? ReadIdentity(JsonObject state)
    {
        var processId = state["processId"]?.GetValue<int>() ?? 0;
        var startedAtUtcTicks = state["startedAtUtcTicks"]?.GetValue<long>() ?? 0;
        if (processId <= 0 || startedAtUtcTicks <= 0) return null;
        return new RuntimeProcessIdentity(processId,
            state["parentProcessId"]?.GetValue<int>() ?? 0,
            startedAtUtcTicks,
            NormalizePath(state["executablePath"]?.GetValue<string>()),
            ProcessTargetResolver.NormalizeName(state["processName"]?.GetValue<string>() ?? string.Empty));
    }

    public static bool TryReadPath(Process process, out string? executablePath)
    {
        try
        {
            executablePath = NormalizePath(process.MainModule?.FileName);
            return !string.IsNullOrWhiteSpace(executablePath);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or
                                           NotSupportedException or ArgumentException)
        {
            executablePath = null;
            return false;
        }
    }

    public static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try { return Path.GetFullPath(path.Trim()); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
