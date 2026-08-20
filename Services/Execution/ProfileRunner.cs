using SwitchBoard.Models.Actions;
using SwitchBoard.Models.Execution;
using SwitchBoard.Models.Profiles;
using SwitchBoard.Localization;
using SwitchBoard.Services.Activity;
using SwitchBoard.Services.Logging;
using SwitchBoard.Services.Persistence;
using System.Diagnostics;

namespace SwitchBoard.Services.Execution;

public sealed class ProfileRunner
{
    public const int MaximumNestingDepth = 9;
    private readonly IActionRegistry _actionRegistry;
    private readonly IExecutionSessionRepository _sessionRepository;
    private readonly IAppLogger? _logger;
    private readonly Func<Guid, ProfileDefinition?>? _profileResolver;
    private readonly IActivityService? _activity;
    private readonly ILocalizationService? _localization;
    private int _isRunning;

    public ProfileRunner(IActionRegistry actionRegistry, IExecutionSessionRepository sessionRepository,
        IAppLogger? logger = null, Func<Guid, ProfileDefinition?>? profileResolver = null,
        IActivityService? activity = null, ILocalizationService? localization = null)
    {
        _actionRegistry = actionRegistry;
        _sessionRepository = sessionRepository;
        _logger = logger;
        _profileResolver = profileResolver;
        _activity = activity;
        _localization = localization;
    }

    public bool IsRunning => Volatile.Read(ref _isRunning) != 0;

    public async Task<ExecutionSession> RunAsync(ProfileDefinition profile,
        IProgress<ProfileExecutionProgress>? progress = null, CancellationToken cancellationToken = default,
        ExecutionOrigin origin = ExecutionOrigin.ProfileRun)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
            throw new InvalidOperationException("Another profile is already running.");

        var session = new ExecutionSession { ProfileId = profile.Id, Origin = origin, Status = ExecutionSessionStatus.Running };
        var persistent = new PersistentExecutionSession
        {
            SessionId = session.Id,
            ProfileId = profile.Id,
            Origin = origin,
            ProfileName = profile.Name,
            Status = PersistentSessionStatus.Preparing
        };
        var state = new RunState(session, persistent, progress,
            profile.Actions.Count(action => action.IsEnabled));
        try
        {
            await _sessionRepository.SaveAsync(persistent, cancellationToken);
            persistent.Status = PersistentSessionStatus.Executing;
            await _sessionRepository.SaveAsync(persistent, cancellationToken);
            var result = await ExecuteProfileAsync(profile, null, null, [], state, cancellationToken);
            var status = !result.IsSuccessful ? ExecutionSessionStatus.Failed
                : state.HasFailures ? ExecutionSessionStatus.CompletedWithErrors
                : ExecutionSessionStatus.Completed;
            return await FinishAsync(session, persistent, status, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var running = persistent.Actions.LastOrDefault(item =>
                item.ExecutionStatus == PersistentActionExecutionStatus.Running);
            if (running is not null) running.ExecutionStatus = PersistentActionExecutionStatus.Cancelled;
            return await FinishAsync(session, persistent, ExecutionSessionStatus.Cancelled, CancellationToken.None);
        }
        catch
        {
            persistent.Status = PersistentSessionStatus.Failed;
            await _sessionRepository.SaveAsync(persistent, CancellationToken.None);
            throw;
        }
        finally { Volatile.Write(ref _isRunning, 0); }
    }

    private async Task<ActionExecutionResult> ExecuteProfileAsync(ProfileDefinition profile, Guid? parentActionId,
        string? branch, IReadOnlyList<Guid> activeStack, RunState state, CancellationToken cancellationToken)
    {
        if (activeStack.Contains(profile.Id))
        {
            var cycle = string.Join(" -> ", activeStack.Append(profile.Id));
            return ActionExecutionResult.Failure($"Profile cycle detected: {cycle}", false);
        }
        if (activeStack.Count >= MaximumNestingDepth)
            return ActionExecutionResult.Failure($"Maximum automation nesting depth ({MaximumNestingDepth}) was exceeded.", false);

        _activity?.Add(ActivityLevel.Info,
            FormatActivity("Activity.ProfileStarted", "Profile started: {0}", profile.Name), profile.Id);
        var stack = activeStack.Append(profile.Id).ToArray();
        return await ExecuteActionsAsync(profile.Actions, profile.Id, parentActionId, branch, stack,
            state, cancellationToken);
    }

