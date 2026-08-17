using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using SwitchBoard.Models.Actions;

namespace SwitchBoard.Services.Execution.Handlers;

public sealed class ProcessSetStateActionHandler : IReversibleActionHandler
{
    private static readonly TimeSpan DefaultExitTimeout = TimeSpan.FromSeconds(5);

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

        var processName = NormalizeProcessName(ActionParameterReader.ReadString(
            action.Parameters,
            ActionParameterNames.ProcessName));
        if (string.IsNullOrWhiteSpace(processName))
        {
            return ActionExecutionResult.Failure("Process name is required.");
        }

        var executablePath = ActionParameterReader.ReadString(
            action.Parameters,
            ActionParameterNames.ExecutablePath).Trim();
        var lookup = FindMatchingProcesses(processName, executablePath, action.RuntimeProcessIdHint);
        if (lookup.ErrorMessage is not null)
        {
            return ActionExecutionResult.Failure(lookup.ErrorMessage);
        }

        if (lookup.Processes.Count == 0)
        {
            if (lookup.InspectionFailures > 0 && !string.IsNullOrWhiteSpace(executablePath))
            {
                return ActionExecutionResult.Failure(
                    $"Windows could not inspect any '{processName}' process with the configured path. " +
                    "The operation may require administrator privileges.");
            }

            return ActionExecutionResult.Skipped("The process is not running.");
        }

        var timeout = action.Timeout is { } configuredTimeout && configuredTimeout > TimeSpan.Zero
            ? configuredTimeout
            : DefaultExitTimeout;
        var failures = new List<string>();

        try
        {
            foreach (var target in lookup.Processes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    target.Process.Kill(entireProcessTree: false);
                }
                catch (InvalidOperationException)
                {
                    // It exited between discovery and Kill(). This is the requested end state.
                }
                catch (Win32Exception exception)
                {
                    if (IsProcessIdAlive(target.ProcessId))
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
            foreach (var target in lookup.Processes)
            {
                target.Process.Dispose();
            }
        }

        if (failures.Count > 0)
            return ActionExecutionResult.Failure(string.Join(Environment.NewLine, failures));

        // A runtime PID hint means the user selected one concrete process instance. Do not turn
        // verification into a broader name-based operation that can include unrelated instances.
        if (action.RuntimeProcessIdHint is int selectedPid && selectedPid > 0)
            return IsProcessIdAlive(selectedPid)
                ? ActionExecutionResult.Failure(
                    $"Windows did not stop the selected '{processName}' process (PID {selectedPid}).")
                : ActionExecutionResult.Success(
                    $"Verified: the selected '{processName}' process (PID {selectedPid}) no longer exists.");

        // Verify from fresh process snapshots. Windows can expose a just-terminated process briefly,
        // and a target can also respawn, so do not decide from a single read.
        var verificationDeadline = Stopwatch.StartNew();
        ProcessLookupResult? lastVerification = null;
        while (verificationDeadline.Elapsed < TimeSpan.FromSeconds(2))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (lastVerification is not null)
                foreach (var target in lastVerification.Processes) target.Process.Dispose();
            lastVerification = FindMatchingProcesses(processName, executablePath, null);
            if (lastVerification.ErrorMessage is not null)
                return ActionExecutionResult.Failure(lastVerification.ErrorMessage);
            if (lastVerification.Processes.Count == 0 &&
                (lastVerification.InspectionFailures == 0 || string.IsNullOrWhiteSpace(executablePath)))
                return ActionExecutionResult.Success($"Verified: no matching '{processName}' process remains.");
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
        finally { foreach (var target in lastVerification!.Processes) target.Process.Dispose(); }
    }

    private static ProcessLookupResult FindMatchingProcesses(
        string processName,
        string executablePath,
        int? runtimeProcessIdHint)
    {
        string? expectedPath = null;
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            try
            {
                expectedPath = Path.GetFullPath(executablePath);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return new ProcessLookupResult([], 0, $"The executable path is invalid: {exception.Message}");
            }
        }

