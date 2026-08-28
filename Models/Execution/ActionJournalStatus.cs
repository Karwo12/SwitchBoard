namespace SwitchBoard.Models.Execution;

public enum ActionJournalStatus
{
    Pending,
    Running,
    Success,
    Failed,
    Skipped,
    Cancelled,
    Unsupported,
    Restored,
    RestoreFailed
}
