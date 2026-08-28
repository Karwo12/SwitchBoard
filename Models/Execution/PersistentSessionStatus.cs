namespace SwitchBoard.Models.Execution;

public enum PersistentSessionStatus
{
    Preparing,
    Executing,
    Executed,
    RestorePending,
    Restoring,
    PartiallyRestored,
    Restored,
    Failed,
    RecoveryRequired,
    Discarded,
    RestoreCancelled
}

public enum PersistentActionExecutionStatus
{
    Pending,
    Prepared,
    Running,
    Success,
    Skipped,
    Failed,
    Cancelled,
    Unsupported
}

public enum PersistentActionRestoreStatus
{
    NotRequired,
    Pending,
    Restoring,
    Restored,
    Failed
}
