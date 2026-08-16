using SwitchBoard.Models.Actions;
using SwitchBoard.Models.Execution;

namespace SwitchBoard.Services.Execution;

public sealed record ProfileExecutionProgress(
    int CurrentActionNumber,
    int TotalActiveActions,
    ActionDefinition Action,
    ActionJournalEntry JournalEntry);