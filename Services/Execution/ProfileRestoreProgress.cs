using SwitchBoard.Models.Execution;

namespace SwitchBoard.Services.Execution;

public sealed record ProfileRestoreProgress(int CurrentAction, int TotalActions,
    PersistentSessionAction Action, PersistentActionRestoreStatus Status, string? Message = null);
