using SwitchBoard.Models.Actions;
using SwitchBoard.Models.Execution;
using SwitchBoard.Models.Profiles;
using SwitchBoard.Services.Persistence;
using SwitchBoard.Services.Logging;

namespace SwitchBoard.Services.Execution;

public sealed class ProfileRunner(IActionRegistry actionRegistry, IExecutionSessionRepository sessionRepository,
    IAppLogger? logger = null)
{
    private int _isRunning;
    public bool IsRunning => Volatile.Read(ref _isRunning) != 0;

    public async Task<ExecutionSession> RunAsync(ProfileDefinition profile,
        IProgress<ProfileExecutionProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
            throw new InvalidOperationException("Another profile is already running.");

        var session = new ExecutionSession { ProfileId = profile.Id, Status = ExecutionSessionStatus.Running };
        var context = new ActionExecutionContext(session.Id, profile.Id);
        var ordered = profile.Actions.OrderBy(action => action.SortOrder).ToList();
        var persistent = new PersistentExecutionSession
        {
            SessionId = session.Id, ProfileId = profile.Id, ProfileName = profile.Name,
            Status = PersistentSessionStatus.Preparing,
            Actions = ordered.Select(action => new PersistentSessionAction
            {
                ActionId = action.Id, ActionType = action.Type, ActionName = action.Name,
                SortOrder = action.SortOrder, Timeout = action.Timeout,
                Parameters = action.Parameters.DeepClone().AsObject()
            }).ToList()
        };
        try
        {
            await sessionRepository.SaveAsync(persistent, cancellationToken);
            persistent.Status = PersistentSessionStatus.Executing;
            await sessionRepository.SaveAsync(persistent, cancellationToken);
        }
        catch
        {
            Volatile.Write(ref _isRunning, 0);
            throw;
        }

        var activeTotal = ordered.Count(action => action.IsEnabled);
        var current = 0;
        var hasFailures = false;
        try
        {
            foreach (var action in ordered)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var saved = persistent.Actions.Single(item => item.ActionId == action.Id);
                var journal = new ActionJournalEntry { ActionId = action.Id, ActionType = action.Type };
                session.Journal.Add(journal);
                if (!action.IsEnabled)
                {
                    journal.Status = ActionJournalStatus.Skipped;
                    journal.CompletedAt = DateTimeOffset.UtcNow;
                    saved.ExecutionStatus = PersistentActionExecutionStatus.Skipped;
                    await sessionRepository.SaveAsync(persistent, cancellationToken);
                    continue;
                }

                current++;
                journal.Status = ActionJournalStatus.Running;
                progress?.Report(new(current, activeTotal, action, journal));
                if (!actionRegistry.TryGetHandler(action.Type, out var handler) || handler is null)
                {
                    CompleteFailure(journal, saved, $"This action type is not implemented: {action.Type}", true);
                    hasFailures = true;
                    await sessionRepository.SaveAsync(persistent, cancellationToken);
                    progress?.Report(new(current, activeTotal, action, journal));
                    if (action.FailurePolicy != ActionFailurePolicy.Continue)
                        return await FinishAsync(session, persistent, ExecutionSessionStatus.Failed, cancellationToken);
                    continue;
                }

                if (action.RestoreBehavior != ActionRestoreBehavior.DoNotRestore && handler is IReversibleActionHandler reversible)
                {
                    try
                    {
                        saved.PreviousState = await reversible.CaptureStateAsync(action, context, cancellationToken);
                        if (saved.PreviousState is null) throw new InvalidOperationException("The action did not provide a restorable state.");
                        saved.RequiresRestore = true;
                        saved.RestoreStatus = PersistentActionRestoreStatus.Pending;
                        saved.ExecutionStatus = PersistentActionExecutionStatus.Prepared;
                        await sessionRepository.SaveAsync(persistent, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception exception)
                    {
                        CompleteFailure(journal, saved, $"Could not capture the previous state: {exception.Message}");
                        hasFailures = true;
                        await sessionRepository.SaveAsync(persistent, cancellationToken);
                        progress?.Report(new(current, activeTotal, action, journal));
                        if (action.FailurePolicy != ActionFailurePolicy.Continue)
                            return await FinishAsync(session, persistent, ExecutionSessionStatus.Failed, cancellationToken);
                        continue;
                    }
                }

                saved.ExecutionStatus = PersistentActionExecutionStatus.Running;
                await sessionRepository.SaveAsync(persistent, cancellationToken);
                ActionExecutionResult result;
                try { result = await handler.ExecuteAsync(action, context, cancellationToken); }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                catch (Exception exception) { result = ActionExecutionResult.Failure(exception.Message); }

                if (saved.RequiresRestore && result.RestoreState is not null)
                {
                    saved.PreviousState ??= [];
                    foreach (var property in result.RestoreState)
                        saved.PreviousState[property.Key] = property.Value?.DeepClone();
                }

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
                if (!result.IsSuccessful) hasFailures = true;
                if (!result.IsSuccessful)
                    logger?.Warning("ProfileRunner", $"Action {action.Id} ({action.Type}) failed: {result.Message}");
                await sessionRepository.SaveAsync(persistent, cancellationToken);
                progress?.Report(new(current, activeTotal, action, journal));
                if (!result.IsSuccessful && action.FailurePolicy != ActionFailurePolicy.Continue)
                    return await FinishAsync(session, persistent, ExecutionSessionStatus.Failed, cancellationToken);
            }

            return await FinishAsync(session, persistent,
                hasFailures ? ExecutionSessionStatus.CompletedWithErrors : ExecutionSessionStatus.Completed,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            var running = persistent.Actions.FirstOrDefault(item => item.ExecutionStatus == PersistentActionExecutionStatus.Running);
            if (running is not null) running.ExecutionStatus = PersistentActionExecutionStatus.Cancelled;
            return await FinishAsync(session, persistent, ExecutionSessionStatus.Cancelled, CancellationToken.None);
        }
        finally { Volatile.Write(ref _isRunning, 0); }
    }

    private async Task<ExecutionSession> FinishAsync(ExecutionSession execution, PersistentExecutionSession persistent,
        ExecutionSessionStatus status, CancellationToken cancellationToken)
    {
        execution.Status = status;
        execution.CompletedAt = DateTimeOffset.UtcNow;
        persistent.Status = persistent.PendingRestoreCount > 0 ? PersistentSessionStatus.RestorePending
            : status == ExecutionSessionStatus.Completed ? PersistentSessionStatus.Executed : PersistentSessionStatus.Failed;
        await sessionRepository.SaveAsync(persistent, cancellationToken);
        return execution;
    }

    private static void CompleteFailure(ActionJournalEntry journal, PersistentSessionAction saved, string message,
        bool unsupported = false)
    {
        journal.Status = unsupported ? ActionJournalStatus.Unsupported : ActionJournalStatus.Failed;
        journal.ErrorMessage = message;
        journal.CompletedAt = DateTimeOffset.UtcNow;
        saved.ExecutionStatus = unsupported ? PersistentActionExecutionStatus.Unsupported : PersistentActionExecutionStatus.Failed;
        saved.ExecutionMessage = message;
    }
}
