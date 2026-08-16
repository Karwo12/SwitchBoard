namespace SwitchBoard.Models.Execution;

public enum ActionJournalStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Skipped,
    Unsupported,
    Restored,
    RestoreFailed
}