    private async Task<ActionExecutionResult> ExecuteActionsAsync(IEnumerable<ActionDefinition> sourceActions,
        Guid profileId, Guid? parentActionId, string? branch, IReadOnlyList<Guid> activeStack,
        RunState state, CancellationToken cancellationToken)
    {
        var propagateFailures = parentActionId is not null;
        var localFailures = false;
        foreach (var action in sourceActions.OrderBy(action => action.SortOrder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var saved = CreatePersistentAction(action, profileId, parentActionId, branch,
                activeStack.Count - 1, ++state.ExecutionSequence);
            state.Persistent.Actions.Add(saved);
            var journal = new ActionJournalEntry
            {
                ActionId = action.Id,
                ActionType = action.Type,
                ProfileId = profileId,
                ParentActionId = parentActionId,
                Branch = branch,
                NestingDepth = activeStack.Count - 1
            };
            state.Session.Journal.Add(journal);

            if (!action.IsEnabled)
            {
                journal.Status = ActionJournalStatus.Skipped;
                journal.CompletedAt = DateTimeOffset.UtcNow;
                saved.ExecutionStatus = PersistentActionExecutionStatus.Skipped;
                await _sessionRepository.SaveAsync(state.Persistent, cancellationToken);
                continue;
            }

            state.Current++;
            journal.Status = ActionJournalStatus.Running;
            state.Progress?.Report(new(state.Current, Math.Max(state.Total, state.Current), action, journal));
            _activity?.Add(ActivityLevel.Info,
                FormatActivity("Activity.ActionStarted", "Action: {0}", ActionName(action)), profileId, action.Id);

            if (!_actionRegistry.TryGetHandler(action.Type, out var handler) || handler is null)
            {
                CompleteFailure(journal, saved, $"This action type is not implemented: {action.Type}", true);
                localFailures = true;
                ReportResult(action, profileId,
                    ActionExecutionResult.Failure(journal.ErrorMessage!, false), saved, state);
                await _sessionRepository.SaveAsync(state.Persistent, cancellationToken);
                state.Progress?.Report(new(state.Current, Math.Max(state.Total, state.Current), action, journal));
                if (action.FailurePolicy != ActionFailurePolicy.Continue)
                    return ActionExecutionResult.Failure(journal.ErrorMessage!, false);
                continue;
            }

            var baseContext = CreateContext(action, profileId, parentActionId, branch, activeStack, state);
            if (action.RestoreBehavior != ActionRestoreBehavior.DoNotRestore &&
                handler is IReversibleActionHandler reversible)
            {
                try
                {
                    saved.PreviousState = await reversible.CaptureStateAsync(action, baseContext, cancellationToken);
                    if (saved.PreviousState is null)
                        throw new InvalidOperationException("The action did not provide a restorable state.");
                    saved.RequiresRestore = false;
                    saved.RestoreStatus = PersistentActionRestoreStatus.NotRequired;
                    saved.ExecutionStatus = PersistentActionExecutionStatus.Prepared;
                    await _sessionRepository.SaveAsync(state.Persistent, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    _logger?.Error("ProfileRunner", exception,
                        $"ActionType={action.Type} ActionId={action.Id} Target={DescribeTarget(action)} failed during CaptureState.");
                    CompleteFailure(journal, saved, $"Could not capture the previous state: {exception.Message}");
                    localFailures = true;
                    ReportResult(action, profileId,
                        ActionExecutionResult.Failure(journal.ErrorMessage!, false), saved, state);
                    await _sessionRepository.SaveAsync(state.Persistent, cancellationToken);
                    if (action.FailurePolicy != ActionFailurePolicy.Continue)
                        return ActionExecutionResult.Failure(journal.ErrorMessage!, false);
                    continue;
                }
            }

            saved.ExecutionStatus = PersistentActionExecutionStatus.Running;
            saved.ExecutionAttempted = true;
            await _sessionRepository.SaveAsync(state.Persistent, cancellationToken);
            var context = baseContext with { CapturedState = saved.PreviousState?.DeepClone().AsObject() };
            var attempts = action.RetryOnFailure ? Math.Clamp(action.MaximumAttempts, 1, 10) : 1;
            ActionExecutionResult result = ActionExecutionResult.Failure("The action was not executed.");
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                journal.AttemptCount = saved.AttemptCount = attempt;
                var attemptStarted = Stopwatch.GetTimestamp();
                result = await ExecuteAttemptAsync(handler, action, context, cancellationToken);
                _logger?.Info("ProfileRunner",
                    $"ActionType={action.Type} ActionId={action.Id} Target={DescribeTarget(action)} Attempt={attempt} " +
                    $"StateBefore={DescribeState(saved.PreviousState)} RequestedState={DescribeRequestedState(action)} " +
                    $"ApiAndVerificationResult={(result.IsSuccessful ? (result.IsSkipped ? "Skipped" : "VerifiedSuccess") : "Failed")} " +
                    $"FinalResult={(result.IsSuccessful ? (result.IsSkipped ? "Skipped" : "Success") : "Failed")} " +
                    $"Message={result.Message} Technical={result.TechnicalDetails ?? "n/a"} " +
                    $"ElapsedMs={Stopwatch.GetElapsedTime(attemptStarted).TotalMilliseconds:0}");
                if (result.IsSuccessful || !result.IsRetryable || attempt == attempts) break;
                var delay = action.RetryDelay < TimeSpan.Zero ? TimeSpan.Zero : action.RetryDelay;
                if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            }

            if (saved.PreviousState is not null && result.RestoreState is not null)
            {
                saved.PreviousState ??= [];
                foreach (var property in result.RestoreState)
                    saved.PreviousState[property.Key] = property.Value?.DeepClone();
            }
            saved.StateAfter = result.StateAfter?.DeepClone().AsObject();
            if (action.RestoreBehavior != ActionRestoreBehavior.DoNotRestore &&
                handler is IReversibleActionHandler && saved.PreviousState is not null)
            {
                saved.ExecutionVerified = result.IsSuccessful || result.RestoreRequired.HasValue;
                saved.RequiresRestore = result.RestoreRequired ?? (result.IsSuccessful && !result.IsSkipped);
                saved.RestoreStatus = saved.RequiresRestore
                    ? PersistentActionRestoreStatus.Pending
                    : PersistentActionRestoreStatus.NotRequired;
            }
            CompleteResult(journal, saved, result);
            ReportResult(action, profileId, result, saved, state);
            if (!result.IsSuccessful) localFailures = true;
            await _sessionRepository.SaveAsync(state.Persistent, cancellationToken);
            state.Progress?.Report(new(state.Current, Math.Max(state.Total, state.Current), action, journal));
            if (!result.IsSuccessful && action.FailurePolicy != ActionFailurePolicy.Continue) return result;
        }
        return propagateFailures && localFailures
            ? ActionExecutionResult.Failure("One or more nested actions failed.", false)
            : ActionExecutionResult.Success();
    }

    private ActionExecutionContext CreateContext(ActionDefinition action, Guid profileId, Guid? parentActionId,
        string? branch, IReadOnlyList<Guid> activeStack, RunState state) => new(
        state.Session.Id, profileId, action.Id, ParentActionId: parentActionId, Branch: branch,
        NestingDepth: activeStack.Count - 1, ActiveProfileStack: activeStack,
        ExecuteProfileAsync: async (targetProfileId, compositeActionId, token) =>
        {
            if (_profileResolver?.Invoke(targetProfileId) is not { } target)
                return ActionExecutionResult.Failure("The selected profile no longer exists.", false);
            return await ExecuteProfileAsync(target, compositeActionId, "profile", activeStack, state, token);
        },
        ExecuteActionsAsync: async (nested, compositeActionId, nestedBranch, token) =>
        {
            if (activeStack.Count >= MaximumNestingDepth)
                return ActionExecutionResult.Failure(
                    $"Maximum automation nesting depth ({MaximumNestingDepth}) was exceeded.", false);
            return await ExecuteActionsAsync(nested, profileId, compositeActionId, nestedBranch,
                activeStack, state, token);
        },
        ResolveProfile: _profileResolver);

    private async Task<ActionExecutionResult> ExecuteAttemptAsync(IActionHandler handler,
        ActionDefinition action, ActionExecutionContext context, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (action.Timeout is { } timeout && timeout > TimeSpan.Zero) timeoutSource.CancelAfter(timeout);
        try { return await handler.ExecuteAsync(action, context, timeoutSource.Token); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutSource.IsCancellationRequested)
        { return ActionExecutionResult.Failure("The action timed out."); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            _logger?.Error("ProfileRunner", exception,
                $"ActionType={action.Type} ActionId={action.Id} Target={DescribeTarget(action)} threw during Execute/Verify.");
            return ActionExecutionResult.Failure(exception.Message);
        }
    }

    private void ReportResult(ActionDefinition action, Guid profileId, ActionExecutionResult result,
        PersistentSessionAction saved, RunState state)
    {
        var actionName = ActionName(action);
        var detail = string.IsNullOrWhiteSpace(result.Message) ||
                     string.Equals(result.Message, actionName, StringComparison.CurrentCulture)
            ? string.Empty
            : $" — {result.Message}";
        if (!result.IsSuccessful)
        {
            state.HasFailures = true;
            _logger?.Warning("ProfileRunner", $"Action {action.Id} ({action.Type}) failed: {result.Message}");
            RecordActivity(ActivityLevel.Error,
                FormatActivity("Activity.ActionFailed", "Action failed: {0}", actionName) + detail,
                action, profileId, saved, state, result, ActivityEventTypes.Failed);
        }
        else
        {
            var key = result.IsSkipped ? "Activity.ActionSkipped" : "Activity.ActionCompleted";
            var fallback = result.IsSkipped ? "Action skipped: {0}" : "Action completed: {0}";
            RecordActivity(result.IsSkipped ? ActivityLevel.Info : ActivityLevel.Success,
                FormatActivity(key, fallback, actionName) + detail, action, profileId, saved, state, result,
                result.IsSkipped ? ActivityEventTypes.Execute : ActivityEventTypes.Verify);
        }
    }

    private void RecordActivity(ActivityLevel level, string message, ActionDefinition action, Guid profileId,
        PersistentSessionAction saved, RunState state, ActionExecutionResult result, string eventType)
    {
        if (_activity is null) return;
        var observedChange = saved.RequiresRestore || result.RestoreRequired == true;
        _activity.Record(new PersistentActivityRecord
        {
            Origin = state.Persistent.Origin,
            SessionId = state.Persistent.SessionId,
            ProfileId = profileId,
            ProfileName = state.Persistent.ProfileName,
            ActionId = action.Id,
            ActionType = action.Type,
            FriendlyName = ActionName(action),
            EventType = eventType,
            Level = level,
            StateBefore = observedChange ? saved.PreviousState?.DeepClone().AsObject() : null,
            RequestedState = observedChange ? BuildRequestedState(action) : null,
            StateAfter = observedChange ? saved.StateAfter?.DeepClone().AsObject() : null,
            Result = result.IsSuccessful ? result.IsSkipped ? "skipped" : "success" : "failed",
            RestoreStatus = observedChange
                ? saved.RequiresRestore ? SystemChangeStatuses.Pending : SystemChangeStatuses.LeftActive
                : null,
            Message = message
        });
    }

    private async Task<ExecutionSession> FinishAsync(ExecutionSession execution,
        PersistentExecutionSession persistent, ExecutionSessionStatus status, CancellationToken cancellationToken)
    {
        execution.Status = status;
        execution.CompletedAt = DateTimeOffset.UtcNow;
        persistent.Status = persistent.PendingRestoreCount > 0 ? PersistentSessionStatus.RestorePending
            : status == ExecutionSessionStatus.Completed ? PersistentSessionStatus.Executed : PersistentSessionStatus.Failed;
        await _sessionRepository.SaveAsync(persistent, cancellationToken);
        var (level, key, fallback) = status switch
        {
            ExecutionSessionStatus.Completed => (ActivityLevel.Success, "Activity.ProfileCompleted", "Profile completed: {0}"),
            ExecutionSessionStatus.Cancelled => (ActivityLevel.Warning, "Activity.ProfileCancelled", "Profile cancelled: {0}"),
            ExecutionSessionStatus.CompletedWithErrors => (ActivityLevel.Warning, "Activity.ProfileCompletedWithErrors", "Profile completed with errors: {0}"),
            _ => (ActivityLevel.Error, "Activity.ProfileFailed", "Profile failed: {0}")
        };
        _activity?.Add(level, FormatActivity(key, fallback, persistent.ProfileName), persistent.ProfileId);
        return execution;
    }

    private string ActionName(ActionDefinition action)
    {
        if (action.Type == ActionTypeIds.ServiceSetState &&
            action.Parameters[ActionParameterNames.ServiceDisplayName]?.GetValue<string>() is { Length: > 0 } displayName)
            return displayName;
        return ActivityText.ActionName(action.Name, action.Type, _localization);
    }

    private string FormatActivity(string resourceKey, string fallback, params object?[] arguments) =>
        _localization is null
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture, fallback, arguments)
            : _localization.Format(resourceKey, arguments);

