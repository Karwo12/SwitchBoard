namespace SwitchBoard.Models.Execution;

public sealed class ExecutionSession
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public required Guid ProfileId { get; init; }

    public ExecutionOrigin Origin { get; init; } = ExecutionOrigin.ProfileRun;

    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? CompletedAt { get; set; }

    public ExecutionSessionStatus Status { get; set; } = ExecutionSessionStatus.Created;

    public List<ActionJournalEntry> Journal { get; } = [];
}
