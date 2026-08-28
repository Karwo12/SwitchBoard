using System.Text.Json.Nodes;
using SwitchBoard.Models.Actions;
using SwitchBoard.Services.Windows;

namespace SwitchBoard.Services.Execution.Handlers;

public sealed class DeviceSetStateActionHandler(IDeviceManager deviceManager) : IReversibleActionHandler
{
    public string ActionType => ActionTypeIds.DeviceSetState;

    public async Task<JsonObject?> CaptureStateAsync(ActionDefinition action, ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var id = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.DeviceInstanceId);
        var device = await deviceManager.GetDeviceAsync(id, cancellationToken) ??
                     throw new InvalidOperationException("The selected Windows device is not present.");
        return new JsonObject { ["enabled"] = device.IsEnabled, ["instanceId"] = device.InstanceId };
    }

    public async Task<ActionExecutionResult> ExecuteAsync(ActionDefinition action, ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var id = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.DeviceInstanceId);
        var desired = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.DesiredState);
        if (string.IsNullOrWhiteSpace(id)) return ActionExecutionResult.Failure("Device Instance ID is required.", false);
        if (desired == DeviceStateIds.Unchanged) return ActionExecutionResult.Skipped("The requested device state is Unchanged.");
        var enabled = desired == DeviceStateIds.Enabled;
        try
        {
            var device = await deviceManager.GetDeviceAsync(id, cancellationToken);
            if (device is null) return ActionExecutionResult.Failure("The selected Windows device is not present.");
            if (!enabled && device.IsCritical)
                return ActionExecutionResult.Failure("SwitchBoard blocked disabling this critical Windows device.", false);
            if (device.IsEnabled == enabled) return ActionExecutionResult.Skipped("The device already has the requested state.");
            await deviceManager.SetEnabledAsync(id, enabled, cancellationToken);
            var verified = await deviceManager.GetDeviceAsync(id, cancellationToken);
            return verified is not null && verified.IsEnabled == enabled
                ? ActionExecutionResult.Success($"Verified: the device is now {(enabled ? "Enabled" : "Disabled")}.")
                : ActionExecutionResult.Failure($"Windows did not set the device to {(enabled ? "Enabled" : "Disabled")}. " +
                    $"Current state: {(verified is null ? "not present" : verified.IsEnabled ? "Enabled" : "Disabled")}.");
        }
        catch (InvalidOperationException exception)
        {
            return ActionExecutionResult.Failure(exception.Message,
                !exception.Message.Contains("blocked", StringComparison.OrdinalIgnoreCase));
        }
    }

    public async Task<ActionExecutionResult> RestoreAsync(ActionDefinition action, JsonObject restoreState, ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var id = restoreState["instanceId"]?.GetValue<string>() ??
                 ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.DeviceInstanceId);
        var enabled = restoreState["enabled"]?.GetValue<bool>() ??
                      throw new InvalidOperationException("The previous device state is missing.");
        await deviceManager.SetEnabledAsync(id, enabled, cancellationToken);
        var verified = await deviceManager.GetDeviceAsync(id, cancellationToken);
        return verified is not null && verified.IsEnabled == enabled
            ? ActionExecutionResult.Success($"Verified: the device was restored to {(enabled ? "Enabled" : "Disabled")}.")
            : ActionExecutionResult.Failure($"Windows did not restore the device to {(enabled ? "Enabled" : "Disabled")}.");
    }
}
