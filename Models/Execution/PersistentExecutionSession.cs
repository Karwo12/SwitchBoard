using System.Text.Json.Nodes;
using SwitchBoard.Models.Actions;

namespace SwitchBoard.Models.Execution;

public sealed class PersistentExecutionSession
{
    public Guid SessionId { get; set; } = Guid.NewGuid();
    public Guid ProfileId { get; set; }
    public ExecutionOrigin Origin { get; set; } = ExecutionOrigin.ProfileRun;
    public string ProfileName { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public PersistentSessionStatus Status { get; set; } = PersistentSessionStatus.Preparing;
    public List<PersistentSessionAction> Actions { get; set; } = [];

    public IReadOnlyList<PersistentSessionAction> GetPendingRestoreEntries() =>
        Status == PersistentSessionStatus.Discarded
            ? []
            : Actions.Where(action => action.RequiresRestore && !action.IsRestored &&
                !string.Equals(action.ActionType, ActionTypeIds.Comment, StringComparison.OrdinalIgnoreCase)).ToList();

    public int PendingRestoreCount => GetPendingRestoreEntries().Count;

    public IReadOnlyList<PersistentSessionAction> DiscardPendingRestore()
    {
        var discarded = GetPendingRestoreEntries().ToList();
        Status = PersistentSessionStatus.Discarded;
        return discarded;
    }
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
    public JsonObject? StateAfter { get; set; }
    public string? RequestedState { get; set; }
    public JsonObject? RequestedConfiguration { get; set; }
    public bool RequiresRestore { get; set; }
    public bool ExecutionAttempted { get; set; }
    public bool ExecutionVerified { get; set; }
    public bool IsRestored { get; set; }
    public PersistentActionExecutionStatus ExecutionStatus { get; set; } = PersistentActionExecutionStatus.Pending;
    public string? ExecutionMessage { get; set; }
    public PersistentActionRestoreStatus RestoreStatus { get; set; } = PersistentActionRestoreStatus.NotRequired;
    public string? RestoreMessage { get; set; }
    public Guid ProfileId { get; set; }
    public Guid? ParentActionId { get; set; }
    public string? Branch { get; set; }
    public int NestingDepth { get; set; }
    public int AttemptCount { get; set; }
    public long ExecutionSequence { get; set; }
}
