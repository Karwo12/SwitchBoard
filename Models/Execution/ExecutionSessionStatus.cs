namespace SwitchBoard.Models.Execution;

public enum ExecutionSessionStatus
{
    Created,
    Running,
    Active,
    Stopping,
    Completed,
    Failed,
    Cancelled
}
