using SwitchBoard.Models.Actions;
using SwitchBoard.Models.Execution;
using SwitchBoard.Models.Profiles;

namespace SwitchBoard.Services.Execution;

public sealed class ProfileRunner(IActionRegistry actionRegistry)
{
    private int _isRunning;

    public bool IsRunning => Volatile.Read(ref _isRunning) != 0;

    public async Task<ExecutionSession> RunAsync(
        ProfileDefinition profile,
        IProgress<ProfileExecutionProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (Interlocked.CompareExchange(ref _isRunning, 1, 0) != 0)
        {
            throw new InvalidOperationException("Another profile is already running.");
        }

        var session = new ExecutionSession
        {
            ProfileId = profile.Id,
            Status = ExecutionSessionStatus.Running
        };
        var context = new ActionExecutionContext(session.Id, profile.Id);
        var orderedActions = profile.Actions.OrderBy(action => action.SortOrder).ToList();
        var totalActiveActions = orderedActions.Count(action => action.IsEnabled);
        var currentActiveAction = 0;
        var hasFailures = false;
        ActionJournalEntry? runningEntry = null;
        ActionDefinition? runningAction = null;

        try
        {
            foreach (var action in orderedActions)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var journalEntry = new ActionJournalEntry
                {
                    ActionId = action.Id,
                    ActionType = action.Type
                };
                session.Journal.Add(journalEntry);

                if (!action.IsEnabled)
                {
                    journalEntry.Status = ActionJournalStatus.Skipped;
                    journalEntry.CompletedAt = DateTimeOffset.UtcNow;
                    continue;
                }

                currentActiveAction++;
                runningEntry = journalEntry;
                runningAction = action;
                journalEntry.Status = ActionJournalStatus.Running;
                progress?.Report(new ProfileExecutionProgress(
                    currentActiveAction,
                    totalActiveActions,
                    action,
                    journalEntry));

                if (!actionRegistry.TryGetHandler(action.Type, out var handler) || handler is null)
                {
                    journalEntry.Status = ActionJournalStatus.Unsupported;
                    hasFailures = true;
                    journalEntry.ErrorMessage = $"This action type is not implemented: {action.Type}";
                    journalEntry.CompletedAt = DateTimeOffset.UtcNow;
                    progress?.Report(new ProfileExecutionProgress(
                        currentActiveAction,
                        totalActiveActions,
                        action,
                        journalEntry));
                    runningEntry = null;
                    runningAction = null;

                    if (action.FailurePolicy != ActionFailurePolicy.Continue)
                    {
                        session.Status = ExecutionSessionStatus.Failed;
                        session.CompletedAt = DateTimeOffset.UtcNow;
                        return session;
                    }

                    continue;
                }

                ActionExecutionResult result;
                try
                {
                    result = await handler.ExecuteAsync(action, context, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    result = ActionExecutionResult.Failure(exception.Message);
                }

                journalEntry.CompletedAt = DateTimeOffset.UtcNow;
                journalEntry.RestoreState = result.RestoreState;
                journalEntry.ErrorMessage = result.Message;
                journalEntry.Status = result.IsSkipped
                    ? ActionJournalStatus.Skipped
                    : result.IsSuccessful
                        ? ActionJournalStatus.Success
                        : ActionJournalStatus.Failed;
                if (!result.IsSuccessful)
                {
                    hasFailures = true;
                }

                progress?.Report(new ProfileExecutionProgress(
                    currentActiveAction,
                    totalActiveActions,
                    action,
                    journalEntry));
                runningEntry = null;
                runningAction = null;

                if (!result.IsSuccessful && action.FailurePolicy != ActionFailurePolicy.Continue)
                {
                    session.Status = ExecutionSessionStatus.Failed;
                    session.CompletedAt = DateTimeOffset.UtcNow;
                    return session;
                }
            }

            session.Status = hasFailures
                ? ExecutionSessionStatus.Failed
                : ExecutionSessionStatus.Completed;
            session.CompletedAt = DateTimeOffset.UtcNow;
            return session;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (runningEntry is not null && runningAction is not null)
            {
                runningEntry.Status = ActionJournalStatus.Cancelled;
                runningEntry.CompletedAt = DateTimeOffset.UtcNow;
                runningEntry.ErrorMessage = "Execution was cancelled.";
                progress?.Report(new ProfileExecutionProgress(
                    currentActiveAction,
                    totalActiveActions,
                    runningAction,
                    runningEntry));
            }

            session.Status = ExecutionSessionStatus.Cancelled;
            session.CompletedAt = DateTimeOffset.UtcNow;
            return session;
        }
        finally
        {
            Volatile.Write(ref _isRunning, 0);
        }
    }
}