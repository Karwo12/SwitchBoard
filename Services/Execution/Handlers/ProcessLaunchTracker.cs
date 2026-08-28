using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using SwitchBoard.Services.Logging;

namespace SwitchBoard.Services.Execution.Handlers;

/// <summary>
/// Records process identities before a launcher is invoked and keeps only instances
/// that appeared after that invocation. PID alone is never treated as an identity.
/// </summary>
public static class ProcessLaunchTracker
{
    private static readonly TimeSpan CaptureClockTolerance = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    public static JsonObject Capture(string processName, string? executablePath)
    {
        var normalizedName = ProcessTargetResolver.NormalizeName(processName ?? string.Empty);
        var fullPath = ProcessTargetResolver.NormalizeConfiguredPath(executablePath);
        var existing = CaptureExisting(normalizedName, fullPath);
        return new JsonObject
        {
            ["targetProcessName"] = normalizedName,
            ["targetExecutablePath"] = fullPath,
            ["processName"] = normalizedName,
            ["executablePath"] = fullPath,
            ["captureAtUtcTicks"] = DateTime.UtcNow.Ticks,
            ["preExistingProcesses"] = new JsonArray(existing.Select(RuntimeProcessIdentityService.ToJson).ToArray()),
            ["startedBySwitchBoard"] = false
        };
    }

    public static JsonObject CreateTrackingState(JsonObject? capturedState, string processName,
        string? executablePath, TimeSpan maximumWait)
    {
        var normalizedName = ProcessTargetResolver.NormalizeName(processName ?? string.Empty);
        var fullPath = ProcessTargetResolver.NormalizeConfiguredPath(executablePath);
        var wait = maximumWait < TimeSpan.Zero ? TimeSpan.Zero : maximumWait;
        return new JsonObject
        {
            ["targetProcessName"] = normalizedName,
            ["targetExecutablePath"] = fullPath,
            ["processName"] = normalizedName,
            ["executablePath"] = fullPath,
            ["captureAtUtcTicks"] = capturedState?["captureAtUtcTicks"]?.DeepClone(),
            ["preExistingProcesses"] = capturedState?["preExistingProcesses"]?.DeepClone(),
            ["startedBySwitchBoard"] = false,
            ["trackingPending"] = true,
            ["trackingDeadlineUtcTicks"] = DateTime.UtcNow.Add(wait).Ticks,
            ["launchedProcesses"] = new JsonArray()
        };
    }

    public static void ApplyTrackingResult(JsonObject state, JsonArray launchedProcesses)
    {
        state["startedBySwitchBoard"] = launchedProcesses.Count > 0;
        state["launchedProcesses"] = launchedProcesses.DeepClone();
        state["trackingPending"] = false;
    }

    public static Task TrackInBackgroundAsync(JsonObject? capturedState, string processName,
        string? executablePath, TimeSpan maximumWait, Func<JsonArray, Task> publish,
        Action<Exception>? reportError = null)
    {
        return TrackAndPublishAsync(capturedState, processName, executablePath, maximumWait,
            publish, reportError);
    }

    private static async Task TrackAndPublishAsync(JsonObject? capturedState, string processName,
        string? executablePath, TimeSpan maximumWait, Func<JsonArray, Task> publish,
        Action<Exception>? reportError)
    {
        try
        {
            await Task.Yield();
            var launched = await TrackAsync(capturedState, processName, executablePath,
                maximumWait, CancellationToken.None);
            await publish(launched);
        }
        catch (Exception exception)
        {
            reportError?.Invoke(exception);
        }
    }

    public static TimeSpan GetRemainingWait(JsonObject state)
    {
        var deadline = state["trackingDeadlineUtcTicks"]?.GetValue<long>() ?? 0;
        if (deadline <= DateTime.UtcNow.Ticks) return TimeSpan.Zero;
        return TimeSpan.FromTicks(deadline - DateTime.UtcNow.Ticks);
    }

