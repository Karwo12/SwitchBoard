namespace SwitchBoard.Services.Discovery;

public interface IProgramDiscoveryService
{
    Task SearchAsync(
        ProgramSearchMode mode,
        IProgress<ProgramDiscoveryProgress> progress,
        CancellationToken cancellationToken = default);
}
