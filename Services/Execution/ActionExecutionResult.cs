using System.Text.Json.Nodes;

namespace SwitchBoard.Services.Execution;

public sealed record ActionExecutionResult(
    bool IsSuccessful,
    bool IsSkipped = false,
    string? Message = null,
    JsonObject? RestoreState = null)
{
    public static ActionExecutionResult Success(string? message = null, JsonObject? restoreState = null) =>
        new(true, Message: message, RestoreState: restoreState);

    public static ActionExecutionResult Skipped(string? message = null) =>
        new(true, IsSkipped: true, Message: message);

    public static ActionExecutionResult Failure(string errorMessage) =>
        new(false, Message: errorMessage);
}