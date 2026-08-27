using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using SwitchBoard.Models.Actions;
using SwitchBoard.Localization;
using SwitchBoard.Services.Windows;

namespace SwitchBoard.Services.Execution.Handlers;

public sealed class ConditionIfActionHandler(IWindowsServiceManager serviceManager,
    ILocalizationService? localization = null) : IActionHandler
{
    public string ActionType => ActionTypeIds.ConditionIf;

    public async Task<ActionExecutionResult> ExecuteAsync(ActionDefinition action, ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (context.ExecuteActionsAsync is null || context.ActionId is null)
            return ActionExecutionResult.Failure("Nested action execution is not available.", false);
        var type = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.ConditionType);
        var value = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.ConditionValue).Trim();
        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(value))
            return ActionExecutionResult.Failure(Format("Result.ConditionIncomplete",
                "The condition is incomplete."), false);

        bool condition;
        switch (type)
        {
            case ConditionTypeIds.ProcessRunning:
            case ConditionTypeIds.ProcessNotRunning:
                var matches = ProcessTargetResolver.Find(value);
                var running = matches.Count > 0;
                foreach (var process in matches) process.Dispose();
                condition = type == ConditionTypeIds.ProcessRunning ? running : !running;
                break;
            case ConditionTypeIds.ServiceRunning:
            case ConditionTypeIds.ServiceStopped:
                var state = await serviceManager.GetStateAsync(value, cancellationToken);
                condition = type == ConditionTypeIds.ServiceRunning
                    ? string.Equals(state, ServiceDesiredStateIds.Running, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(state, ServiceDesiredStateIds.Stopped, StringComparison.OrdinalIgnoreCase);
                break;
            case ConditionTypeIds.FileExists:
                condition = File.Exists(value);
                break;
            case ConditionTypeIds.FileNotExists:
                condition = !File.Exists(value);
                break;
            default:
                return ActionExecutionResult.Failure(Format("Result.ConditionUnsupported",
                    "Unsupported condition: {0}", type), false);
        }

        var branch = condition ? "then" : "else";
        var parameter = condition ? ActionParameterNames.ThenActions : ActionParameterNames.ElseActions;
        var actions = ReadActions(action.Parameters[parameter] as JsonArray);
        if (actions.Count == 0)
            return ActionExecutionResult.Skipped(Format("Result.ConditionBranchEmpty",
                "The {0} branch is empty.", condition ? "Then" : "Otherwise"));
        return await context.ExecuteActionsAsync(actions, context.ActionId.Value, branch, cancellationToken);
    }

    public Task<ActionExecutionResult> RestoreAsync(ActionDefinition action, JsonObject restoreState, ActionExecutionContext context,
        CancellationToken cancellationToken) => Task.FromResult(ActionExecutionResult.Skipped("No restore is required."));

    private static List<ActionDefinition> ReadActions(JsonArray? array)
    {
        if (array is null) return [];
        var result = new List<ActionDefinition>();
        foreach (var node in array)
        {
            try
            {
                if (ActionDefinitionJson.Deserialize(node) is { } action) result.Add(action);
            }
            catch (Exception exception) when (exception is JsonException or InvalidOperationException or NotSupportedException)
            {
                throw new InvalidDataException("A nested condition action is malformed.", exception);
            }
        }
        return result;
    }

    private string Format(string key, string fallback, params object?[] arguments) => localization is null
        ? string.Format(System.Globalization.CultureInfo.CurrentCulture, fallback, arguments)
        : localization.Format(key, arguments);
}
