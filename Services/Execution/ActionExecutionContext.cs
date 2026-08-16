namespace SwitchBoard.Services.Execution;

public sealed record ActionExecutionContext(
    Guid SessionId,
    Guid ProfileId,
    Guid? ActionId = null,
    System.Text.Json.Nodes.JsonObject? CapturedState = null);
