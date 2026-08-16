using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using SwitchBoard.Models.Actions;

namespace SwitchBoard.Services.Execution.Handlers;

public sealed class ProgramRunActionHandler : IActionHandler
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

            process.Dispose();
            return Task.FromResult(ActionExecutionResult.Success());
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

    private static bool IsAlreadyRunning(string target)
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

    public Task RestoreAsync(
        ActionDefinition action,
        System.Text.Json.Nodes.JsonObject restoreState,
        ActionExecutionContext context,
        CancellationToken cancellationToken) => Task.CompletedTask;
}