    private static PersistentSessionAction CreatePersistentAction(ActionDefinition action, Guid profileId,
        Guid? parentActionId, string? branch, int depth, long sequence) => new()
    {
        ActionId = action.Id, ActionType = action.Type, ActionName = action.Name, SortOrder = action.SortOrder,
        Timeout = action.Timeout, Parameters = action.Parameters.DeepClone().AsObject(), ProfileId = profileId,
        RequestedState = action.Parameters[ActionParameterNames.DesiredState]?.GetValue<string>(),
        RequestedConfiguration = BuildRequestedState(action),
        ParentActionId = parentActionId, Branch = branch, NestingDepth = depth, ExecutionSequence = sequence
    };

    private static void CompleteResult(ActionJournalEntry journal, PersistentSessionAction saved,
        ActionExecutionResult result)
    {
        journal.CompletedAt = DateTimeOffset.UtcNow;
        journal.RestoreState = saved.PreviousState ?? result.RestoreState;
        journal.ErrorMessage = result.Message;
        journal.Status = result.IsSkipped ? ActionJournalStatus.Skipped
            : result.IsSuccessful ? ActionJournalStatus.Success : ActionJournalStatus.Failed;
        saved.ExecutionMessage = result.Message;
        saved.ExecutionStatus = result.IsSkipped ? PersistentActionExecutionStatus.Skipped
            : result.IsSuccessful ? PersistentActionExecutionStatus.Success : PersistentActionExecutionStatus.Failed;
        if (result.IsSkipped)
        {
            saved.RequiresRestore = false;
            saved.RestoreStatus = PersistentActionRestoreStatus.NotRequired;
        }
    }

