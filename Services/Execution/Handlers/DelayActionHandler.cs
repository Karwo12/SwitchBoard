using SwitchBoard.Models.Actions;

namespace SwitchBoard.Services.Execution.Handlers;

public sealed class DelayActionHandler : IActionHandler
{
    public string ActionType => ActionTypeIds.Delay;

    public async Task<ActionExecutionResult> ExecuteAsync(
        ActionDefinition action,
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var seconds = ActionParameterReader.ReadInt32(
            action.Parameters,
            ActionParameterNames.DelaySeconds,
            defaultValue: 0);
        if (seconds is < 0 or > 3600)
        {
            return ActionExecutionResult.Failure("Delay duration must be between 0 and 3600 seconds.");
        }

        await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
        return ActionExecutionResult.Success();
    }

    public Task RestoreAsync(
        ActionDefinition action,
        System.Text.Json.Nodes.JsonObject restoreState,
        ActionExecutionContext context,
        CancellationToken cancellationToken) => Task.CompletedTask;
}