    public static async Task<JsonArray> TrackAsync(
        JsonObject? capturedState,
        string processName,
        string? executablePath,
        TimeSpan maximumWait,
        CancellationToken cancellationToken)
    {
        var normalizedName = ProcessTargetResolver.NormalizeName(processName ?? string.Empty);
        if (string.IsNullOrWhiteSpace(normalizedName)) return [];
        var fullPath = ProcessTargetResolver.NormalizeConfiguredPath(executablePath);
        var baseline = RuntimeProcessIdentityService.ReadIdentities(
            capturedState?["preExistingProcesses"] as JsonArray);
        var captureTicks = capturedState?["captureAtUtcTicks"]?.GetValue<long>() ??
                           DateTime.UtcNow.Subtract(CaptureClockTolerance).Ticks;
        var tracked = new Dictionary<RuntimeProcessIdentityKey, JsonObject>();
        var wait = maximumWait < TimeSpan.Zero ? TimeSpan.Zero : maximumWait;
        var stopwatch = Stopwatch.StartNew();
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var process in ProcessTargetResolver.Find(normalizedName, fullPath))
            {
                try
                {
                    var identity = RuntimeProcessIdentityService.TryCapture(process);
                    if (identity is null || identity.StartedAtUtcTicks < captureTicks ||
                        RuntimeProcessIdentityService.WasPresentBefore(identity, baseline)) continue;
                    tracked[identity.Key] = RuntimeProcessIdentityService.ToJson(identity);
                }
                finally { process.Dispose(); }
            }

            if (tracked.Count > 0 || wait <= TimeSpan.Zero || stopwatch.Elapsed >= wait) break;
            var remaining = wait - stopwatch.Elapsed;
            await Task.Delay(remaining < PollInterval ? remaining : PollInterval, cancellationToken);
        } while (true);

        return new JsonArray(tracked.Values.ToArray());
    }

    public static async Task<ActionExecutionResult> CloseAsync(
        JsonObject restoreState,
        CancellationToken cancellationToken,
        IAppLogger? logger = null)
    {
        if (restoreState["trackingPending"]?.GetValue<bool>() == true)
        {
            var processName = restoreState["targetProcessName"]?.GetValue<string>()?.Trim() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(processName))
            {
                var launched = await TrackAsync(restoreState, processName,
                    restoreState["targetExecutablePath"]?.GetValue<string>(),
                    GetRemainingWait(restoreState), cancellationToken);
                ApplyTrackingResult(restoreState, launched);
            }
        }

        var identities = RuntimeProcessIdentityService.ReadIdentities(
            restoreState["launchedProcesses"] as JsonArray);
        if (identities.Count == 0 && RuntimeProcessIdentityService.ReadIdentity(restoreState) is { } legacy)
            identities.Add(legacy);
        if (identities.Count == 0)
            return ActionExecutionResult.Success("No process started by SwitchBoard remains.");

        var failures = new List<string>();
        foreach (var identity in identities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var process = Process.GetProcessById(identity.ProcessId);
                var match = RuntimeProcessIdentityService.Match(process, identity);
                if (match == RuntimeProcessMatch.NoMatch) continue;
                if (match == RuntimeProcessMatch.Unknown)
                {
                    failures.Add($"PID {identity.ProcessId}: Windows could not verify the saved process identity.");
                    continue;
                }
                var requestedName = restoreState["targetProcessName"]?.GetValue<string>() ?? identity.ProcessName;
                var requestedPath = restoreState["targetExecutablePath"]?.GetValue<string>();
                if (!ProcessTerminationGuard.TryPrepareForKill(process, requestedName, requestedPath, logger,
                        "ProcessLaunchTracker.Close", out var safetyError))
                {
                    failures.Add($"PID {identity.ProcessId}: {safetyError}");
                    continue;
                }
                process.Kill(entireProcessTree: false);
                await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
            catch (TimeoutException) { failures.Add($"PID {identity.ProcessId}: timed out while waiting for exit."); }
            catch (Win32Exception exception) { failures.Add($"PID {identity.ProcessId}: {exception.Message}"); }
        }

        var remaining = identities.Count(identity =>
            RuntimeProcessIdentityService.GetLiveMatch(identity) != RuntimeProcessMatch.NoMatch);
        return remaining == 0
            ? ActionExecutionResult.Success("Verified: the process started by SwitchBoard was closed.")
            : ActionExecutionResult.Failure(
                $"Could not close {remaining} process instance(s) started by SwitchBoard." +
                (failures.Count == 0 ? string.Empty : " " + string.Join(" ", failures)), false);
    }

    private static List<RuntimeProcessIdentity> CaptureExisting(string processName, string? executablePath)
    {
        var result = new List<RuntimeProcessIdentity>();
        if (string.IsNullOrWhiteSpace(processName)) return result;
        var lookup = ProcessTargetResolver.FindWithDiagnostics(processName, executablePath);
        try
        {
            // An inaccessible same-name path belongs in the baseline. This conservative choice can
            // only protect a pre-existing process; tracking still accepts confirmed path matches only.
            foreach (var process in lookup.Processes.Concat(lookup.PathUnverifiedProcesses))
                if (RuntimeProcessIdentityService.TryCapture(process) is { } identity) result.Add(identity);
        }
        finally { lookup.DisposeAll(); }
        return result;
    }
}
