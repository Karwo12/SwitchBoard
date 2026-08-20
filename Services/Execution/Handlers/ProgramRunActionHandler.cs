using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using SwitchBoard.Models.Actions;
using SwitchBoard.Localization;

namespace SwitchBoard.Services.Execution.Handlers;

public sealed class ProgramRunActionHandler(ILocalizationService? localization = null) : IReversibleActionHandler
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

        var legacyStartOnly = ActionParameterReader.ReadBoolean(
            action.Parameters, ActionParameterNames.StartOnlyIfNotAlreadyRunning, defaultValue: true);
        var instanceBehavior = ActionParameterReader.ReadString(action.Parameters,
            ActionParameterNames.InstanceBehavior);
        if (string.IsNullOrWhiteSpace(instanceBehavior))
            instanceBehavior = legacyStartOnly ? InstanceBehaviorIds.DoNotStartAgain : InstanceBehaviorIds.StartAnother;
        if (instanceBehavior == InstanceBehaviorIds.DoNotStartAgain && !IsProtocolTarget(target) && IsAlreadyRunning(target))
            return ActionExecutionResult.Skipped("The program is already running.");

        try
        {
            if (instanceBehavior == InstanceBehaviorIds.RestartExisting)
                await StopExistingExactAsync(target, cancellationToken);
            if (action.RestoreBehavior != ActionRestoreBehavior.CloseIfStartedBySwitchBoard)
            {
                using var untrackedProcess = Process.Start(CreateStartInfo(action, target));
                if (untrackedProcess is null)
                    return ActionExecutionResult.Failure("Windows did not start the requested target.");
                var windowResult = await ApplyWindowBehaviorAsync(action, target, cancellationToken);
                if (windowResult is not null) return windowResult;
                if (IsProtocolTarget(target) || string.Equals(Path.GetExtension(target), ".lnk", StringComparison.OrdinalIgnoreCase))
                    return ActionExecutionResult.Success(
                        "Windows accepted the shell handoff. The target application cannot be identified reliably for full verification.");
                var expectedName = Path.GetFileNameWithoutExtension(target);
                var expectedPath = Path.IsPathRooted(target) &&
                                   string.Equals(Path.GetExtension(target), ".exe", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetFullPath(target) : null;
                var verification = ProcessTargetResolver.Find(expectedName, expectedPath);
                try
                {
                    return verification.Count > 0
                        ? ActionExecutionResult.Success($"Verified: '{expectedName}' is running.")
                        : ActionExecutionResult.Failure(
                            $"Windows accepted the start request, but no matching '{expectedName}' process was found.");
                }
                finally { foreach (var process in verification) process.Dispose(); }
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
                return baseline.Count > 0
                    ? ActionExecutionResult.Skipped(
                        "Windows handed the request to the already running application; no new instance was identified for restore.")
                    : ActionExecutionResult.Failure(
                        "The program start request completed, but SwitchBoard could not verify a new process that can be closed safely.");

            var windowBehaviorResult = await ApplyWindowBehaviorAsync(action, target, cancellationToken);
            if (windowBehaviorResult is not null && !windowBehaviorResult.IsSuccessful) return windowBehaviorResult;
            var ordered = tracked.Values
                .OrderBy(identity => identity.StartedAtUtcTicks)
                .ThenBy(identity => identity.ProcessId)
                .ToList();
            var primary = ordered.FirstOrDefault(identity => identity.ProcessId == directProcessId) ?? ordered[0];
            return ActionExecutionResult.Success(
                $"Verified: identified {ordered.Count} new '{primary.ProcessName}' process(es) started by SwitchBoard.",
                new JsonObject
            {
                ["startedBySwitchBoard"] = true,
                ["processId"] = primary.ProcessId,
                ["startedAtUtcTicks"] = primary.StartedAtUtcTicks,
                ["executablePath"] = fullPath,
                ["processName"] = Path.GetFileNameWithoutExtension(fullPath),
                ["captureAtUtcTicks"] = context.CapturedState?["captureAtUtcTicks"]?.GetValue<long>() ?? launchedAfterTicks,
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
        var storedWorkingDirectory = ActionParameterReader.ReadString(
            action.Parameters, ActionParameterNames.WorkingDirectory).Trim();
        var useCustomWorkingDirectory = ActionParameterReader.ReadBoolean(
            action.Parameters, ActionParameterNames.UseCustomWorkingDirectory,
            !string.IsNullOrWhiteSpace(storedWorkingDirectory));
        var workingDirectory = useCustomWorkingDirectory ? storedWorkingDirectory : string.Empty;
        var useShellExecute = IsProtocolTarget(target) ||
                              string.Equals(Path.GetExtension(target), ".lnk", StringComparison.OrdinalIgnoreCase) ||
                              ActionParameterReader.ReadBoolean(action.Parameters,
                                  ActionParameterNames.RunAsAdministrator, false);

        var startInfo = new ProcessStartInfo
        {
            FileName = target,
            Arguments = arguments,
            UseShellExecute = useShellExecute
        };
        if (ActionParameterReader.ReadBoolean(action.Parameters, ActionParameterNames.RunAsAdministrator, false))
            startInfo.Verb = "runas";
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

    private static async Task StopExistingExactAsync(string target, CancellationToken cancellationToken)
    {
        if (IsProtocolTarget(target) || !Path.IsPathRooted(target) ||
            !string.Equals(Path.GetExtension(target), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Restart requires a full path to an EXE so the existing process can be identified safely.");
        var fullPath = Path.GetFullPath(target);
        var matches = ProcessTargetResolver.Find(Path.GetFileNameWithoutExtension(fullPath), fullPath);
        try
        {
            foreach (var process in matches)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited) continue;
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            }
        }
        finally { foreach (var process in matches) process.Dispose(); }
    }

    private static async Task<ActionExecutionResult?> ApplyWindowBehaviorAsync(ActionDefinition action,
        string target, CancellationToken cancellationToken)
    {
        var behavior = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.WindowBehavior);
        if (string.IsNullOrWhiteSpace(behavior) || behavior == WindowBehaviorIds.None) return null;
        if (IsProtocolTarget(target))
            return ActionExecutionResult.Failure("Window behavior for protocol targets requires a separate Wait for window action.", false);
        var processName = Path.GetFileNameWithoutExtension(target);
        var path = Path.IsPathRooted(target) ? Path.GetFullPath(target) : null;
        var seconds = Math.Clamp(ActionParameterReader.ReadInt32(action.Parameters,
            ActionParameterNames.WindowWaitSeconds, 10), 1, 300);
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var windows = WindowInterop.FindWindows(processName, path, WindowMatchModeIds.Any, string.Empty);
            if (windows.Count > 0)
            {
                WindowInterop.ApplyBehavior(windows[0].Handle, behavior);
                return null;
            }
            await Task.Delay(150, cancellationToken);
        }
        return ActionExecutionResult.Failure($"The program window did not appear within {seconds} seconds.");
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
            ["processName"] = Path.GetFileNameWithoutExtension(fullPath),
            ["captureAtUtcTicks"] = DateTime.UtcNow.Ticks,
            ["startedBySwitchBoard"] = false,
            ["preExistingProcesses"] = new JsonArray(existing.Select(ToJson).ToArray())
        });
    }

    public async Task<ActionExecutionResult> RestoreAsync(
        ActionDefinition action,
        JsonObject restoreState,
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (!(restoreState["startedBySwitchBoard"]?.GetValue<bool>() ?? false))
            return ActionExecutionResult.Skipped("SwitchBoard did not start an independently identifiable program instance.");

        var identities = ReadIdentities(restoreState["launchedProcesses"] as JsonArray);
        if (identities.Count == 0 && ReadLegacyIdentity(restoreState) is { } legacy)
            identities.Add(legacy);
        var processName = restoreState["processName"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(processName))
            processName = Path.GetFileNameWithoutExtension(restoreState["executablePath"]?.GetValue<string>() ?? string.Empty);
        if (identities.Count == 0 || string.IsNullOrWhiteSpace(processName))
            return ActionExecutionResult.Failure("The saved program identity is incomplete.", false);

        var baseline = ReadIdentities(restoreState["preExistingProcesses"] as JsonArray);
        var captureTicks = restoreState["captureAtUtcTicks"]?.GetValue<long>() ??
                           identities.Min(identity => identity.StartedAtUtcTicks) - IdentityTolerance.Ticks;
        var fullPath = restoreState["executablePath"]?.GetValue<string>();
        var killed = 0;
        var failures = new List<string>();
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < RestoreTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = FindRestoreCandidates(processName, fullPath, baseline, identities, captureTicks);
            if (candidates.Count == 0)
                return ActionExecutionResult.Success(
                    Format("Result.ProgramRestoreSuccess",
                        "Verified: the program was closed. Closed {0} '{1}.exe' process(es); none remain.", killed, processName));
            foreach (var candidate in OrderChildrenFirst(candidates))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var process = Process.GetProcessById(candidate.ProcessId);
                    if (process.HasExited || !IdentityMatches(process, candidate)) continue;
                    process.Kill(entireProcessTree: true);
                    killed++;
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                catch (Win32Exception exception) { failures.Add($"PID {candidate.ProcessId}: {exception.Message}"); }
            }
            await Task.Delay(200, cancellationToken);
        }

        var remaining = FindRestoreCandidates(processName, fullPath, baseline, identities, captureTicks);
        return ActionExecutionResult.Failure(
            Format("Result.ProgramRestoreFailed",
                "Could not close the program. {0} '{1}.exe' process(es) started by SwitchBoard remain.",
                remaining.Count, processName) +
            (failures.Count == 0 ? string.Empty : " Administrator privileges may be required. " + string.Join(" ", failures.Distinct())));
    }

    private string Format(string key, string fallback, params object?[] arguments) => localization is null
        ? string.Format(System.Globalization.CultureInfo.CurrentCulture, fallback, arguments)
        : localization.Format(key, arguments);

    private static List<TrackedProcessIdentity> FindRestoreCandidates(string processName, string? fullPath,
        IReadOnlyCollection<TrackedProcessIdentity> baseline, IReadOnlyCollection<TrackedProcessIdentity> originallyTracked,
        long captureTicks)
    {
        var result = new List<TrackedProcessIdentity>();
        var graph = CaptureProcessGraph();
        foreach (var entry in graph.Values)
        {
            var identity = TryCaptureIdentity(entry.ProcessId, entry.ParentProcessId);
            if (identity is null) continue;
            var exactName = string.Equals(Path.GetFileNameWithoutExtension(entry.ExecutableName), processName,
                StringComparison.OrdinalIgnoreCase);
            var wasTracked = originallyTracked.Any(tracked => tracked.Key == identity.Key);
            var descendantOfTracked = IsDescendantOf(entry.ProcessId,
                originallyTracked.Select(item => item.ProcessId).ToHashSet(), graph);
            // Persisted tracked identities and their descendants are owned even when the child executable
            // has a different name from the launcher.
            if (wasTracked || descendantOfTracked)
            {
                result.Add(identity);
                continue;
            }
            if (!exactName || WasPresentBefore(identity, baseline)) continue;
            if (baseline.Count == 0)
            {
                // Safe fallback: no exact-name process existed at capture time.
                result.Add(identity);
                continue;
            }
            var newExactExecutable = identity.StartedAtUtcTicks >= captureTicks &&
                                     (string.IsNullOrWhiteSpace(fullPath) ||
                                      !string.IsNullOrWhiteSpace(identity.ExecutablePath) && PathsEqual(identity.ExecutablePath, fullPath));
            if (newExactExecutable) result.Add(identity);
        }
        return result;
    }

    private static bool IsDescendantOf(int processId, HashSet<int> ancestors,
        IReadOnlyDictionary<int, ProcessGraphEntry> graph)
    {
        var visited = new HashSet<int>();
        var current = processId;
        while (graph.TryGetValue(current, out var entry) && entry.ParentProcessId > 0 && visited.Add(current))
        {
            if (ancestors.Contains(entry.ParentProcessId)) return true;
            current = entry.ParentProcessId;
        }
        return false;
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
