using SwitchBoard.Localization;
using SwitchBoard.Models.Actions;
using SwitchBoard.Models.Execution;
using SwitchBoard.Services.Activity;

namespace SwitchBoard.ViewModels;

public sealed class SystemChangeItemViewModel : IDisposable
{
    private readonly ActivityIconViewModel _icon;

    public SystemChangeItemViewModel(SystemChangeEntry change, ILocalizationService localization,
        ActionItemViewModel? sourceAction = null)
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
        var details = change.ActionType == ActionTypeIds.ServiceSetState
            ? BuildServiceDetails(change, localization)
            : change.Message;
        Details = change.Origin == ExecutionOrigin.PostRestore
            ? string.Concat(localization.GetString("Activity.PostRestorePhase"), " - ", details)
            : details;
        ProcessSearchText = BuildProcessSearchText(change);
        _icon = new ActivityIconViewModel(sourceAction, change.ActionType);
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
    public ActivityIconViewModel IconPresentation => _icon;
    public System.Windows.Media.ImageSource? Icon => _icon.Icon;
    public bool HasIcon => _icon.HasIcon;
    /// <summary>Process identifiers persisted with the change, used only by the activity filter.</summary>
    public string ProcessSearchText { get; }
    public bool IsUnresolved => Status is SystemChangeStatuses.Pending or SystemChangeStatuses.Discarded or
        SystemChangeStatuses.LeftActive or SystemChangeStatuses.RestoreFailed;

    public void Dispose() => _icon.Dispose();

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

    private static string BuildProcessSearchText(SystemChangeEntry change)
    {
        var values = new List<string>();
        foreach (var state in new[] { change.StateBefore, change.RequestedState, change.StateAfter })
        {
            foreach (var key in new[]
                     {
                         ActionParameterNames.ProcessName, ActionParameterNames.ExecutablePath,
                         "targetProcessName"
                     })
            {
                if (state?[key]?.GetValue<string>() is { Length: > 0 } value) values.Add(value);
            }
        }
        return string.Join(" ", values);
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