    private static void CompleteFailure(ActionJournalEntry journal, PersistentSessionAction saved,
        string message, bool unsupported = false)
    {
        journal.Status = unsupported ? ActionJournalStatus.Unsupported : ActionJournalStatus.Failed;
        journal.ErrorMessage = message;
        journal.CompletedAt = DateTimeOffset.UtcNow;
        saved.ExecutionStatus = unsupported ? PersistentActionExecutionStatus.Unsupported
            : PersistentActionExecutionStatus.Failed;
        saved.ExecutionMessage = message;
    }

    private static string DescribeTarget(ActionDefinition action)
    {
        foreach (var key in new[] { ActionParameterNames.ServiceName, ActionParameterNames.Target,
                     ActionParameterNames.ProcessName, ActionParameterNames.DeviceInstanceId,
                     ActionParameterNames.PowerPlanGuid, ActionParameterNames.ScriptPath,
                     ActionParameterNames.DisplayDeviceName })
            if (action.Parameters[key]?.GetValue<string>() is { Length: > 0 } value)
                return SanitizeLogValue(value);
        return action.Name ?? action.Type;
    }

    private static string DescribeRequestedState(ActionDefinition action)
    {
        var values = new List<string>();
        foreach (var key in new[] { ActionParameterNames.DesiredState, ActionParameterNames.ServiceStartupType,
                     ActionParameterNames.PowerPlanGuid,
                     ActionParameterNames.DisplayWidth, ActionParameterNames.DisplayHeight,
                     ActionParameterNames.DisplayRefreshRate, ActionParameterNames.VolumePercent,
                     ActionParameterNames.Mute, ActionParameterNames.ProcessPriority })
            if (action.Parameters[key] is { } node) values.Add($"{key}={SanitizeLogValue(node.ToJsonString())}");
        return values.Count == 0 ? "n/a" : string.Join(",", values);
    }

