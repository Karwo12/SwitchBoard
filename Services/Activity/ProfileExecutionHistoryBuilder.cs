using SwitchBoard.Models.Actions;

namespace SwitchBoard.Services.Activity;

public enum ProfileExecutionResult
{
    Success,
    Warning,
    Error,
    Cancelled
}

public sealed record ProfileExecutionActionSummary(
    Guid ActionId,
    Guid? ProfileId,
    string Name,
    string Message,
    ActivityLevel Level,
    string Result,
    DateTimeOffset Timestamp,
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null);

public sealed record ProfileExecutionSummary(
    Guid SessionId,
    Guid? ProfileId,
    string ProfileName,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    ProfileExecutionResult Result,
    IReadOnlyList<ProfileExecutionActionSummary> Actions,
    bool IsRestored,
    bool HasRestoreFailure)
{
    public int SuccessfulActionCount => Actions.Count(action =>
        action.Result is "success" or "skipped");
}

/// <summary>
/// Creates the user-facing profile-run history from the same durable activity records
/// that power the activity and system-change views.
/// </summary>
public static class ProfileExecutionHistoryBuilder
{
    public static IReadOnlyList<ProfileExecutionSummary> Build(
        IEnumerable<PersistentActivityRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);

        return records
            .Where(record => record.SessionId.HasValue && !string.IsNullOrWhiteSpace(record.ProfileName))
            .GroupBy(record => record.SessionId!.Value)
            .Select(BuildSession)
            .OrderByDescending(session => session.StartedAt)
            .ToList();
    }

    private static ProfileExecutionSummary BuildSession(
        IGrouping<Guid, PersistentActivityRecord> group)
    {
        var records = group.OrderBy(record => record.Timestamp).ToList();
        var started = records.FirstOrDefault(record =>
            record.EventType == ActivityEventTypes.ProfileStarted);
        var completed = records.LastOrDefault(record =>
            record.EventType == ActivityEventTypes.ProfileCompleted);
        var actionRecords = records
            .Where(IsExecutionActionRecord)
            .GroupBy(record => record.ActionId!.Value)
            .Select(action =>
            {
                var ordered = action.OrderBy(record => record.Timestamp).ToList();
                var latest = ordered[^1];
                var startedAt = ordered.Select(record => record.StartedAt)
                    .FirstOrDefault(value => value.HasValue);
                var completedAt = ordered.Select(record => record.CompletedAt)
                    .LastOrDefault(value => value.HasValue);

                // Old records only have the event timestamp. A single event is not
                // enough to claim a duration, so keep it unknown instead of showing
                // a fabricated zero-second result.
                if (startedAt is null && ordered.Count > 1) startedAt = ordered[0].Timestamp;
                if (completedAt is null && ordered.Count > 1) completedAt = latest.Timestamp;

                return new ProfileExecutionActionSummary(
                    latest.ActionId!.Value,
                    latest.ProfileId,
                    latest.FriendlyName ?? latest.ActionType ?? string.Empty,
                    latest.Message,
                    latest.Level,
                    latest.Result ?? string.Empty,
                    latest.Timestamp,
                    startedAt,
                    completedAt);
            })
            .ToList();

        var result = ResolveResult(completed, actionRecords);
        var isRestored = records.Any(record =>
            record.RestoreStatus == SystemChangeStatuses.Restored ||
            record.EventType == ActivityEventTypes.Restore && record.Level == ActivityLevel.Success);
        var hasRestoreFailure = records.Any(record =>
            record.RestoreStatus == SystemChangeStatuses.RestoreFailed ||
            record.EventType == ActivityEventTypes.Restore && record.Level == ActivityLevel.Error);

        return new ProfileExecutionSummary(
            group.Key,
            started?.ProfileId ?? records.FirstOrDefault()?.ProfileId,
            started?.ProfileName ?? records.First(record => !string.IsNullOrWhiteSpace(record.ProfileName)).ProfileName!,
            started?.Timestamp ?? records[0].Timestamp,
            completed?.Timestamp,
            result,
            actionRecords,
            isRestored,
            hasRestoreFailure);
    }

    private static bool IsExecutionActionRecord(PersistentActivityRecord record) =>
        record.ActionId.HasValue && record.Result is not null &&
        !string.Equals(record.ActionType, ActionTypeIds.Comment, StringComparison.OrdinalIgnoreCase) &&
        record.EventType is ActivityEventTypes.Execute or ActivityEventTypes.Verify or ActivityEventTypes.Failed;

    private static ProfileExecutionResult ResolveResult(
        PersistentActivityRecord? completed,
        IReadOnlyList<ProfileExecutionActionSummary> actions)
    {
        if (completed?.Result is "cancelled") return ProfileExecutionResult.Cancelled;
        if (completed?.Result is "failed" || actions.Any(action => action.Result == "failed"))
            return ProfileExecutionResult.Error;
        if (completed?.Result is "warning" || actions.Any(action => action.Level == ActivityLevel.Warning))
            return ProfileExecutionResult.Warning;
        return ProfileExecutionResult.Success;
    }
}
