namespace SwitchBoard.Services.Discovery;

public interface IProcessDiscoveryService
{
    Task<IReadOnlyList<ProcessCandidate>> GetProcessesAsync(
        CancellationToken cancellationToken = default);
}
