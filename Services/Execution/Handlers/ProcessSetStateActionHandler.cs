using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using SwitchBoard.Models.Actions;
using SwitchBoard.Services.Logging;

namespace SwitchBoard.Services.Execution.Handlers;

public sealed class ProcessSetStateActionHandler : IReversibleActionHandler
{
    private static readonly TimeSpan DefaultExitTimeout = TimeSpan.FromSeconds(5);
    private readonly IAppLogger? _logger;

    public ProcessSetStateActionHandler(IAppLogger? logger = null) => _logger = logger;

    public string ActionType => ActionTypeIds.ProcessSetState;

    public async Task<ActionExecutionResult> ExecuteAsync(
        ActionDefinition action,
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var desiredState = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.DesiredState);
        if (string.IsNullOrWhiteSpace(desiredState))
        {
            desiredState = ProcessDesiredStateIds.Stopped;
        }

        if (string.Equals(desiredState, ProcessDesiredStateIds.Unchanged, StringComparison.OrdinalIgnoreCase))
        {
            return ActionExecutionResult.Skipped("The desired process state is Unchanged.");
        }

        if (!string.Equals(desiredState, ProcessDesiredStateIds.Stopped, StringComparison.OrdinalIgnoreCase))
        {
            return ActionExecutionResult.Failure($"Unsupported desired process state '{desiredState}'.");
        }

        var processName = ProcessTargetResolver.NormalizeName(ActionParameterReader.ReadString(
            action.Parameters,
            ActionParameterNames.ProcessName));
        if (string.IsNullOrWhiteSpace(processName))
        {
            return ActionExecutionResult.Failure("Process name is required.");
        }

        var executablePath = ActionParameterReader.ReadString(
            action.Parameters,
            ActionParameterNames.ExecutablePath).Trim();
        var lookup = ProcessTargetResolver.FindWithDiagnostics(processName, executablePath);
        if (lookup.ErrorMessage is not null)
        {
            lookup.DisposeAll();
            return ActionExecutionResult.Failure(lookup.ErrorMessage);
        }

        if (lookup.Processes.Count == 0)
        {
            if (lookup.InspectionFailures > 0 && !string.IsNullOrWhiteSpace(executablePath))
            {
                lookup.DisposeAll();
                return ActionExecutionResult.Failure(
                    $"Windows could not inspect any '{processName}' process with the configured path. " +
                    "The operation may require administrator privileges.");
            }

            lookup.DisposeAll();
            return ActionExecutionResult.Skipped("The process is not running.");
        }

        var targets = lookup.Processes
            .Select(process => (Process: process, Identity: RuntimeProcessIdentityService.TryCapture(process)))
            .Where(target => target.Identity is not null)
            .Select(target => new ProcessStopTarget(target.Process, target.Identity!))
            .ToList();
        if (targets.Count == 0)
        {
            lookup.DisposeAll();
            return ActionExecutionResult.Failure(
                $"Windows could not safely identify the running '{processName}' process instance(s).");
        }

        var timeout = action.Timeout is { } configuredTimeout && configuredTimeout > TimeSpan.Zero
            ? configuredTimeout
            : DefaultExitTimeout;
        var logger = context.Logger ?? _logger;
        var failures = new List<string>();

        try
        {
            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (!ProcessTerminationGuard.TryPrepareForKill(target.Process, processName, executablePath,
                            logger, "ProcessSetState", out var safetyError))
                    {
                        failures.Add(safetyError!);
                        continue;
                    }
                    target.Process.Kill(entireProcessTree: false);
                }
                catch (InvalidOperationException)
                {
                    // It exited between discovery and Kill(). This is the requested end state.
                }
                catch (Win32Exception exception)
                {
                    if (RuntimeProcessIdentityService.GetLiveMatch(target.Identity) != RuntimeProcessMatch.NoMatch)
                    {
                        failures.Add(
                            $"Windows could not stop '{target.ProcessName}' (PID {target.ProcessId}). " +
                            $"The operation may require administrator privileges. {exception.Message}");
                        continue;
                    }
                }

