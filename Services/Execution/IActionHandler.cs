using System.Text.Json.Nodes;
using SwitchBoard.Models.Actions;

namespace SwitchBoard.Services.Execution;

public interface IActionHandler
{
    string ActionType { get; }

    Task<ActionExecutionResult> ExecuteAsync(
        ActionDefinition action,
        ActionExecutionContext context,
        CancellationToken cancellationToken);

    Task RestoreAsync(
        ActionDefinition action,
        JsonObject restoreState,
        ActionExecutionContext context,
        CancellationToken cancellationToken);
}

public interface IReversibleActionHandler : IActionHandler
{
    Task<JsonObject?> CaptureStateAsync(
        ActionDefinition action,
        ActionExecutionContext context,
        CancellationToken cancellationToken);
}
