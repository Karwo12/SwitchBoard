using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using SwitchBoard.Models.Actions;

namespace SwitchBoard.Services.Execution.Handlers;

public sealed class ProgramRunActionHandler : IReversibleActionHandler
{
    private static readonly TimeSpan IdentityTolerance = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DiscoveryWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan RestoreTimeout = TimeSpan.FromSeconds(8);

    public string ActionType => ActionTypeIds.ProgramRun;

    public async Task<ActionExecutionResult> ExecuteAsync(
        ActionDefinition action,
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var target = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.Target).Trim();
        if (string.IsNullOrWhiteSpace(target))
            return ActionExecutionResult.Failure("Program target is required.");

        var startOnlyIfNotAlreadyRunning = ActionParameterReader.ReadBoolean(
            action.Parameters, ActionParameterNames.StartOnlyIfNotAlreadyRunning, defaultValue: true);
        if (startOnlyIfNotAlreadyRunning && !IsProtocolTarget(target) && IsAlreadyRunning(target))
            return ActionExecutionResult.Skipped("The program is already running.");

        try
        {
            if (action.RestoreBehavior != ActionRestoreBehavior.CloseIfStartedBySwitchBoard)
            {
                using var untrackedProcess = Process.Start(CreateStartInfo(action, target));
                return untrackedProcess is null
                    ? ActionExecutionResult.Failure("Windows did not start the requested target.")
                    : ActionExecutionResult.Success();
            }

            var fullPath = ValidateRestorableTarget(target);
            var baseline = ReadIdentities(context.CapturedState?["preExistingProcesses"] as JsonArray);
            if (baseline.Count == 0)
                baseline = CaptureMatchingProcesses(fullPath);

            var launchedAfterTicks = DateTime.UtcNow.Subtract(IdentityTolerance).Ticks;
            using var startedProcess = Process.Start(CreateStartInfo(action, target));
            if (startedProcess is null)
                return ActionExecutionResult.Failure("Windows did not start the requested target.");

            var directProcessId = TryRead(() => startedProcess.Id) ?? 0;
            var tracked = new Dictionary<ProcessIdentityKey, TrackedProcessIdentity>();
            if (directProcessId > 0 && TryCaptureIdentity(directProcessId, parentProcessId: 0) is { } direct &&
                !WasPresentBefore(direct, baseline))
                tracked[direct.Key] = direct;

            await DiscoverLaunchedProcessesAsync(fullPath, directProcessId, launchedAfterTicks,
                baseline, tracked, cancellationToken);

            if (tracked.Count == 0)
                return ActionExecutionResult.Failure(
                    "The program started, but SwitchBoard could not identify a new process that can be closed safely.");

            var ordered = tracked.Values
                .OrderBy(identity => identity.StartedAtUtcTicks)
                .ThenBy(identity => identity.ProcessId)
                .ToList();
            var primary = ordered.FirstOrDefault(identity => identity.ProcessId == directProcessId) ?? ordered[0];
            return ActionExecutionResult.Success(restoreState: new JsonObject
            {
                ["startedBySwitchBoard"] = true,
                ["processId"] = primary.ProcessId,
                ["startedAtUtcTicks"] = primary.StartedAtUtcTicks,
                ["executablePath"] = fullPath,
                ["launchedProcesses"] = new JsonArray(ordered.Select(ToJson).ToArray())
            });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or
                                           FileNotFoundException or DirectoryNotFoundException)
        {
            return ActionExecutionResult.Failure($"Could not start '{target}': {exception.Message}");
        }
    }

    internal static ProcessStartInfo CreateStartInfo(ActionDefinition action, string target)
    {
        var arguments = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.Arguments);
        var workingDirectory = ActionParameterReader.ReadString(
            action.Parameters, ActionParameterNames.WorkingDirectory).Trim();
        var useShellExecute = IsProtocolTarget(target) ||
                              string.Equals(Path.GetExtension(target), ".lnk", StringComparison.OrdinalIgnoreCase);

        var startInfo = new ProcessStartInfo
        {
            FileName = target,
            Arguments = arguments,
            UseShellExecute = useShellExecute
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory))
            startInfo.WorkingDirectory = workingDirectory;
        else if (!useShellExecute && Path.IsPathRooted(target))
            startInfo.WorkingDirectory = Path.GetDirectoryName(target) ?? string.Empty;

        return startInfo;
    }

    internal static bool IsProtocolTarget(string target) =>
        Uri.TryCreate(target, UriKind.Absolute, out var uri) && !uri.IsFile &&
        !string.IsNullOrWhiteSpace(uri.Scheme);

    internal static bool IsAlreadyRunning(string target)
    {
        var processName = Path.GetFileNameWithoutExtension(target);
        if (string.IsNullOrWhiteSpace(processName)) return false;

        var expectedPath = Path.IsPathRooted(target) &&
                           string.Equals(Path.GetExtension(target), ".exe", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(target)
            : null;
        var processes = Process.GetProcessesByName(processName);
        try
        {
            if (expectedPath is null) return processes.Length > 0;
            foreach (var process in processes)
            {
                try
                {
                    if (PathsEqual(process.MainModule?.FileName, expectedPath)) return true;
                }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // A same-name process that Windows will not let us inspect is treated conservatively as running.
                    return true;
                }
            }
            return false;
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    public Task<JsonObject?> CaptureStateAsync(ActionDefinition action, ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.Target).Trim();
        var fullPath = ValidateRestorableTarget(target);
        var existing = CaptureMatchingProcesses(fullPath);
        return Task.FromResult<JsonObject?>(new JsonObject
        {
            ["wasRunningBefore"] = existing.Count > 0,
            ["executablePath"] = fullPath,
            ["startedBySwitchBoard"] = false,
            ["preExistingProcesses"] = new JsonArray(existing.Select(ToJson).ToArray())
        });
    }

    public async Task RestoreAsync(
        ActionDefinition action,
        JsonObject restoreState,
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (!(restoreState["startedBySwitchBoard"]?.GetValue<bool>() ?? false)) return;

        var identities = ReadIdentities(restoreState["launchedProcesses"] as JsonArray);
        if (identities.Count == 0 && ReadLegacyIdentity(restoreState) is { } legacy)
            identities.Add(legacy);
        if (identities.Count == 0)
            throw new InvalidOperationException("The saved program identity is incomplete.");

        var failures = new List<string>();
        foreach (var identity in OrderChildrenFirst(identities))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Process? process = null;
            try
            {
                process = Process.GetProcessById(identity.ProcessId);
                if (process.HasExited) continue;
                if (!IdentityMatches(process, identity)) continue;

                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken)
                    .WaitAsync(RestoreTimeout, cancellationToken);
            }
            catch (ArgumentException)
            {
                // This exact process has already exited.
            }
            catch (Win32Exception exception)
            {
                failures.Add($"PID {identity.ProcessId}: {exception.Message}");
            }
            catch (TimeoutException)
            {
                failures.Add($"PID {identity.ProcessId}: the process did not exit within {RestoreTimeout.TotalSeconds:0} seconds.");
            }
            finally
            {
                process?.Dispose();
            }
        }

        if (failures.Count > 0)
            throw new InvalidOperationException(
                "Windows could not close every program process started by SwitchBoard. " +
                "The operation may require administrator privileges. " + string.Join(" ", failures));
    }

    private static async Task DiscoverLaunchedProcessesAsync(
        string targetPath,
        int directProcessId,
        long launchedAfterTicks,
        IReadOnlyCollection<TrackedProcessIdentity> baseline,
        IDictionary<ProcessIdentityKey, TrackedProcessIdentity> tracked,
        CancellationToken cancellationToken)
    {
        var targetName = Path.GetFileNameWithoutExtension(targetPath);
        var deadline = Stopwatch.StartNew();
        var unchangedPasses = 0;
        var lastCount = -1;
        while (deadline.Elapsed < DiscoveryWindow)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var graph = CaptureProcessGraph();
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var entry in graph.Values)
                {
                    if (tracked.Keys.Any(key => key.ProcessId == entry.ProcessId)) continue;
                    var isDirect = entry.ProcessId == directProcessId;
                    var parentIsTracked = tracked.Keys.Any(key => key.ProcessId == entry.ParentProcessId);
                    var isNewMatchingTarget = string.Equals(
                        Path.GetFileNameWithoutExtension(entry.ExecutableName), targetName,
                        StringComparison.OrdinalIgnoreCase);
                    if (!isDirect && !parentIsTracked && !isNewMatchingTarget) continue;

                    var identity = TryCaptureIdentity(entry.ProcessId, entry.ParentProcessId);
                    if (identity is null || WasPresentBefore(identity, baseline)) continue;
                    if (!isDirect && !parentIsTracked)
                    {
                        if (identity.StartedAtUtcTicks < launchedAfterTicks ||
                            string.IsNullOrWhiteSpace(identity.ExecutablePath) ||
                            !PathsEqual(identity.ExecutablePath, targetPath))
                            continue;
                    }
                    tracked[identity.Key] = identity;
                    changed = true;
                }
            }

            unchangedPasses = tracked.Count == lastCount ? unchangedPasses + 1 : 0;
            lastCount = tracked.Count;
            if (tracked.Count > 0 && unchangedPasses >= 5 && deadline.Elapsed >= TimeSpan.FromMilliseconds(650))
                break;
            await Task.Delay(DiscoveryInterval, cancellationToken);
        }
    }

    private static List<TrackedProcessIdentity> CaptureMatchingProcesses(string fullPath)
    {
        var result = new List<TrackedProcessIdentity>();
        var name = Path.GetFileNameWithoutExtension(fullPath);
        var graph = CaptureProcessGraph();
        foreach (var entry in graph.Values.Where(entry => string.Equals(
                     Path.GetFileNameWithoutExtension(entry.ExecutableName), name,
                     StringComparison.OrdinalIgnoreCase)))
        {
            var identity = TryCaptureIdentity(entry.ProcessId, entry.ParentProcessId);
            if (identity is null) continue;
            // If path inspection is denied, include the same-name process in the baseline. This can only
            // prevent a close; it can never cause a pre-existing process to be killed.
            if (string.IsNullOrWhiteSpace(identity.ExecutablePath) || PathsEqual(identity.ExecutablePath, fullPath))
                result.Add(identity);
        }
        return result;
    }

    private static TrackedProcessIdentity? TryCaptureIdentity(int processId, int parentProcessId)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId);
            if (process.HasExited) return null;
            var started = process.StartTime.ToUniversalTime().Ticks;
            string? path = null;
            try { path = process.MainModule?.FileName; }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException) { }
            return new(processId, parentProcessId, started,
                string.IsNullOrWhiteSpace(path) ? null : Path.GetFullPath(path), process.ProcessName);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static bool WasPresentBefore(TrackedProcessIdentity identity,
        IReadOnlyCollection<TrackedProcessIdentity> baseline) => baseline.Any(existing =>
        existing.ProcessId == identity.ProcessId &&
        (existing.StartedAtUtcTicks <= 0 || Math.Abs(existing.StartedAtUtcTicks - identity.StartedAtUtcTicks) <= IdentityTolerance.Ticks));

    private static bool IdentityMatches(Process process, TrackedProcessIdentity identity)
    {
        long actualStarted;
        try { actualStarted = process.StartTime.ToUniversalTime().Ticks; }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) { return false; }
        if (identity.StartedAtUtcTicks <= 0 ||
            Math.Abs(actualStarted - identity.StartedAtUtcTicks) > IdentityTolerance.Ticks)
            return false;
        if (string.IsNullOrWhiteSpace(identity.ExecutablePath)) return true;
        try { return PathsEqual(process.MainModule?.FileName, identity.ExecutablePath); }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            // Start time + PID still identify the exact process. Path verification is best-effort.
            return true;
        }
    }

    private static IEnumerable<TrackedProcessIdentity> OrderChildrenFirst(
        IReadOnlyCollection<TrackedProcessIdentity> identities)
    {
        var ids = identities.Select(identity => identity.ProcessId).ToHashSet();
        int Depth(TrackedProcessIdentity identity)
        {
            var depth = 0;
            var parent = identity.ParentProcessId;
            var visited = new HashSet<int>();
            while (parent > 0 && ids.Contains(parent) && visited.Add(parent))
            {
                depth++;
                parent = identities.First(candidate => candidate.ProcessId == parent).ParentProcessId;
            }
            return depth;
        }
        return identities.OrderByDescending(Depth).ThenByDescending(identity => identity.StartedAtUtcTicks);
    }

    private static JsonObject ToJson(TrackedProcessIdentity identity) => new()
    {
        ["processId"] = identity.ProcessId,
        ["parentProcessId"] = identity.ParentProcessId,
        ["startedAtUtcTicks"] = identity.StartedAtUtcTicks,
        ["executablePath"] = identity.ExecutablePath,
        ["processName"] = identity.ProcessName
    };

    private static List<TrackedProcessIdentity> ReadIdentities(JsonArray? array)
    {
        var result = new List<TrackedProcessIdentity>();
        if (array is null) return result;
        foreach (var node in array.OfType<JsonObject>())
        {
            var pid = node["processId"]?.GetValue<int>() ?? 0;
            var ticks = node["startedAtUtcTicks"]?.GetValue<long>() ?? 0;
            if (pid <= 0 || ticks <= 0) continue;
            result.Add(new(pid,
                node["parentProcessId"]?.GetValue<int>() ?? 0,
                ticks,
                node["executablePath"]?.GetValue<string>(),
                node["processName"]?.GetValue<string>() ?? string.Empty));
        }
        return result;
    }

    private static TrackedProcessIdentity? ReadLegacyIdentity(JsonObject state)
    {
        var pid = state["processId"]?.GetValue<int>() ?? 0;
        var ticks = state["startedAtUtcTicks"]?.GetValue<long>() ?? 0;
        if (pid <= 0 || ticks <= 0) return null;
        return new(pid, 0, ticks, state["executablePath"]?.GetValue<string>(), string.Empty);
    }

    private static string ValidateRestorableTarget(string target)
    {
        if (!Path.IsPathRooted(target) ||
            !string.Equals(Path.GetExtension(target), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Close-on-restore requires a full path to an executable file.");
        return Path.GetFullPath(target);
    }

    private static bool PathsEqual(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second)) return false;
        try { return string.Equals(Path.GetFullPath(first), Path.GetFullPath(second), StringComparison.OrdinalIgnoreCase); }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static T? TryRead<T>(Func<T> read) where T : struct
    {
        try { return read(); }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) { return null; }
    }

    private static Dictionary<int, ProcessGraphEntry> CaptureProcessGraph()
    {
        var result = new Dictionary<int, ProcessGraphEntry>();
        var snapshot = CreateToolhelp32Snapshot(Th32CsSnapProcess, 0);
        if (snapshot == InvalidHandleValue) return result;
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry)) return result;
            do
            {
                var pid = unchecked((int)entry.ProcessId);
                if (pid > 0) result[pid] = new(pid, unchecked((int)entry.ParentProcessId), entry.ExecutableFile ?? string.Empty);
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            } while (Process32Next(snapshot, ref entry));
            return result;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    private readonly record struct ProcessIdentityKey(int ProcessId, long StartedAtUtcTicks);
    private sealed record TrackedProcessIdentity(
        int ProcessId,
        int ParentProcessId,
        long StartedAtUtcTicks,
        string? ExecutablePath,
        string ProcessName)
    {
        public ProcessIdentityKey Key => new(ProcessId, StartedAtUtcTicks);
    }
    private sealed record ProcessGraphEntry(int ProcessId, int ParentProcessId, string ExecutableName);

    private const uint Th32CsSnapProcess = 0x00000002;
    private static readonly nint InvalidHandleValue = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string? ExecutableFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
