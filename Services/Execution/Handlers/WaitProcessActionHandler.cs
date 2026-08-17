using System.Text.Json.Nodes;
using SwitchBoard.Models.Actions;

namespace SwitchBoard.Services.Execution.Handlers;

public sealed class WaitProcessActionHandler(string actionType) : IActionHandler
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);
    public string ActionType { get; } = actionType is ActionTypeIds.WaitProcessStart or ActionTypeIds.WaitProcessExit
        ? actionType : throw new ArgumentOutOfRangeException(nameof(actionType));

    public async Task<ActionExecutionResult> ExecuteAsync(ActionDefinition action, ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var name = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.ProcessName);
        var path = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.ExecutablePath);
        if (string.IsNullOrWhiteSpace(name)) return ActionExecutionResult.Failure("Process name is required.");
        var waitForStart = ActionType == ActionTypeIds.WaitProcessStart;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matches = ProcessTargetResolver.Find(name, path, action.RuntimeProcessIdHint);
            var exists = matches.Count > 0;
            foreach (var process in matches) process.Dispose();
            if (exists == waitForStart)
                return ActionExecutionResult.Success(waitForStart ? "The process is running." : "The process has exited.");
            await Task.Delay(PollInterval, cancellationToken);
        }
    }

    public Task<ActionExecutionResult> RestoreAsync(ActionDefinition action, JsonObject restoreState, ActionExecutionContext context,
        CancellationToken cancellationToken) => Task.FromResult(ActionExecutionResult.Skipped("Wait actions do not require restore."));
}