    private static System.Text.Json.Nodes.JsonObject BuildRequestedState(ActionDefinition action)
    {
        var result = new System.Text.Json.Nodes.JsonObject();
        foreach (var key in new[]
                 {
                     ActionParameterNames.ServiceName, ActionParameterNames.ServiceDisplayName,
                     ActionParameterNames.DesiredState, ActionParameterNames.ServiceStartupType,
                     ActionParameterNames.ProcessName, ActionParameterNames.ExecutablePath,
                     ActionParameterNames.PowerPlanGuid, ActionParameterNames.PowerPlanName,
                     ActionParameterNames.DisplayDeviceName, ActionParameterNames.DisplayName,
                     ActionParameterNames.DisplayWidth, ActionParameterNames.DisplayHeight,
                     ActionParameterNames.DisplayRefreshRate, ActionParameterNames.DeviceInstanceId,
                     ActionParameterNames.DeviceFriendlyName, ActionParameterNames.VolumePercent,
                     ActionParameterNames.Mute, ActionParameterNames.ProcessPriority
                 })
        {
            if (action.Parameters[key] is { } node) result[key] = node.DeepClone();
        }
        return result;
    }

    private static string DescribeState(System.Text.Json.Nodes.JsonObject? state)
    {
        if (state is null) return "not-captured";
        var values = new List<string>();
        foreach (var key in new[] { "previousState", "previousStartupType", "wasRunningBefore", "wasRunning", "instanceCount",
                     "previousPowerPlanGuid", "enabled", "affinityMask", "priority", "width", "height", "refreshRate" })
            if (state[key] is { } node) values.Add($"{key}={SanitizeLogValue(node.ToJsonString())}");
        return values.Count == 0 ? "captured(redacted)" : string.Join(",", values);
    }

    private static string SanitizeLogValue(string value) =>
        value.Replace('\r', ' ').Replace('\n', ' ').Length <= 300
            ? value.Replace('\r', ' ').Replace('\n', ' ')
            : value.Replace('\r', ' ').Replace('\n', ' ')[..300] + "…";

    private sealed class RunState(ExecutionSession session, PersistentExecutionSession persistent,
        IProgress<ProfileExecutionProgress>? progress, int total)
    {
        public ExecutionSession Session { get; } = session;
        public PersistentExecutionSession Persistent { get; } = persistent;
        public IProgress<ProfileExecutionProgress>? Progress { get; } = progress;
        public int Total { get; } = total;
        public int Current { get; set; }
        public long ExecutionSequence { get; set; }
        public bool HasFailures { get; set; }
    }
}
