using System.Text.Json.Nodes;
using SwitchBoard.Models.Actions;

namespace SwitchBoard.Services.Execution.Handlers;

public sealed class ProfileRunActionHandler : IActionHandler
{
    public string ActionType => ActionTypeIds.ProfileRun;

    public async Task<ActionExecutionResult> ExecuteAsync(ActionDefinition action, ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var value = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.ProfileId);
        if (!Guid.TryParse(value, out var profileId))
            return ActionExecutionResult.Failure("A valid target profile is required.", false);
        if (context.ExecuteProfileAsync is null || context.ActionId is null)
            return ActionExecutionResult.Failure("Nested profile execution is not available.", false);
        return await context.ExecuteProfileAsync(profileId, context.ActionId.Value, cancellationToken);
    }

    public Task<ActionExecutionResult> RestoreAsync(ActionDefinition action, JsonObject restoreState, ActionExecutionContext context,
        CancellationToken cancellationToken) => Task.FromResult(ActionExecutionResult.Skipped("Nested actions restore independently."));
}
