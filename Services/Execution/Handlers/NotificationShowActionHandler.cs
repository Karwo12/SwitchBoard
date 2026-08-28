using System.Text.Json.Nodes;
using SwitchBoard.Models.Actions;
using SwitchBoard.Services.Activity;

namespace SwitchBoard.Services.Execution.Handlers;

public sealed class NotificationShowActionHandler(IActivityService activityService) : IActionHandler
{
    public string ActionType => ActionTypeIds.NotificationShow;

    public Task<ActionExecutionResult> ExecuteAsync(ActionDefinition action, ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var message = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.NotificationMessage).Trim();
        if (string.IsNullOrWhiteSpace(message))
            return Task.FromResult(ActionExecutionResult.Failure("Notification message is required.", false));
        var level = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.NotificationLevel) switch
        {
            NotificationLevelIds.Success => ActivityLevel.Success,
            NotificationLevelIds.Warning => ActivityLevel.Warning,
            NotificationLevelIds.Error => ActivityLevel.Error,
            _ => ActivityLevel.Info
        };
        activityService.Add(level, message, context.ProfileId, action.Id);
        return Task.FromResult(ActionExecutionResult.Success(message));
    }

    public Task<ActionExecutionResult> RestoreAsync(ActionDefinition action, JsonObject restoreState, ActionExecutionContext context,
        CancellationToken cancellationToken) => Task.FromResult(ActionExecutionResult.Skipped("Notifications do not require restore."));
}
