using System.ComponentModel;
using System.Text.Json.Nodes;
using SwitchBoard.Models.Actions;
using SwitchBoard.Services.Windows;

namespace SwitchBoard.Services.Execution.Handlers;

public sealed class PowerSetPlanActionHandler(IPowerPlanManager powerPlanManager) : IReversibleActionHandler
{
    public string ActionType => ActionTypeIds.PowerSetPlan;

    public async Task<ActionExecutionResult> ExecuteAsync(
        ActionDefinition action,
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var guidText = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.PowerPlanGuid).Trim();
        if (!Guid.TryParse(guidText, out var targetPlan))
        {
            return ActionExecutionResult.Failure("A valid power plan must be selected.");
        }

        try
        {
            var activePlan = await powerPlanManager.GetActivePlanAsync(cancellationToken);
            if (activePlan == targetPlan)
            {
                return ActionExecutionResult.Skipped("The selected power plan is already active.");
            }

            await powerPlanManager.SetActivePlanAsync(targetPlan, cancellationToken);
            activePlan = await powerPlanManager.GetActivePlanAsync(cancellationToken);
            if (activePlan != targetPlan)
            {
                return ActionExecutionResult.Failure("Windows did not activate the selected power plan.");
            }

            return ActionExecutionResult.Success($"Verified: power plan {targetPlan:D} is active.");
        }
        catch (Win32Exception exception)
        {
            var hint = exception.NativeErrorCode == 5
                ? " The operation may require administrator privileges or may be restricted by system policy."
                : string.Empty;
            return ActionExecutionResult.Failure($"{exception.Message}{hint}");
        }
    }

    public async Task<JsonObject?> CaptureStateAsync(
        ActionDefinition action,
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var active = await powerPlanManager.GetActivePlanAsync(cancellationToken);
        return new JsonObject { ["previousPowerPlanGuid"] = active.ToString("D") };
    }

    public async Task<ActionExecutionResult> RestoreAsync(
        ActionDefinition action,
        JsonObject restoreState,
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var value = restoreState["previousPowerPlanGuid"]?.GetValue<string>();
        if (!Guid.TryParse(value, out var previous)) return ActionExecutionResult.Failure("The saved power plan is invalid.", false);
        if (await powerPlanManager.GetActivePlanAsync(cancellationToken) == previous)
            return ActionExecutionResult.Success("The previous power plan was already active.");
        await powerPlanManager.SetActivePlanAsync(previous, cancellationToken);
        if (await powerPlanManager.GetActivePlanAsync(cancellationToken) != previous)
            return ActionExecutionResult.Failure("Windows did not restore the previous power plan.");
        return ActionExecutionResult.Success("Verified: the previous power plan is active.");
    }
}