                if (!await WaitUntilExitedAsync(target, timeout, cancellationToken))
                {
                    failures.Add(
                        $"Process '{target.ProcessName}' (PID {target.ProcessId}) did not exit within " +
                        $"{timeout.TotalSeconds:0.#} seconds.");
                }
            }
        }
        finally
        {
            lookup.DisposeAll();
        }

        if (failures.Count > 0)
            return ActionExecutionResult.Failure(string.Join(Environment.NewLine, failures));

        // Verify from fresh process snapshots. Windows can expose a just-terminated process briefly,
        // and a target can also respawn, so do not decide from a single read.
        var verificationDeadline = Stopwatch.StartNew();
        ProcessLookupResult? lastVerification = null;
        while (verificationDeadline.Elapsed < TimeSpan.FromSeconds(2))
        {
            cancellationToken.ThrowIfCancellationRequested();
            lastVerification?.DisposeAll();
            lastVerification = ProcessTargetResolver.FindWithDiagnostics(processName, executablePath);
            if (lastVerification.ErrorMessage is not null)
            {
                lastVerification.DisposeAll();
                return ActionExecutionResult.Failure(lastVerification.ErrorMessage);
            }
            if (lastVerification.CanSafelyConcludeNoMatch)
            {
                lastVerification.DisposeAll();
                return ActionExecutionResult.Success($"Verified: no matching '{processName}' process remains.");
            }
            await Task.Delay(100, cancellationToken);
        }
        try
        {
            if (lastVerification!.InspectionFailures > 0 && !string.IsNullOrWhiteSpace(executablePath))
                return ActionExecutionResult.Failure(
                    $"Windows could not verify that every '{processName}' process stopped. Administrator privileges may be required.");
            return ActionExecutionResult.Failure(
                $"Windows did not stop all matching '{processName}' processes. Current matching process count: {lastVerification.Processes.Count}.");
        }
        finally { lastVerification!.DisposeAll(); }
    }

    private static async Task<bool> WaitUntilExitedAsync(
        ProcessStopTarget target,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.GetTimestamp() + (long)(timeout.TotalSeconds * Stopwatch.Frequency);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (target.Process.HasExited)
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
                if (RuntimeProcessIdentityService.GetLiveMatch(target.Identity) == RuntimeProcessMatch.NoMatch)
                {
                    return true;
                }
            }

            if (RuntimeProcessIdentityService.GetLiveMatch(target.Identity) == RuntimeProcessMatch.NoMatch)
            {
                return true;
            }

            await Task.Delay(75, cancellationToken);
        }

        return RuntimeProcessIdentityService.GetLiveMatch(target.Identity) == RuntimeProcessMatch.NoMatch;
    }

    private sealed record ProcessStopTarget(Process Process, RuntimeProcessIdentity Identity)
    {
        public int ProcessId => Identity.ProcessId;
        public string ProcessName => Identity.ProcessName;
    }

    public Task<JsonObject?> CaptureStateAsync(ActionDefinition action, ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var processName = ProcessTargetResolver.NormalizeName(ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.ProcessName));
        var path = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.ExecutablePath).Trim();
        if (string.IsNullOrWhiteSpace(processName) || !Path.IsPathRooted(path) ||
            !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Restart-on-restore requires a process name and a full executable path.");
        var fullPath = Path.GetFullPath(path);
        var lookup = ProcessTargetResolver.FindWithDiagnostics(processName, fullPath);
        if (lookup.ErrorMessage is not null) throw new InvalidOperationException(lookup.ErrorMessage);
        if (lookup.InspectionFailures > 0)
        {
            lookup.DisposeAll();
            throw new InvalidOperationException(
                "Windows could not safely identify every existing process instance. Restore state was not captured.");
        }
        try
        {
            return Task.FromResult<JsonObject?>(new JsonObject
            {
                ["wasRunning"] = lookup.Processes.Count > 0,
                ["instanceCount"] = lookup.Processes.Count,
                ["executablePath"] = fullPath
            });
        }
        finally
        {
            lookup.DisposeAll();
        }
    }

    public async Task<ActionExecutionResult> RestoreAsync(
        ActionDefinition action,
        JsonObject restoreState,
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!(restoreState["wasRunning"]?.GetValue<bool>() ?? false))
            return ActionExecutionResult.Skipped("The process was not running before the action.");
        var path = restoreState["executablePath"]?.GetValue<string>();
        var desiredCount = restoreState["instanceCount"]?.GetValue<int>() ?? 0;
        if (desiredCount <= 0 || string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidOperationException("The executable needed to restart the process is unavailable.");
        var currentCount = CountMatchingExact(path);
        for (var index = currentCount; index < desiredCount; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var process = Process.Start(new ProcessStartInfo
            {
                FileName = path,
                WorkingDirectory = Path.GetDirectoryName(path) ?? string.Empty,
                UseShellExecute = false
            });
            if (process is null) throw new InvalidOperationException($"Windows did not restart '{path}'.");
            process.Dispose();
        }
        var windowBehavior = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.WindowBehavior);
        if (!string.IsNullOrWhiteSpace(windowBehavior) && windowBehavior != WindowBehaviorIds.None)
        {
            var processName = ProcessTargetResolver.NormalizeName(path);
            var waitSeconds = Math.Clamp(ActionParameterReader.ReadInt32(
                action.Parameters, ActionParameterNames.WindowWaitSeconds, 10), 1, 300);
            var found = await WindowBehaviorService.ApplyAsync(processName, path, windowBehavior,
                waitSeconds, cancellationToken);
            if (!found && windowBehavior != WindowBehaviorIds.Hide)
                return ActionExecutionResult.Failure($"The restarted process window did not appear within {waitSeconds} seconds.");
        }
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var verifiedCount = CountMatchingExact(path);
            if (verifiedCount >= desiredCount)
                return ActionExecutionResult.Success($"Verified: restored {verifiedCount} matching process instance(s).");
            await Task.Delay(100, cancellationToken);
        }
        return ActionExecutionResult.Failure(
            $"Windows did not restore the expected process count. Expected at least {desiredCount}, current count: {CountMatchingExact(path)}.");
    }

    private static int CountMatchingExact(string executablePath)
    {
        var name = ProcessTargetResolver.NormalizeName(executablePath);
        var lookup = ProcessTargetResolver.FindWithDiagnostics(name, executablePath);
        try
        {
            if (lookup.ErrorMessage is not null) throw new InvalidOperationException(lookup.ErrorMessage);
            if (lookup.InspectionFailures > 0)
                throw new InvalidOperationException(
                    "Windows could not safely identify existing process instances. No duplicate was started.");
            return lookup.Processes.Count;
        }
        finally { lookup.DisposeAll(); }
    }
}
