using System.Text.Json.Nodes;

namespace SwitchBoard.Models.Execution;

public sealed class PersistentExecutionSession
{
    public Guid SessionId { get; set; } = Guid.NewGuid();
    public Guid ProfileId { get; set; }
    public string ProfileName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public PersistentSessionStatus Status { get; set; } = PersistentSessionStatus.Preparing;
    public List<PersistentSessionAction> Actions { get; set; } = [];

    public int PendingRestoreCount => Actions.Count(action => action.RequiresRestore && !action.IsRestored);
}

public sealed class PersistentSessionAction
{
    public Guid ActionId { get; set; }
    public string ActionType { get; set; } = string.Empty;
    public string? ActionName { get; set; }
    public int SortOrder { get; set; }
    public TimeSpan? Timeout { get; set; }
    public JsonObject Parameters { get; set; } = [];
    public JsonObject? PreviousState { get; set; }
    public bool RequiresRestore { get; set; }
    public bool IsRestored { get; set; }
    public PersistentActionExecutionStatus ExecutionStatus { get; set; } = PersistentActionExecutionStatus.Pending;
    public string? ExecutionMessage { get; set; }
    public PersistentActionRestoreStatus RestoreStatus { get; set; } = PersistentActionRestoreStatus.NotRequired;
    public string? RestoreMessage { get; set; }
}
