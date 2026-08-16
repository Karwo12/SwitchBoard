using SwitchBoard.Services.Discovery;

namespace SwitchBoard.Services.Windows;

public interface IWindowsServiceManager
{
    Task<IReadOnlyList<ServiceCandidate>> GetServicesAsync(CancellationToken cancellationToken = default);

    Task<string> GetStateAsync(string serviceName, CancellationToken cancellationToken = default);

    Task<WindowsServiceOperationResult> SetStateAsync(
        string serviceName,
        string desiredState,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed record WindowsServiceOperationResult(bool IsSuccessful, bool IsSkipped, string? Message = null);
