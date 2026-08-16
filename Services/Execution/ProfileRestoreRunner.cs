using SwitchBoard.Models.Actions;
using SwitchBoard.Models.Execution;
using SwitchBoard.Services.Persistence;
using SwitchBoard.Services.Logging;

namespace SwitchBoard.Services.Execution;

public sealed class ProfileRestoreRunner(IActionRegistry actionRegistry, IExecutionSessionRepository repository,
    IAppLogger? logger = null)
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
            session.Status = PersistentSessionStatus.Restoring;
            await repository.SaveAsync(session, cancellationToken);
            var pending = session.Actions.Where(item => item.RequiresRestore && !item.IsRestored)
                .OrderByDescending(item => item.SortOrder).ToList();
            var current = 0;
            foreach (var item in pending)
            {
                cancellationToken.ThrowIfCancellationRequested();
                current++;
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
                        RestoreBehavior = ActionRestoreBehavior.RestorePreviousState
                    };
                    await handler.RestoreAsync(action, item.PreviousState,
                        new ActionExecutionContext(session.SessionId, session.ProfileId), cancellationToken);
                    item.IsRestored = true;
                    item.RestoreStatus = PersistentActionRestoreStatus.Restored;
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
                }
                await repository.SaveAsync(session, cancellationToken);
                progress?.Report(new(current, pending.Count, item, item.RestoreStatus, item.RestoreMessage));
            }
            session.Status = session.PendingRestoreCount == 0 ? PersistentSessionStatus.Restored : PersistentSessionStatus.PartiallyRestored;
            await repository.SaveAsync(session, cancellationToken);
            return session;
        }
        finally { Volatile.Write(ref _isRunning, 0); }
    }
}
