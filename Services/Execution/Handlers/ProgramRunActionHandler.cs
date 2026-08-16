using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using SwitchBoard.Models.Actions;

namespace SwitchBoard.Services.Execution.Handlers;

public sealed class ProgramRunActionHandler : IReversibleActionHandler
{
    public string ActionType => ActionTypeIds.ProgramRun;

    public Task<ActionExecutionResult> ExecuteAsync(
        ActionDefinition action,
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var target = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.Target).Trim();
        if (string.IsNullOrWhiteSpace(target))
        {
            return Task.FromResult(ActionExecutionResult.Failure("Program target is required."));
        }

        var startOnlyIfNotAlreadyRunning = ActionParameterReader.ReadBoolean(
            action.Parameters,
            ActionParameterNames.StartOnlyIfNotAlreadyRunning,
            defaultValue: true);
        if (startOnlyIfNotAlreadyRunning && !IsProtocolTarget(target) && IsAlreadyRunning(target))
        {
            return Task.FromResult(ActionExecutionResult.Skipped("The program is already running."));
        }

        try
        {
            var process = Process.Start(CreateStartInfo(action, target));
            if (process is null)
            {
                return Task.FromResult(ActionExecutionResult.Failure("Windows did not start the requested target."));
            }

            if (action.RestoreBehavior != ActionRestoreBehavior.CloseIfStartedBySwitchBoard)
            {
                process.Dispose();
                return Task.FromResult(ActionExecutionResult.Success());
            }

            int processId;
            long? startedAtTicks;
            try
            {
                processId = process.Id;
                startedAtTicks = process.StartTime.ToUniversalTime().Ticks;
            }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
            {
                process.Dispose();
                return Task.FromResult(ActionExecutionResult.Failure(
                    $"The program started, but SwitchBoard could not capture its identity for safe restore: {exception.Message}"));
            }
            process.Dispose();
            return Task.FromResult(ActionExecutionResult.Success(restoreState: new JsonObject
            {
                ["startedBySwitchBoard"] = true,
                ["processId"] = processId,
                ["startedAtUtcTicks"] = startedAtTicks,
                ["executablePath"] = Path.GetFullPath(target)
            }));
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or FileNotFoundException or DirectoryNotFoundException)
        {
            return Task.FromResult(ActionExecutionResult.Failure($"Could not start '{target}': {exception.Message}"));
        }
    }

    internal static ProcessStartInfo CreateStartInfo(ActionDefinition action, string target)
    {
        var arguments = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.Arguments);
        var workingDirectory = ActionParameterReader.ReadString(
            action.Parameters,
            ActionParameterNames.WorkingDirectory).Trim();
        var useShellExecute = IsProtocolTarget(target) ||
                              string.Equals(Path.GetExtension(target), ".lnk", StringComparison.OrdinalIgnoreCase);

        var startInfo = new ProcessStartInfo
        {
            FileName = target,
            Arguments = arguments,
            UseShellExecute = useShellExecute
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory))
        {
            startInfo.WorkingDirectory = workingDirectory;
        }
        else if (!useShellExecute && Path.IsPathRooted(target))
        {
            startInfo.WorkingDirectory = Path.GetDirectoryName(target) ?? string.Empty;
        }

        return startInfo;
    }

    internal static bool IsProtocolTarget(string target) =>
        Uri.TryCreate(target, UriKind.Absolute, out var uri) &&
        !uri.IsFile &&
        !string.IsNullOrWhiteSpace(uri.Scheme);

    internal static bool IsAlreadyRunning(string target)
    {
        var processName = Path.GetFileNameWithoutExtension(target);
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        var expectedPath = Path.IsPathRooted(target) &&
                           string.Equals(Path.GetExtension(target), ".exe", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(target)
            : null;
        var processes = Process.GetProcessesByName(processName);
        try
        {
            if (expectedPath is null)
            {
                return processes.Length > 0;
            }

            foreach (var process in processes)
            {
                try
                {
                    if (string.Equals(
                            Path.GetFullPath(process.MainModule?.FileName ?? string.Empty),
                            expectedPath,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    // Conservatively avoid a duplicate if Windows denies path inspection.
                    return true;
                }
            }

            return false;
        }
        finally
        {
            foreach (var process in processes)
            {
                process.Dispose();
            }
        }
    }

    public Task<JsonObject?> CaptureStateAsync(ActionDefinition action, ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.Target).Trim();
        if (!Path.IsPathRooted(target) || !string.Equals(Path.GetExtension(target), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Close-on-restore requires a full path to an executable file.");
        var fullPath = Path.GetFullPath(target);
        return Task.FromResult<JsonObject?>(new JsonObject
        {
            ["wasRunningBefore"] = IsAlreadyRunning(fullPath),
            ["executablePath"] = fullPath,
            ["startedBySwitchBoard"] = false
        });
    }

    public async Task RestoreAsync(
        ActionDefinition action,
        JsonObject restoreState,
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (!(restoreState["startedBySwitchBoard"]?.GetValue<bool>() ?? false)) return;
        var path = restoreState["executablePath"]?.GetValue<string>();
        var pid = restoreState["processId"]?.GetValue<int>() ?? 0;
        var ticks = restoreState["startedAtUtcTicks"]?.GetValue<long>() ?? 0;
        if (pid <= 0 || ticks <= 0 || string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException("The saved program identity is incomplete.");
        Process? process = null;
        try
        {
            process = Process.GetProcessById(pid);
            if (process.HasExited) return;
            var actualStart = process.StartTime.ToUniversalTime().Ticks;
            if (Math.Abs(actualStart - ticks) > TimeSpan.FromSeconds(1).Ticks)
                throw new InvalidOperationException("The saved PID now belongs to a different process; it was not closed.");
            var actualPath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(actualPath) || !string.Equals(Path.GetFullPath(actualPath), Path.GetFullPath(path),
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The process executable no longer matches the saved identity; it was not closed.");
            process.Kill(entireProcessTree: false);
            await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        }
        catch (ArgumentException)
        {
            // The exact process already exited, so the restore objective is satisfied.
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException(
                $"Windows could not close the program started by SwitchBoard. The operation may require administrator privileges. {exception.Message}", exception);
        }
        finally { process?.Dispose(); }
    }
}
