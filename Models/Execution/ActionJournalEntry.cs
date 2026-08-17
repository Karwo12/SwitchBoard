using System.Text.Json.Nodes;

namespace SwitchBoard.Models.Execution;

public sealed class ActionJournalEntry
{
    public required Guid ActionId { get; init; }

    public required string ActionType { get; init; }

    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? CompletedAt { get; set; }

    public ActionJournalStatus Status { get; set; } = ActionJournalStatus.Pending;

    public string? ErrorMessage { get; set; }

    public JsonObject? RestoreState { get; set; }

    public Guid ProfileId { get; set; }

    public Guid? ParentActionId { get; set; }

    public string? Branch { get; set; }

    public int NestingDepth { get; set; }

    public int AttemptCount { get; set; }
}
