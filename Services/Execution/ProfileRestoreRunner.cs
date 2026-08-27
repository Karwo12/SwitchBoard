using SwitchBoard.Models.Actions;
using SwitchBoard.Models.Execution;
using SwitchBoard.Localization;
using SwitchBoard.Services.Activity;
using SwitchBoard.Services.Persistence;
using SwitchBoard.Services.Logging;
using System.Diagnostics;

namespace SwitchBoard.Services.Execution;

public sealed class ProfileRestoreRunner(IActionRegistry actionRegistry, IExecutionSessionRepository repository,
    IAppLogger? logger = null, IActivityService? activity = null, ILocalizationService? localization = null)
{
    private int _isRunning;
    public bool IsRunning => Volatile.Read(ref _isRunning) != 0;

    public async Task<PersistentExecutionSession> RunAsync(PersistentExecutionSession session,
        IProgress<ProfileRestoreProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
            throw new InvalidOperationException("Another restore operation is already running.");
        try
        {
            activity?.Add(ActivityLevel.Info,
                FormatActivity("Activity.RestoreStarted", "Restoring profile: {0}", session.ProfileName),
                session.ProfileId);
            session.Status = PersistentSessionStatus.Restoring;
            await repository.SaveAsync(session, cancellationToken);
            var pending = session.GetPendingRestoreEntries()
                .Where(item => !string.Equals(item.ActionType, ActionTypeIds.Comment, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => item.ExecutionSequence).ThenByDescending(item => item.SortOrder).ToList();
            var current = 0;
            foreach (var item in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                current++;
                var actionName = GetActionName(item);
                var profileId = item.ProfileId == Guid.Empty ? session.ProfileId : item.ProfileId;
                activity?.Add(ActivityLevel.Info,
                    FormatActivity("Activity.RestoreActionStarted", "Restoring action: {0}", actionName),
                    profileId, item.ActionId);
                item.RestoreStatus = PersistentActionRestoreStatus.Restoring;
                item.RestoreMessage = null;
                await repository.SaveAsync(session, cancellationToken);
                progress?.Report(new(current, pending.Count, item, item.RestoreStatus));
                try
                {
                    if (item.PreviousState is null) throw new InvalidOperationException("The previous state is missing.");
                    if (!actionRegistry.TryGetHandler(item.ActionType, out var handler) || handler is not IReversibleActionHandler)
                        throw new InvalidOperationException($"Restore is not supported for action type '{item.ActionType}'.");
                    var action = new ActionDefinition
                    {
                        Id = item.ActionId, Type = item.ActionType, Name = item.ActionName, SortOrder = item.SortOrder,
                        Timeout = item.Timeout, Parameters = item.Parameters.DeepClone().AsObject(),
                        RestoreBehavior = item.RestoreBehavior
                    };
                    var startedAt = Stopwatch.GetTimestamp();
                    var result = await handler.RestoreAsync(action, item.PreviousState,
                        new ActionExecutionContext(session.SessionId,
                            item.ProfileId == Guid.Empty ? session.ProfileId : item.ProfileId,
                            item.ActionId, ParentActionId: item.ParentActionId,
                            Branch: item.Branch, NestingDepth: item.NestingDepth,
                            Logger: logger), cancellationToken);
                    var elapsed = Stopwatch.GetElapsedTime(startedAt);
                    logger?.Info("RestoreRunner",
                        $"ActionType={item.ActionType} ActionId={item.ActionId} Target={DescribeTarget(item)} " +
                        $"RestoreResult={(result.IsSuccessful ? (result.IsSkipped ? "Skipped" : "VerifiedSuccess") : "Failed")} " +
                        $"Message={result.Message} Technical={result.TechnicalDetails ?? "n/a"} ElapsedMs={elapsed.TotalMilliseconds:0}");
                    if (!result.IsSuccessful)
                        throw new InvalidOperationException(result.Message ?? "The restored state could not be verified.");
                    item.IsRestored = true;
                    item.RestoreStatus = PersistentActionRestoreStatus.Restored;
                    item.RestoreMessage = result.Message;
                    RecordRestoreActivity(ActivityLevel.Success,
                        FormatActivity("Activity.RestoreActionCompleted", "Action restored: {0}", actionName) +
                        FormatDetail(result.Message, actionName), session, item, actionName,
                        SystemChangeStatuses.Restored, result.StateAfter);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    item.RestoreStatus = PersistentActionRestoreStatus.Pending;
                    await repository.SaveAsync(session, CancellationToken.None);
                    throw;
                }
                catch (Exception exception)
                {
                    item.RestoreStatus = PersistentActionRestoreStatus.Failed;
                    item.RestoreMessage = exception.Message;
                    logger?.Error("RestoreRunner", exception, $"Restore failed for action {item.ActionId} ({item.ActionType}).");
                    RecordRestoreActivity(ActivityLevel.Error,
                        FormatActivity("Activity.RestoreActionFailed", "Action restore failed: {0}", actionName) +
                        $" — {exception.Message}", session, item, actionName,
                        SystemChangeStatuses.RestoreFailed, null);
                }
                await repository.SaveAsync(session, cancellationToken);
                progress?.Report(new(current, pending.Count, item, item.RestoreStatus, item.RestoreMessage));
            }
            session.Status = session.PendingRestoreCount == 0 ? PersistentSessionStatus.Restored : PersistentSessionStatus.PartiallyRestored;
            await repository.SaveAsync(session, cancellationToken);
            activity?.Add(session.PendingRestoreCount == 0 ? ActivityLevel.Success : ActivityLevel.Warning,
                session.PendingRestoreCount == 0
                    ? FormatActivity("Activity.RestoreCompleted", "Profile restored: {0}", session.ProfileName)
                    : FormatActivity("Activity.RestorePartial", "Profile partially restored: {0}", session.ProfileName),
                session.ProfileId);
            return session;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            session.Status = session.PendingRestoreCount == 0
                ? PersistentSessionStatus.Restored
                : PersistentSessionStatus.RestoreCancelled;
            await repository.SaveAsync(session, CancellationToken.None);
            activity?.Add(ActivityLevel.Warning,
                FormatActivity("Activity.RestoreCancelled", "Profile restore cancelled: {0}", session.ProfileName),
                session.ProfileId);
            throw;
        }
        catch (Exception exception)
        {
            activity?.Add(ActivityLevel.Error,
                FormatActivity("Activity.RestoreFailed", "Profile restore failed: {0}", session.ProfileName) +
                $" — {exception.Message}", session.ProfileId);
            throw;
        }
        finally { Volatile.Write(ref _isRunning, 0); }
    }

    private string FormatActivity(string resourceKey, string fallback, params object?[] arguments) =>
        localization is null
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture, fallback, arguments)
            : localization.Format(resourceKey, arguments);

    private string GetActionName(PersistentSessionAction item)
    {
        if (item.ActionType == ActionTypeIds.ServiceSetState &&
            item.Parameters[ActionParameterNames.ServiceDisplayName]?.GetValue<string>() is { Length: > 0 } displayName)
            return displayName;
        return ActivityText.ActionName(item.ActionName, item.ActionType, localization);
    }

    private void RecordRestoreActivity(ActivityLevel level, string message, PersistentExecutionSession session,
        PersistentSessionAction item, string actionName, string status,
        System.Text.Json.Nodes.JsonObject? stateAfter)
    {
        activity?.Record(new PersistentActivityRecord
        {
            SessionId = session.SessionId,
            ProfileId = item.ProfileId == Guid.Empty ? session.ProfileId : item.ProfileId,
            ProfileName = session.ProfileName,
            ActionId = item.ActionId,
            ActionType = item.ActionType,
            FriendlyName = actionName,
            EventType = status == SystemChangeStatuses.Restored
                ? ActivityEventTypes.Restore
                : ActivityEventTypes.Failed,
            Level = level,
            StateBefore = item.StateAfter?.DeepClone().AsObject(),
            RequestedState = item.PreviousState?.DeepClone().AsObject(),
            StateAfter = stateAfter?.DeepClone().AsObject(),
            Result = status == SystemChangeStatuses.Restored ? "success" : "failed",
            RestoreStatus = status,
            Message = message
        });
    }

    private static string FormatDetail(string? message, string actionName) =>
        string.IsNullOrWhiteSpace(message) || string.Equals(message, actionName, StringComparison.CurrentCulture)
            ? string.Empty
            : $" — {message}";

    private static string DescribeTarget(PersistentSessionAction item)
    {
        foreach (var key in new[] { ActionParameterNames.ServiceName, ActionParameterNames.Target,
                     ActionParameterNames.ProcessName, ActionParameterNames.DeviceInstanceId,
                     ActionParameterNames.PowerPlanGuid, ActionParameterNames.ScriptPath })
            if (item.Parameters[key]?.GetValue<string>() is { Length: > 0 } value)
                return value.Replace('\r', ' ').Replace('\n', ' ');
        return item.ActionName ?? item.ActionType;
    }
}
