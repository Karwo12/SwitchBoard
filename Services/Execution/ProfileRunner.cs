using SwitchBoard.Models.Actions;
using SwitchBoard.Models.Execution;
using SwitchBoard.Models.Profiles;

namespace SwitchBoard.Services.Execution;

public sealed class ProfileRunner(IActionRegistry actionRegistry)
{
    public async Task<ExecutionSession> RunAsync(
        ProfileDefinition profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);

        var session = new ExecutionSession
        {
            ProfileId = profile.Id,
            Status = ExecutionSessionStatus.Running
        };
        var context = new ActionExecutionContext(session.Id, profile.Id);

        try
        {
            foreach (var action in profile.Actions)
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

                if (!actionRegistry.TryGetHandler(action.Type, out var handler) || handler is null)
                {
                    journalEntry.Status = ActionJournalStatus.Unsupported;
                    journalEntry.ErrorMessage = $"No handler is registered for '{action.Type}'.";
                    journalEntry.CompletedAt = DateTimeOffset.UtcNow;

                    if (action.FailurePolicy != ActionFailurePolicy.Continue)
                    {
                        session.Status = ExecutionSessionStatus.Failed;
                        session.CompletedAt = DateTimeOffset.UtcNow;
                        return session;
                    }

                    continue;
                }

                journalEntry.Status = ActionJournalStatus.Running;
                var result = await handler.ExecuteAsync(action, context, cancellationToken);
                journalEntry.CompletedAt = DateTimeOffset.UtcNow;
                journalEntry.RestoreState = result.RestoreState;

                if (result.IsSuccessful)
                {
                    journalEntry.Status = ActionJournalStatus.Succeeded;
                    continue;
                }

                journalEntry.Status = ActionJournalStatus.Failed;
                journalEntry.ErrorMessage = result.ErrorMessage;

                if (action.FailurePolicy == ActionFailurePolicy.StopAndRollback)
                {
                    await RestoreCompletedActionsAsync(profile, session, context, cancellationToken);
                }

                if (action.FailurePolicy != ActionFailurePolicy.Continue)
                {
                    session.Status = ExecutionSessionStatus.Failed;
                    session.CompletedAt = DateTimeOffset.UtcNow;
                    return session;
                }
            }

            session.Status = ExecutionSessionStatus.Active;
            return session;
        }
        catch (OperationCanceledException)
        {
            session.Status = ExecutionSessionStatus.Cancelled;
            session.CompletedAt = DateTimeOffset.UtcNow;
            return session;
        }
    }

    public async Task StopAsync(
        ProfileDefinition profile,
        ExecutionSession session,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(session);

        session.Status = ExecutionSessionStatus.Stopping;
        var context = new ActionExecutionContext(session.Id, profile.Id);
        await RestoreCompletedActionsAsync(profile, session, context, cancellationToken);
        session.Status = ExecutionSessionStatus.Completed;
        session.CompletedAt = DateTimeOffset.UtcNow;
    }

    private async Task RestoreCompletedActionsAsync(
        ProfileDefinition profile,
        ExecutionSession session,
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var actionsById = profile.Actions.ToDictionary(action => action.Id);

        foreach (var journalEntry in session.Journal.AsEnumerable().Reverse())
        {
            if (journalEntry.Status != ActionJournalStatus.Succeeded ||
                journalEntry.RestoreState is null ||
                !actionsById.TryGetValue(journalEntry.ActionId, out var action) ||
                !actionRegistry.TryGetHandler(journalEntry.ActionType, out var handler) ||
                handler is null)
            {
                continue;
            }

            try
            {
                await handler.RestoreAsync(
                    action,
                    journalEntry.RestoreState,
                    context,
                    cancellationToken);
                journalEntry.Status = ActionJournalStatus.Restored;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                journalEntry.Status = ActionJournalStatus.RestoreFailed;
                journalEntry.ErrorMessage = exception.Message;
            }
        }
    }
}
