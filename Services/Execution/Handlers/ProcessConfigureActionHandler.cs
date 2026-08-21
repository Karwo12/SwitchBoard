using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using SwitchBoard.Models.Actions;

namespace SwitchBoard.Services.Execution.Handlers;

public sealed class ProcessConfigureActionHandler : IReversibleActionHandler
{
    private readonly ProcessSetStateActionHandler _stopHandler;
    private readonly ProcessSettingsService _settingsService;

    public ProcessConfigureActionHandler(ProcessSetStateActionHandler? stopHandler = null,
        ProcessSettingsService? settingsService = null)
    {
        _stopHandler = stopHandler ?? new ProcessSetStateActionHandler();
        _settingsService = settingsService ?? new ProcessSettingsService();
    }

    public string ActionType => ActionTypeIds.ProcessConfigure;

    public Task<JsonObject?> CaptureStateAsync(ActionDefinition action, ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (IsStopOperation(action))
            return _stopHandler.CaptureStateAsync(action, context, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        using var process = FindSingle(action);
        return Task.FromResult<JsonObject?>(_settingsService.Capture(process, action.Parameters));
    }

    public Task<ActionExecutionResult> ExecuteAsync(ActionDefinition action, ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (IsStopOperation(action))
            return _stopHandler.ExecuteAsync(action, context, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var process = FindSingle(action);
            _settingsService.Apply(process, action.Parameters);

            return Task.FromResult(ActionExecutionResult.Success("Verified: the requested process settings are active."));
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or
                                           Win32Exception or NotSupportedException)
        {
            return Task.FromResult(ActionExecutionResult.Failure($"Could not change process settings: {exception.Message}"));
        }
    }

    public Task<ActionExecutionResult> RestoreAsync(ActionDefinition action, JsonObject restoreState, ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (IsStopOperation(action))
            return _stopHandler.RestoreAsync(action, restoreState, context, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        var processId = restoreState["processId"]?.GetValue<int>() ?? 0;
        var startedAt = restoreState["startedAtUtcTicks"]?.GetValue<long>() ?? 0;
        Process? process = null;
        try
        {
            process = Process.GetProcessById(processId);
            if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != startedAt)
                throw new InvalidOperationException("The original process no longer exists; its settings cannot be restored.");
            _settingsService.Restore(process, restoreState);
            return Task.FromResult(ActionExecutionResult.Success("Verified: the previous process settings are active."));
        }
        catch (ArgumentException)
        {
            throw new InvalidOperationException("The original process no longer exists; its settings cannot be restored.");
        }
        finally { process?.Dispose(); }
    }

    private static Process FindSingle(ActionDefinition action)
    {
        var name = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.ProcessName);
        var path = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.ExecutablePath);
        var matches = ProcessTargetResolver.Find(name, path, action.RuntimeProcessIdHint);
        if (matches.Count == 0) throw new InvalidOperationException("The selected process is not running.");
        var selected = matches[0];
        foreach (var extra in matches.Skip(1)) extra.Dispose();
        return selected;
    }

    // Kept as a compatibility facade for integrations and existing runtime tests.
    public static ulong ReadAffinityMask(JsonArray? values) => ProcessSettingsService.ReadAffinityMask(values);

    private static bool IsStopOperation(ActionDefinition action) =>
        string.Equals(ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.ProcessOperation),
            ProcessOperationIds.Stop, StringComparison.OrdinalIgnoreCase);
}
