using SwitchBoard.Models.Actions;
using SwitchBoard.Models.Profiles;

namespace SwitchBoard.Services.Execution;

public sealed record ActionExecutionContext(
    Guid SessionId,
    Guid ProfileId,
    Guid? ActionId = null,
    System.Text.Json.Nodes.JsonObject? CapturedState = null,
    Guid? ParentActionId = null,
    string? Branch = null,
    int NestingDepth = 0,
    IReadOnlyList<Guid>? ActiveProfileStack = null,
    Func<Guid, Guid, CancellationToken, Task<ActionExecutionResult>>? ExecuteProfileAsync = null,
    Func<IReadOnlyList<ActionDefinition>, Guid, string, CancellationToken, Task<ActionExecutionResult>>? ExecuteActionsAsync = null,
    Func<Guid, ProfileDefinition?>? ResolveProfile = null);
