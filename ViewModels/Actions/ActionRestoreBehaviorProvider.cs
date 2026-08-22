using SwitchBoard.Localization;
using SwitchBoard.Models.Actions;

namespace SwitchBoard.ViewModels.Actions;

internal static class ActionRestoreBehaviorProvider
{
    public static IReadOnlyList<LocalizedValueOptionViewModel> Get(
        string actionType,
        string operation,
        ILocalizationService localization) => actionType switch
    {
        ActionTypeIds.ProgramRun =>
        [new("none", "RestoreBehavior.None", localization), new("closeStarted", "RestoreBehavior.CloseStarted", localization)],
        ActionTypeIds.ProcessConfigure when string.Equals(operation, ProcessOperationIds.Stop, StringComparison.OrdinalIgnoreCase) =>
        [new("none", "RestoreBehavior.None", localization), new("restart", "RestoreBehavior.RestartProcess", localization)],
        ActionTypeIds.ProcessConfigure =>
        [new("none", "RestoreBehavior.None", localization), new("previous", "RestoreBehavior.ProcessSettings", localization)],
        ActionTypeIds.PowerSetPlan =>
        [new("none", "RestoreBehavior.None", localization), new("previous", "RestoreBehavior.PreviousPlan", localization)],
        ActionTypeIds.DisplayConfigure =>
        [new("none", "RestoreBehavior.None", localization), new("previous", "RestoreBehavior.PreviousDisplay", localization)],
        ActionTypeIds.ScriptRun =>
        [new("none", "RestoreBehavior.None", localization), new("restoreScript", "RestoreBehavior.RestoreScript", localization)],
        ActionTypeIds.AudioConfigure =>
        [new("none", "RestoreBehavior.None", localization), new("previous", "RestoreBehavior.AudioSettings", localization)],
        ActionTypeIds.DeviceSetState =>
        [new("none", "RestoreBehavior.None", localization), new("previous", "RestoreBehavior.DeviceState", localization)],
        _ => [new("none", "RestoreBehavior.None", localization), new("previous", "RestoreBehavior.Previous", localization)]
    };
}
