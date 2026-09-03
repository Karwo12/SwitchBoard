using SwitchBoard.Localization;
using SwitchBoard.Services.Activity;

namespace SwitchBoard.ViewModels;

public sealed class ProfileExecutionViewModel : ObservableObject, IDisposable
{
    private bool _isExpanded;

    public ProfileExecutionViewModel(ProfileExecutionSummary summary, ILocalizationService localization,
        Func<Guid?, Guid, ActionItemViewModel?>? sourceActionLookup = null)
    {
        SessionId = summary.SessionId;
        ProfileId = summary.ProfileId;
        ProfileName = summary.ProfileName;
        Timestamp = summary.StartedAt;
        Result = summary.Result;
        StatusText = localization.GetString(summary.Result switch
        {
            ProfileExecutionResult.Success => "Status.ProfileCompleted",
            ProfileExecutionResult.Warning => "Status.ProfileCompletedWithErrors",
            ProfileExecutionResult.Cancelled => "Status.ProfileCancelled",
            _ => "Status.ProfileFailed"
        });
        Actions = summary.Actions
            .Select(action => new ProfileExecutionActionViewModel(action, localization,
                sourceActionLookup?.Invoke(action.ProfileId, action.ActionId)))
            .ToList();
        DurationText = FormatDuration(localization, summary.StartedAt, summary.CompletedAt);
        ActionSummaryText = localization.Format("Activity.HistorySummary", Actions.Count, DurationText);
        ProgressText = summary.Result == ProfileExecutionResult.Error
            ? localization.Format("Activity.HistoryProgress", summary.SuccessfulActionCount, Actions.Count)
            : string.Empty;
        RestoreText = summary.IsRestored
            ? localization.GetString("Activity.HistoryRestored")
            : summary.HasRestoreFailure
                ? localization.GetString("Activity.HistoryRestoreFailed")
                : string.Empty;
        PhaseText = summary.IsPostRestore
            ? localization.GetString("Activity.PostRestorePhase")
            : string.Empty;
        ToggleExpandedCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    public Guid SessionId { get; }
    public Guid? ProfileId { get; }
    public string ProfileName { get; }
    public DateTimeOffset Timestamp { get; }
    public ProfileExecutionResult Result { get; }
    public string StatusText { get; }
    public string DurationText { get; }
    public string ActionSummaryText { get; }
    public string ProgressText { get; }
    public string RestoreText { get; }
    public string PhaseText { get; }
    public IReadOnlyList<ProfileExecutionActionViewModel> Actions { get; }
    public bool HasActions => Actions.Count > 0;
    public RelayCommand ToggleExpandedCommand { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public void Dispose()
    {
        foreach (var action in Actions) action.Dispose();
    }

    private static string FormatDuration(ILocalizationService localization,
        DateTimeOffset? startedAt, DateTimeOffset? completedAt)
    {
        if (startedAt is not { } start || completedAt is not { } end || end < start)
            return localization.GetString("Activity.DurationUnknown");
        return localization.Format("Activity.DurationSeconds", Math.Max(0, (end - start).TotalSeconds));
    }
}

public sealed class ProfileExecutionActionViewModel : IDisposable
{
    private readonly ActivityIconViewModel _icon;

    public ProfileExecutionActionViewModel(ProfileExecutionActionSummary summary,
        ILocalizationService localization, ActionItemViewModel? sourceAction = null)
    {
        ActionId = summary.ActionId;
        ProfileId = summary.ProfileId;
        Name = summary.Name;
        Description = CleanDescription(summary.Message, summary.Name);
        Result = summary.Result;
        Level = summary.Level;
        DurationText = FormatDuration(localization, summary.StartedAt, summary.CompletedAt);
        StatusText = localization.GetString(summary.Result switch
        {
            "success" => "Execution.Status.Success",
            "skipped" => "Execution.Status.Skipped",
            "cancelled" => "Execution.Status.Cancelled",
            _ => "Execution.Status.Failed"
        });
        _icon = new ActivityIconViewModel(sourceAction, summary.ActionType);
    }

    public Guid ActionId { get; }
    public Guid? ProfileId { get; }
    public string Name { get; }
    public string Description { get; }
    public string Result { get; }
    public ActivityLevel Level { get; }
    public string DurationText { get; }
    public string StatusText { get; }
    public ActivityIconViewModel IconPresentation => _icon;
    public System.Windows.Media.ImageSource? Icon => _icon.Icon;
    public bool HasIcon => _icon.HasIcon;

    public void Dispose() => _icon.Dispose();

    private static string FormatDuration(ILocalizationService localization,
        DateTimeOffset? startedAt, DateTimeOffset? completedAt)
    {
        if (startedAt is not { } start || completedAt is not { } end || end < start)
            return localization.GetString("Activity.DurationUnknown");
        return localization.Format("Activity.DurationSeconds", Math.Max(0, (end - start).TotalSeconds));
    }

    private static string CleanDescription(string message, string name)
    {
        var result = message.Trim();
        var separator = result.IndexOf(':');
        if (separator is >= 0 and <= 18)
            result = result[(separator + 1)..].Trim();
        if (result.StartsWith(name, StringComparison.CurrentCultureIgnoreCase))
        {
            result = result[name.Length..].TrimStart();
            if (result.StartsWith('—')) result = result[1..].Trim();
        }
        return result;
    }
}
