namespace SwitchBoard.Models.Actions;

public enum ActionRestoreBehavior
{
    DoNotRestore,
    RestorePreviousState,
    CloseIfStartedBySwitchBoard,
    RestartIfWasRunning,
    RunRestoreScript,
    RestoreCustomState
}
