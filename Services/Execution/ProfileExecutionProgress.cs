using SwitchBoard.Models.Actions;
using SwitchBoard.Models.Execution;

namespace SwitchBoard.Services.Execution;

public sealed record ProfileExecutionProgress(
    int CurrentActionNumber,
    int TotalActiveActions,
    ActionDefinition Action,
    ActionJournalEntry JournalEntry)
{
    public Guid ActionId => JournalEntry.ActionId;
    public ActionJournalStatus Status { get; init; } = JournalEntry.Status;
    public string? ErrorMessage { get; init; } = JournalEntry.ErrorMessage;
}
