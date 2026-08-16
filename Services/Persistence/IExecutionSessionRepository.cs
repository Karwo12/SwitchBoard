using SwitchBoard.Models.Execution;

namespace SwitchBoard.Services.Persistence;

public interface IExecutionSessionRepository
{
    Task SaveAsync(PersistentExecutionSession session, CancellationToken cancellationToken = default);
    Task<PersistentExecutionSession?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default);
    Task<PersistentExecutionSession?> GetLatestPendingAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task<PersistentExecutionSession?> GetLatestAttentionAsync(Guid profileId, CancellationToken cancellationToken = default);
    Task MaintainAsync(TimeSpan restoredRetention, CancellationToken cancellationToken = default);
}
