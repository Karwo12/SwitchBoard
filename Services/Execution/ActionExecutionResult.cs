using System.Text.Json.Nodes;

namespace SwitchBoard.Services.Execution;

public sealed record ActionExecutionResult(
    bool IsSuccessful,
    bool IsSkipped = false,
    string? Message = null,
    JsonObject? RestoreState = null,
    bool IsRetryable = true,
    string? TechnicalDetails = null,
    bool? RestoreRequired = null,
    JsonObject? StateAfter = null)
{
    public static ActionExecutionResult Success(string? message = null, JsonObject? restoreState = null,
        bool? restoreRequired = null, JsonObject? stateAfter = null, string? technicalDetails = null) =>
        new(true, Message: message, RestoreState: restoreState, RestoreRequired: restoreRequired,
            StateAfter: stateAfter, TechnicalDetails: technicalDetails);

    public static ActionExecutionResult Skipped(string? message = null) =>
        new(true, IsSkipped: true, Message: message, RestoreRequired: false);

    public static ActionExecutionResult Failure(string errorMessage, bool isRetryable = true,
        string? technicalDetails = null, bool? restoreRequired = null, JsonObject? stateAfter = null) =>
        new(false, Message: errorMessage, IsRetryable: isRetryable, TechnicalDetails: technicalDetails,
            RestoreRequired: restoreRequired, StateAfter: stateAfter);
}
