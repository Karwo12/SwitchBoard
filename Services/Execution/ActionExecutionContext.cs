namespace SwitchBoard.Services.Execution;

public sealed record ActionExecutionContext(
    Guid SessionId,
    Guid ProfileId);
