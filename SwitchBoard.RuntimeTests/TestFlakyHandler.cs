using System.Text.Json.Nodes;
using SwitchBoard.Models.Actions;
using SwitchBoard.Services.Execution;

sealed class TestFlakyHandler : IActionHandler
{
    public const string TypeId = "test.flaky";
    public string ActionType => TypeId;
    public int Attempts { get; private set; }
    public Task<ActionExecutionResult> ExecuteAsync(ActionDefinition action, ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Attempts++;
        var failAlways = action.Parameters["failAlways"]?.GetValue<bool>() ?? false;
        return Task.FromResult(failAlways || Attempts < 3
            ? ActionExecutionResult.Failure($"Controlled failure {Attempts}")
            : ActionExecutionResult.Success());
    }
    public Task<ActionExecutionResult> RestoreAsync(ActionDefinition action, JsonObject restoreState, ActionExecutionContext context,
        CancellationToken cancellationToken) => Task.FromResult(ActionExecutionResult.Skipped());
}
