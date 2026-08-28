using SwitchBoard.Services.Discovery;

namespace SwitchBoard.Services.Windows;

public interface IDeviceManager
{
    Task<IReadOnlyList<DeviceCandidate>> GetDevicesAsync(CancellationToken cancellationToken = default);
    Task<DeviceCandidate?> GetDeviceAsync(string instanceId, CancellationToken cancellationToken = default);
    Task SetEnabledAsync(string instanceId, bool enabled, CancellationToken cancellationToken = default);
}
