using System.Text.Json.Nodes;

namespace SwitchBoard.Services.Execution;

public sealed record ActionExecutionResult(
    bool IsSuccessful,
    string? ErrorMessage = null,
    JsonObject? RestoreState = null)
{
    public static ActionExecutionResult Success(JsonObject? restoreState = null) =>
        new(true, RestoreState: restoreState);

    public static ActionExecutionResult Failure(string errorMessage) =>
        new(false, errorMessage);
}
