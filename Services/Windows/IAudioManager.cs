using SwitchBoard.Services.Discovery;

namespace SwitchBoard.Services.Windows;

public interface IAudioManager
{
    Task<IReadOnlyList<AudioDeviceCandidate>> GetDevicesAsync(CancellationToken cancellationToken = default);
    Task<AudioDeviceCandidate?> GetDefaultDeviceAsync(bool input, bool communications,
        CancellationToken cancellationToken = default);
    Task<string?> GetDefaultDeviceIdAsync(bool input, bool communications, CancellationToken cancellationToken = default);
    Task SetDefaultDeviceAsync(string deviceId, bool multimedia, bool communications,
        CancellationToken cancellationToken = default);
    Task<(float Volume, bool Muted)> GetMasterVolumeAsync(string? deviceId = null,
        CancellationToken cancellationToken = default);
    Task SetMasterVolumeAsync(float? volume, bool? muted, string? deviceId = null,
        CancellationToken cancellationToken = default);
}
