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
        // WPF's Selector can receive the bound string value before SelectedValuePath
        // is applied. Return a concrete List so its non-generic IList.IndexOf does
        // not cast that string directly to LocalizedValueOptionViewModel.
        ActionTypeIds.Comment => Build(localization,
            ("none", "RestoreBehavior.None")),
        ActionTypeIds.ProgramRun =>
            Build(localization, ("none", "RestoreBehavior.None"), ("closeStarted", "RestoreBehavior.CloseStarted")),
        ActionTypeIds.ProcessConfigure when string.Equals(operation, ProcessOperationIds.Stop, StringComparison.OrdinalIgnoreCase) =>
            Build(localization, ("none", "RestoreBehavior.None"), ("restart", "RestoreBehavior.RestartProcess")),
        ActionTypeIds.ProcessConfigure =>
            Build(localization, ("none", "RestoreBehavior.None"), ("previous", "RestoreBehavior.ProcessSettings")),
        ActionTypeIds.PowerSetPlan =>
            Build(localization, ("none", "RestoreBehavior.None"), ("previous", "RestoreBehavior.PreviousPlan")),
        ActionTypeIds.DisplayConfigure =>
            Build(localization, ("none", "RestoreBehavior.None"), ("previous", "RestoreBehavior.PreviousDisplay"),
                ("custom", "RestoreBehavior.Custom")),
        ActionTypeIds.ScriptRun =>
            Build(localization, ("none", "RestoreBehavior.None"), ("closeStarted", "RestoreBehavior.CloseStarted"),
                ("restoreScript", "RestoreBehavior.RestoreScript")),
        ActionTypeIds.AudioConfigure =>
            Build(localization, ("none", "RestoreBehavior.None"), ("previous", "RestoreBehavior.AudioSettings")),
        ActionTypeIds.DeviceSetState =>
            Build(localization, ("none", "RestoreBehavior.None"), ("previous", "RestoreBehavior.DeviceState")),
        _ => Build(localization, ("none", "RestoreBehavior.None"), ("previous", "RestoreBehavior.Previous"))
    };

    private static IReadOnlyList<LocalizedValueOptionViewModel> Build(
        ILocalizationService localization,
        params (string Value, string ResourceKey)[] values) => values
        .Select(value => new LocalizedValueOptionViewModel(value.Value, value.ResourceKey, localization))
        .ToList();
}
