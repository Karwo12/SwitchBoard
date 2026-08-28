using SwitchBoard.Localization;
using SwitchBoard.Models.Actions;

namespace SwitchBoard.Services.Activity;

internal static class ActivityText
{
    public static string ActionName(string? name, string actionType, ILocalizationService? localization)
    {
        if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
        var resourceKey = actionType switch
        {
            ActionTypeIds.ProcessSetState => "Action.ProcessState",
            ActionTypeIds.ProgramRun => "Action.RunProgram",
            ActionTypeIds.ServiceSetState => "Action.WindowsServiceState",
            ActionTypeIds.DisplayConfigure => "Action.DisplaySettings",
            ActionTypeIds.PowerSetPlan => "Action.PowerPlan",
            ActionTypeIds.ScriptRun => "Action.RunScript",
            ActionTypeIds.Delay => "Action.Delay",
            ActionTypeIds.ProcessConfigure => "Action.ProcessSettings",
            ActionTypeIds.WaitProcessStart => "Action.WaitProcess",
            ActionTypeIds.WaitProcessExit => "Action.WaitProcessExit",
            ActionTypeIds.WaitWindow => "Action.WaitWindow",
            ActionTypeIds.AudioConfigure => "Action.AudioSettings",
            ActionTypeIds.DeviceSetState => "Action.DeviceState",
            ActionTypeIds.ProfileRun => "Action.RunProfile",
            ActionTypeIds.ConditionIf => "Action.If",
            ActionTypeIds.NotificationShow => "Action.Notification",
            ActionTypeIds.Comment => "Action.Comment",
            _ => null
        };
        return resourceKey is null || localization is null ? actionType : localization.GetString(resourceKey);
    }
}
