using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using SwitchBoard.Models.Actions;

namespace SwitchBoard.Services.Execution.Handlers;

public sealed class ScriptRunActionHandler : IReversibleActionHandler
{
    public string ActionType => ActionTypeIds.ScriptRun;

    public async Task<ActionExecutionResult> ExecuteAsync(
        ActionDefinition action,
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var scriptPath = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.ScriptPath).Trim();
        if (string.IsNullOrWhiteSpace(scriptPath))
        {
            return ActionExecutionResult.Failure("Script path is required.");
        }

        if (!File.Exists(scriptPath))
        {
            return ActionExecutionResult.Failure($"Script was not found: {scriptPath}");
        }

        var scriptType = ResolveScriptType(
            ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.ScriptType),
            scriptPath);
        if (scriptType is null)
        {
            return ActionExecutionResult.Failure("Could not detect the script type. Select PowerShell or Batch/CMD.");
        }

        var waitForExit = ActionParameterReader.ReadBoolean(
            action.Parameters,
            ActionParameterNames.WaitForExit,
            defaultValue: true);
        var runAsAdministrator = ActionParameterReader.ReadBoolean(
            action.Parameters,
            ActionParameterNames.RunAsAdministrator,
            defaultValue: false);
        var trackStartedProcess = action.RestoreBehavior == ActionRestoreBehavior.CloseIfStartedBySwitchBoard;
        var targetProcessName = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.ProcessName).Trim();
        var targetExecutablePath = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.ExecutablePath).Trim();
        if (trackStartedProcess && string.IsNullOrWhiteSpace(targetProcessName))
            return ActionExecutionResult.Failure("A process target is required when close-on-restore is enabled.");
        if (trackStartedProcess)
        {
            try
            {
                targetExecutablePath = ProcessTargetResolver.NormalizeConfiguredPath(targetExecutablePath) ?? string.Empty;
            }
            catch (InvalidOperationException exception)
            {
                return ActionExecutionResult.Failure(exception.Message);
            }
        }

        try
        {
            using var process = Process.Start(CreateStartInfo(action, scriptPath, scriptType, runAsAdministrator));
            if (process is null)
            {
                return ActionExecutionResult.Failure("Windows did not start the script host.");
            }

            var waitForProcess = ActionParameterReader.ReadBoolean(
                action.Parameters, ActionParameterNames.WaitForProcessStart, false);
            var processTrackingWait = TimeSpan.FromSeconds(Math.Clamp(ActionParameterReader.ReadInt32(
                action.Parameters, ActionParameterNames.ProcessStartWaitSeconds, 10), 1, 120));
            var trackingState = trackStartedProcess
                ? ProcessLaunchTracker.CreateTrackingState(context.CapturedState, targetProcessName,
                    targetExecutablePath, processTrackingWait)
                : null;
            Task<JsonArray>? trackingTask = null;
            if (trackStartedProcess)
            {
                if (waitForProcess)
                {
                    trackingTask = ProcessLaunchTracker.TrackAsync(context.CapturedState, targetProcessName,
                        targetExecutablePath, processTrackingWait, cancellationToken);
                }
                else
                {
                    _ = ProcessLaunchTracker.TrackInBackgroundAsync(context.CapturedState, targetProcessName,
                        targetExecutablePath, processTrackingWait,
                        launched => PublishTrackingStateAsync(context, trackingState!, launched),
                        context.ReportBackgroundError);
                }
            }

            if (!waitForExit)
            {
                if (trackingTask is not null)
                {
                    var tracked = await trackingTask;
                    ProcessLaunchTracker.ApplyTrackingResult(trackingState!, tracked);
                }
                return ActionExecutionResult.Success("The script was started.",
                    trackStartedProcess ? trackingState : null);
            }

            try
            {
                if (action.Timeout is { } timeout && timeout > TimeSpan.Zero)
                {
                    await process.WaitForExitAsync(cancellationToken).WaitAsync(timeout, cancellationToken);
                }
                else
                {
                    await process.WaitForExitAsync(cancellationToken);
                }
            }
            catch (TimeoutException)
            {
                return ActionExecutionResult.Failure(
                    $"The script did not finish within {action.Timeout?.TotalSeconds:0.#} seconds.");
            }

            if (trackingTask is not null)
            {
                var launched = await trackingTask;
                ProcessLaunchTracker.ApplyTrackingResult(trackingState!, launched);
            }
            var trackedState = trackStartedProcess ? trackingState : null;
            return process.ExitCode == 0
                ? ActionExecutionResult.Success("The script finished with exit code 0.", trackedState)
                : ActionExecutionResult.Failure($"The script finished with exit code {process.ExitCode}.");
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            return ActionExecutionResult.Failure("Administrator approval was cancelled by the user.");
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or
                                           FileNotFoundException or DirectoryNotFoundException)
        {
            var hint = runAsAdministrator
                ? " The operation may require administrator privileges."
                : string.Empty;
            return ActionExecutionResult.Failure($"Could not run the script.{hint} {exception.Message}".Trim());
        }
    }

    internal static ProcessStartInfo CreateStartInfo(
        ActionDefinition action,
        string scriptPath,
        string scriptType,
        bool runAsAdministrator)
    {
        var arguments = ParseArguments(ActionParameterReader.ReadString(
            action.Parameters,
            ActionParameterNames.Arguments));
        var workingDirectory = ActionParameterReader.ReadString(
            action.Parameters,
            ActionParameterNames.WorkingDirectory).Trim();
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            workingDirectory = Path.GetDirectoryName(Path.GetFullPath(scriptPath)) ?? string.Empty;
        }

        var startInfo = new ProcessStartInfo
        {
            UseShellExecute = runAsAdministrator,
            WorkingDirectory = workingDirectory
        };
        if (runAsAdministrator)
        {
            startInfo.Verb = "runas";
        }

        if (string.Equals(scriptType, ScriptTypeIds.PowerShell, StringComparison.OrdinalIgnoreCase))
        {
            startInfo.FileName = ResolveWindowsPowerShell();
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-NonInteractive");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(Path.GetFullPath(scriptPath));
            foreach (var argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }
        else
        {
            startInfo.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            startInfo.Arguments = $"/d /s /c \"{BuildCmdCommand(scriptPath, arguments)}\"";
        }

        return startInfo;
    }

    private static string? ResolveScriptType(string configuredType, string scriptPath)
    {
        if (string.Equals(configuredType, ScriptTypeIds.PowerShell, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(configuredType, ScriptTypeIds.BatchCmd, StringComparison.OrdinalIgnoreCase))
        {
            return configuredType;
        }

        if (!string.IsNullOrWhiteSpace(configuredType) &&
            !string.Equals(configuredType, ScriptTypeIds.AutoDetect, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Path.GetExtension(scriptPath).ToLowerInvariant() switch
        {
            ".ps1" => ScriptTypeIds.PowerShell,
            ".bat" or ".cmd" => ScriptTypeIds.BatchCmd,
            _ => null
        };
    }

    private static string ResolveWindowsPowerShell()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        return File.Exists(path) ? path : "powershell.exe";
    }

    private static IReadOnlyList<string> ParseArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return [];
        }

        var pointer = CommandLineToArgvW(arguments, out var count);
        if (pointer == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not parse script arguments.");
        }

        try
        {
            var result = new List<string>(count);
            for (var index = 0; index < count; index++)
            {
                var item = Marshal.ReadIntPtr(pointer, index * IntPtr.Size);
                result.Add(Marshal.PtrToStringUni(item) ?? string.Empty);
            }

            return result;
        }
        finally
        {
            LocalFree(pointer);
        }
    }

    private static string BuildCmdCommand(string scriptPath, IReadOnlyList<string> arguments)
    {
        static string Quote(string value) => $"\"{value.Replace("\"", "\"\"")}\"";

        return string.Join(" ", new[] { Quote(Path.GetFullPath(scriptPath)) }.Concat(arguments.Select(Quote)));
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern IntPtr CommandLineToArgvW([MarshalAs(UnmanagedType.LPWStr)] string commandLine, out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    public Task<JsonObject?> CaptureStateAsync(ActionDefinition action, ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (action.RestoreBehavior == ActionRestoreBehavior.CloseIfStartedBySwitchBoard)
        {
            var processName = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.ProcessName).Trim();
            if (string.IsNullOrWhiteSpace(processName))
                throw new InvalidOperationException("A process target is required when close-on-restore is enabled.");
            return Task.FromResult<JsonObject?>(ProcessLaunchTracker.Capture(processName,
                ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.ExecutablePath).Trim()));
        }
        var path = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.RestoreScriptPath).Trim();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new InvalidOperationException("A valid restore script must be selected.");
        return Task.FromResult<JsonObject?>(new JsonObject
        {
            [ActionParameterNames.ScriptPath] = Path.GetFullPath(path),
            [ActionParameterNames.Arguments] = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.RestoreScriptArguments),
            [ActionParameterNames.WorkingDirectory] = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.RestoreScriptWorkingDirectory),
            [ActionParameterNames.ScriptType] = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.RestoreScriptType),
            [ActionParameterNames.WaitForExit] = ActionParameterReader.ReadBoolean(action.Parameters, ActionParameterNames.RestoreScriptWaitForExit, true),
            [ActionParameterNames.RunAsAdministrator] = ActionParameterReader.ReadBoolean(action.Parameters, ActionParameterNames.RestoreScriptRunAsAdministrator, false),
            [ActionParameterNames.RestoreScriptTimeoutSeconds] = ActionParameterReader.ReadInt32(action.Parameters, ActionParameterNames.RestoreScriptTimeoutSeconds, 0)
        });
    }

    public async Task<ActionExecutionResult> RestoreAsync(
        ActionDefinition action,
        JsonObject restoreState,
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (restoreState["targetProcessName"] is not null || restoreState["launchedProcesses"] is not null)
            return await ProcessLaunchTracker.CloseAsync(restoreState, cancellationToken, context.Logger);
        var timeoutSeconds = ActionParameterReader.ReadInt32(restoreState, ActionParameterNames.RestoreScriptTimeoutSeconds, 0);
        var restoreAction = new ActionDefinition
        {
            Id = action.Id,
            Type = ActionTypeIds.ScriptRun,
            Name = action.Name,
            Parameters = restoreState.DeepClone().AsObject(),
            Timeout = timeoutSeconds > 0 ? TimeSpan.FromSeconds(timeoutSeconds) : null
        };
        var result = await ExecuteAsync(restoreAction, context, cancellationToken);
        return result;
    }

    private static Task PublishTrackingStateAsync(ActionExecutionContext context, JsonObject state,
        JsonArray launchedProcesses)
    {
        ProcessLaunchTracker.ApplyTrackingResult(state, launchedProcesses);
        return context.UpdateRestoreStateAsync?.Invoke(state) ?? Task.CompletedTask;
    }
}
