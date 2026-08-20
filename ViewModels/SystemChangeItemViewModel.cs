using SwitchBoard.Localization;
using SwitchBoard.Models.Actions;
using SwitchBoard.Services.Activity;

namespace SwitchBoard.ViewModels;

public sealed class SystemChangeItemViewModel
{
    public SystemChangeItemViewModel(SystemChangeEntry change, ILocalizationService localization)
    {
        Timestamp = change.Timestamp;
        ProfileId = change.ProfileId;
        ActionId = change.ActionId;
        FriendlyName = change.FriendlyName;
        Message = change.Message;
        Status = change.Status;
        StatusText = localization.GetString(change.Status switch
        {
            SystemChangeStatuses.Pending => "SystemChange.Pending",
            SystemChangeStatuses.Restored => "SystemChange.Restored",
            SystemChangeStatuses.Discarded or SystemChangeStatuses.LeftActive => "SystemChange.Discarded",
            SystemChangeStatuses.RestoreFailed => "SystemChange.RestoreFailed",
            SystemChangeStatuses.ExternalChange => "SystemChange.External",
            _ => "SystemChange.Pending"
        });
        Details = change.ActionType == ActionTypeIds.ServiceSetState
            ? BuildServiceDetails(change, localization)
            : change.Message;
    }

    public DateTimeOffset Timestamp { get; }
    public Guid? ProfileId { get; }
    public Guid ActionId { get; }
    public bool IsNavigable => ProfileId is not null;
    public string FriendlyName { get; }
    public string Details { get; }
    public string StatusText { get; }
    public string Status { get; }
    public string Message { get; }
    public bool IsUnresolved => Status is SystemChangeStatuses.Pending or SystemChangeStatuses.Discarded or
        SystemChangeStatuses.LeftActive or SystemChangeStatuses.RestoreFailed;

    private static string BuildServiceDetails(SystemChangeEntry change, ILocalizationService localization)
    {
        var beforeRuntime = change.StateBefore?["previousState"]?.GetValue<string>() ?? "unknown";
        var beforeStartup = change.StateBefore?["previousStartupType"]?.GetValue<string>() ?? "unknown";
        var afterRuntime = change.StateAfter?["runtimeState"]?.GetValue<string>() ??
                           change.RequestedState?[ActionParameterNames.DesiredState]?.GetValue<string>() ?? "unknown";
        var afterStartup = change.StateAfter?["startupType"]?.GetValue<string>() ??
                           change.RequestedState?[ActionParameterNames.ServiceStartupType]?.GetValue<string>() ?? "unknown";
        return localization.Format("SystemChange.ServiceDetails",
            LocalizeRuntime(beforeRuntime, localization), LocalizeRuntime(afterRuntime, localization),
            LocalizeStartup(beforeStartup, localization), LocalizeStartup(afterStartup, localization));
    }

    private static string LocalizeRuntime(string value, ILocalizationService localization) => value switch
    {
        ServiceDesiredStateIds.Running or "Running" => localization.GetString("ServiceState.Running"),
        ServiceDesiredStateIds.Stopped or "Stopped" => localization.GetString("ServiceState.Stopped"),
        ServiceDesiredStateIds.Unchanged => localization.GetString("ServiceState.Unchanged"),
        _ => value
    };

    private static string LocalizeStartup(string value, ILocalizationService localization) => value switch
    {
        ServiceStartupTypeIds.Automatic or "Automatic" => localization.GetString("ServiceStartupType.Automatic"),
        ServiceStartupTypeIds.AutomaticDelayed or "Automatic (Delayed Start)" =>
            localization.GetString("ServiceStartupType.AutomaticDelayed"),
        ServiceStartupTypeIds.Manual or "Manual" => localization.GetString("ServiceStartupType.Manual"),
        ServiceStartupTypeIds.Disabled or "Disabled" => localization.GetString("ServiceStartupType.Disabled"),
        ServiceStartupTypeIds.Unchanged => localization.GetString("ServiceStartupType.Unchanged"),
        _ => value
    };
}
