using SwitchBoard.Services.Discovery;

namespace SwitchBoard.Services.Windows;

public interface IWindowsServiceManager
{
    Task<IReadOnlyList<ServiceCandidate>> GetServicesAsync(CancellationToken cancellationToken = default);

    Task<string> GetStateAsync(string serviceName, CancellationToken cancellationToken = default);

    Task<WindowsServiceSnapshot> GetSnapshotAsync(
        string serviceName,
        CancellationToken cancellationToken = default);

    Task<WindowsServiceOperationResult> SetStateAsync(
        string serviceName,
        string desiredState,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    Task<WindowsServiceConfigurationResult> SetConfigurationAsync(
        string serviceName,
        string desiredState,
        string desiredStartupType,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);
}

public sealed record WindowsServiceSnapshot(string RuntimeState, string StartupType);

public sealed record WindowsServiceConfigurationResult(
    bool IsSuccessful,
    bool IsSkipped,
    WindowsServiceSnapshot? StateBefore,
    WindowsServiceSnapshot? CurrentState,
    string RequestedRuntimeState,
    string RequestedStartupType,
    string? Message = null,
    int? Win32Error = null,
    bool WasRestartedByWindows = false);

public sealed record WindowsServiceOperationResult(
    bool IsSuccessful,
    bool IsSkipped,
    string? Message = null,
    string? StateBefore = null,
    string? CurrentState = null,
    string? ExpectedState = null,
    int? Win32Error = null,
    bool WasRestartedByWindows = false);