        if (runtimeProcessIdHint is > 0)
        {
            var hinted = TryGetHintedProcess(runtimeProcessIdHint.Value, processName, expectedPath);
            if (hinted.Target is not null)
            {
                return new ProcessLookupResult([hinted.Target], hinted.InspectionFailed ? 1 : 0, null);
            }
        }

        var matching = new List<ProcessTarget>();
        var inspectionFailures = 0;
        foreach (var process in Process.GetProcessesByName(processName))
        {
            try
            {
                var processId = process.Id;
                var actualName = process.ProcessName;
                string? actualPath = null;
                if (expectedPath is not null)
                {
                    try
                    {
                        actualPath = process.MainModule?.FileName;
                    }
                    catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
                    {
                        inspectionFailures++;
                        process.Dispose();
                        continue;
                    }

                    if (actualPath is null || !string.Equals(
                            Path.GetFullPath(actualPath),
                            expectedPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        process.Dispose();
                        continue;
                    }
                }

                // Everything used for matching and reporting is captured before Kill().
                matching.Add(new ProcessTarget(process, processId, actualName, actualPath));
            }
            catch (InvalidOperationException)
            {
                process.Dispose();
            }
        }

        return new ProcessLookupResult(matching, inspectionFailures, null);
    }

    private static (ProcessTarget? Target, bool InspectionFailed) TryGetHintedProcess(
        int processId,
        string processName,
        string? expectedPath)
    {
        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId);
            if (!string.Equals(process.ProcessName, processName, StringComparison.OrdinalIgnoreCase))
            {
                process.Dispose();
                return (null, false);
            }

            string? actualPath = null;
            if (expectedPath is not null)
            {
                try
                {
                    actualPath = process.MainModule?.FileName;
                }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    process.Dispose();
                    return (null, true);
                }

                if (actualPath is null || !string.Equals(Path.GetFullPath(actualPath), expectedPath, StringComparison.OrdinalIgnoreCase))
                {
                    process.Dispose();
                    return (null, false);
                }
            }

            return (new ProcessTarget(process, processId, process.ProcessName, actualPath), false);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            process?.Dispose();
            return (null, false);
        }
    }

    private static async Task<bool> WaitUntilExitedAsync(
        ProcessTarget target,
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
                if (!IsProcessIdAlive(target.ProcessId))
                {
                    return true;
                }
            }

            if (!IsProcessIdAlive(target.ProcessId))
            {
                return true;
            }

            await Task.Delay(75, cancellationToken);
        }

        return !IsProcessIdAlive(target.ProcessId);
    }

    private static bool IsProcessIdAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return true;
        }
    }

    private static string NormalizeProcessName(string processName) =>
        Path.GetFileNameWithoutExtension(processName.Trim());

    private sealed record ProcessTarget(Process Process, int ProcessId, string ProcessName, string? ExecutablePath);

    private sealed record ProcessLookupResult(
        List<ProcessTarget> Processes,
        int InspectionFailures,
        string? ErrorMessage);

    public Task<JsonObject?> CaptureStateAsync(ActionDefinition action, ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var processName = NormalizeProcessName(ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.ProcessName));
        var path = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.ExecutablePath).Trim();
        if (string.IsNullOrWhiteSpace(processName) || !Path.IsPathRooted(path) ||
            !string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Restart-on-restore requires a process name and a full executable path.");
        var fullPath = Path.GetFullPath(path);
        var lookup = FindMatchingProcesses(processName, fullPath, action.RuntimeProcessIdHint);
        if (lookup.ErrorMessage is not null) throw new InvalidOperationException(lookup.ErrorMessage);
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
            foreach (var target in lookup.Processes) target.Process.Dispose();
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
        var name = Path.GetFileNameWithoutExtension(executablePath);
        var count = 0;
        foreach (var process in Process.GetProcessesByName(name))
        {
            try
            {
                var actual = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(actual) && string.Equals(Path.GetFullPath(actual),
                        Path.GetFullPath(executablePath), StringComparison.OrdinalIgnoreCase)) count++;
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                throw new InvalidOperationException(
                    "Windows could not safely identify existing process instances. No duplicate was started.", exception);
            }
            finally { process.Dispose(); }
        }
        return count;
    }
}
