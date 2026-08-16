using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using SwitchBoard.Models.Actions;

namespace SwitchBoard.Services.Execution.Handlers;

public sealed class ProcessSetStateActionHandler : IActionHandler
{
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(5);

    public string ActionType => ActionTypeIds.ProcessSetState;

    public async Task<ActionExecutionResult> ExecuteAsync(
        ActionDefinition action,
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var desiredState = ActionParameterReader.ReadString(
            action.Parameters,
            ActionParameterNames.DesiredState);
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
        var lookup = FindMatchingProcesses(processName, executablePath);
        if (lookup.ErrorMessage is not null)
        {
            DisposeAll(lookup.Processes);
            return ActionExecutionResult.Failure(lookup.ErrorMessage);
        }

        if (lookup.Processes.Count == 0)
        {
            return ActionExecutionResult.Skipped("The process is not running.");
        }

        try
        {
            foreach (var process in lookup.Processes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    process.Kill(entireProcessTree: false);
                }
                catch (InvalidOperationException)
                {
                    // The process exited between discovery and termination.
                }
                catch (Win32Exception exception)
                {
                    return ActionExecutionResult.Failure(
                        $"Windows could not stop process '{processName}'. Check permissions. {exception.Message}");
                }
            }

            try
            {
                await Task.WhenAll(lookup.Processes.Select(process => process.WaitForExitAsync(cancellationToken)))
                    .WaitAsync(ExitTimeout, cancellationToken);
            }
            catch (TimeoutException)
            {
                return ActionExecutionResult.Failure(
                    $"Process '{processName}' did not exit within {ExitTimeout.TotalSeconds:0} seconds.");
            }
        }
        finally
        {
            DisposeAll(lookup.Processes);
        }

        var verification = FindMatchingProcesses(processName, executablePath);
        try
        {
            if (verification.ErrorMessage is not null)
            {
                return ActionExecutionResult.Failure(verification.ErrorMessage);
            }

            return verification.Processes.Count == 0
                ? ActionExecutionResult.Success()
                : ActionExecutionResult.Failure($"Process '{processName}' is still running after termination.");
        }
        finally
        {
            DisposeAll(verification.Processes);
        }
    }

    private static ProcessLookupResult FindMatchingProcesses(string processName, string executablePath)
    {
        var processes = Process.GetProcessesByName(processName).ToList();
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return new ProcessLookupResult(processes, null);
        }

        string expectedPath;
        try
        {
            expectedPath = Path.GetFullPath(executablePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new ProcessLookupResult(processes, $"The executable path is invalid: {exception.Message}");
        }

        var matching = new List<Process>();
        foreach (var process in processes)
        {
            try
            {
                var actualPath = process.MainModule?.FileName;
                if (actualPath is not null && string.Equals(
                        Path.GetFullPath(actualPath),
                        expectedPath,
                        StringComparison.OrdinalIgnoreCase))
                {
                    matching.Add(process);
                }
                else
                {
                    process.Dispose();
                }
            }
            catch (InvalidOperationException)
            {
                process.Dispose();
            }
            catch (Exception exception) when (exception is Win32Exception or NotSupportedException)
            {
                foreach (var retained in matching)
                {
                    retained.Dispose();
                }
                foreach (var remaining in processes.Where(candidate => !ReferenceEquals(candidate, process)))
                {
                    if (!matching.Contains(remaining))
                    {
                        remaining.Dispose();
                    }
                }
                process.Dispose();
                return new ProcessLookupResult([], $"Windows could not inspect process '{processName}'. Check permissions. {exception.Message}");
            }
        }

        return new ProcessLookupResult(matching, null);
    }

    private static string NormalizeProcessName(string processName) =>
        Path.GetFileNameWithoutExtension(processName.Trim());

    private static void DisposeAll(IEnumerable<Process> processes)
    {
        foreach (var process in processes)
        {
            process.Dispose();
        }
    }

    private sealed record ProcessLookupResult(List<Process> Processes, string? ErrorMessage);

    public Task RestoreAsync(
        ActionDefinition action,
        System.Text.Json.Nodes.JsonObject restoreState,
        ActionExecutionContext context,
        CancellationToken cancellationToken) => Task.CompletedTask;
}