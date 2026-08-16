using SwitchBoard.Models.Actions;
using SwitchBoard.Services.Windows;

namespace SwitchBoard.Services.Execution.Handlers;

public sealed class ServiceSetStateActionHandler(IWindowsServiceManager serviceManager) : IReversibleActionHandler
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public string ActionType => ActionTypeIds.ServiceSetState;

    public async Task<ActionExecutionResult> ExecuteAsync(
        ActionDefinition action,
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var serviceName = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.ServiceName).Trim();
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            return ActionExecutionResult.Failure("A Windows service must be selected.");
        }

        var desiredState = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.DesiredState);
        if (string.IsNullOrWhiteSpace(desiredState))
        {
            desiredState = ServiceDesiredStateIds.Unchanged;
        }

        var timeout = action.Timeout is { } configured && configured > TimeSpan.Zero
            ? configured
            : DefaultTimeout;
        var result = await serviceManager.SetStateAsync(serviceName, desiredState, timeout, cancellationToken);
        return result.IsSkipped
            ? ActionExecutionResult.Skipped(result.Message)
            : result.IsSuccessful
                ? ActionExecutionResult.Success(result.Message)
                : ActionExecutionResult.Failure(result.Message ?? $"Could not change service '{serviceName}'.");
    }

    public async Task<System.Text.Json.Nodes.JsonObject?> CaptureStateAsync(
        ActionDefinition action,
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var serviceName = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.ServiceName).Trim();
        if (string.IsNullOrWhiteSpace(serviceName)) throw new InvalidOperationException("A Windows service must be selected.");
        var state = await serviceManager.GetStateAsync(serviceName, cancellationToken);
        return new System.Text.Json.Nodes.JsonObject
        {
            ["serviceName"] = serviceName,
            ["previousState"] = state
        };
    }

    public async Task RestoreAsync(
        ActionDefinition action,
        System.Text.Json.Nodes.JsonObject restoreState,
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var serviceName = restoreState["serviceName"]?.GetValue<string>();
        var previousState = restoreState["previousState"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(serviceName) || string.IsNullOrWhiteSpace(previousState))
            throw new InvalidOperationException("The saved service state is incomplete.");
        var timeout = action.Timeout is { } configured && configured > TimeSpan.Zero ? configured : DefaultTimeout;
        var result = await serviceManager.SetStateAsync(serviceName, previousState, timeout, cancellationToken);
        if (!result.IsSuccessful) throw new InvalidOperationException(result.Message ?? "The service could not be restored.");
    }
}
