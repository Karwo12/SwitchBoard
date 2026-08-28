using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using SwitchBoard.Models.Actions;
using SwitchBoard.Services.Logging;

namespace SwitchBoard.Services.Execution.Handlers;

public sealed class ProcessConfigureActionHandler : IReversibleActionHandler
{
    private readonly ProcessSetStateActionHandler _stopHandler;
    private readonly ProcessSettingsService _settingsService;

    public ProcessConfigureActionHandler(ProcessSetStateActionHandler? stopHandler = null,
        ProcessSettingsService? settingsService = null, IAppLogger? logger = null)
    {
        _stopHandler = stopHandler ?? new ProcessSetStateActionHandler(logger);
        _settingsService = settingsService ?? ProcessSettingsService.Shared;
    }

    public string ActionType => ActionTypeIds.ProcessConfigure;

    public Task<JsonObject?> CaptureStateAsync(ActionDefinition action, ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (IsStopOperation(action))
            return _stopHandler.CaptureStateAsync(action, context, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        var processes = FindMatchingProcesses(action);
        try
        {
            var states = new JsonArray();
            foreach (var process in processes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                states.Add(_settingsService.Capture(process, action.Parameters));
            }

            // Preserve the original single-instance shape for existing restore records.
            if (states.Count == 1)
                return Task.FromResult<JsonObject?>(states[0]!.DeepClone().AsObject());
            return Task.FromResult<JsonObject?>(new JsonObject { ["processStates"] = states });
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    public Task<ActionExecutionResult> ExecuteAsync(ActionDefinition action, ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (IsStopOperation(action))
            return _stopHandler.ExecuteAsync(action, context, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        var processes = FindMatchingProcesses(action);
        var failures = new List<string>();
        try
        {
            foreach (var process in processes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    _settingsService.Apply(process, action.Parameters);
                }
                catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or
                                                   Win32Exception or NotSupportedException)
                {
                    failures.Add($"PID {SafeProcessId(process)}: {exception.Message}");
                }
            }

            return Task.FromResult(failures.Count == 0
                ? ActionExecutionResult.Success(
                    $"Verified: the requested process settings are active for {processes.Count} matching process instance(s).")
                : ActionExecutionResult.Failure($"Could not change process settings. {string.Join(" ", failures)}"));
        }
        finally
        {
            foreach (var process in processes) process.Dispose();
        }
    }

    public Task<ActionExecutionResult> RestoreAsync(ActionDefinition action, JsonObject restoreState, ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (IsStopOperation(action))
            return _stopHandler.RestoreAsync(action, restoreState, context, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        if (restoreState["processStates"] is JsonArray states)
        {
            var failures = new List<string>();
            foreach (var state in states.OfType<JsonObject>())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    RestoreSingle(state);
                }
                catch (InvalidOperationException exception)
                {
                    failures.Add(exception.Message);
                }
            }

            return Task.FromResult(failures.Count == 0
                ? ActionExecutionResult.Success("Verified: the previous process settings are active.")
                : ActionExecutionResult.Failure($"Could not restore process settings. {string.Join(" ", failures)}"));
        }

        RestoreSingle(restoreState);
        return Task.FromResult(ActionExecutionResult.Success("Verified: the previous process settings are active."));
    }

    private void RestoreSingle(JsonObject restoreState)
    {
        var identity = RuntimeProcessIdentityService.ReadIdentity(restoreState) ??
                       throw new InvalidOperationException(
                           "The saved process identity is incomplete; its settings cannot be restored.");
        Process? process = null;
        try
        {
            process = Process.GetProcessById(identity.ProcessId);
            var match = RuntimeProcessIdentityService.Match(process, identity);
            if (match == RuntimeProcessMatch.NoMatch)
                throw new InvalidOperationException("The original process no longer exists; its settings cannot be restored.");
            if (match == RuntimeProcessMatch.Unknown)
                throw new InvalidOperationException(
                    "Windows could not safely verify the original process identity; its settings were not changed.");
            _settingsService.Restore(process, restoreState);
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException("The original process no longer exists; its settings cannot be restored.");
        }
        finally { process?.Dispose(); }
    }

    private static List<Process> FindMatchingProcesses(ActionDefinition action)
    {
        var name = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.ProcessName);
        var path = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.ExecutablePath);
        var lookup = ProcessTargetResolver.FindWithDiagnostics(name, path);
        if (lookup.ErrorMessage is not null)
        {
            lookup.DisposeAll();
            throw new InvalidOperationException(lookup.ErrorMessage);
        }
        if (lookup.InspectionFailures > 0)
        {
            lookup.DisposeAll();
            throw new InvalidOperationException(
                "Windows could not safely verify every same-name process against the configured executable path.");
        }
        if (lookup.Processes.Count == 0)
        {
            lookup.DisposeAll();
            throw new InvalidOperationException("The configured process is not running.");
        }
        return lookup.Processes;
    }

    private static int SafeProcessId(Process process)
    {
        try { return process.Id; }
        catch (InvalidOperationException) { return 0; }
    }

    // Kept as a compatibility facade for integrations and existing runtime tests.
    public static ulong ReadAffinityMask(JsonArray? values) => ProcessSettingsService.ReadAffinityMask(values);

    private static bool IsStopOperation(ActionDefinition action) =>
        string.Equals(ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.ProcessOperation),
            ProcessOperationIds.Stop, StringComparison.OrdinalIgnoreCase);
}
