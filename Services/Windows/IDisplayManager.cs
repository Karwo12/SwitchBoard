using SwitchBoard.Services.Discovery;

namespace SwitchBoard.Services.Windows;

public interface IDisplayManager
{
    Task<IReadOnlyList<DisplayCandidate>> GetDisplaysAsync(CancellationToken cancellationToken = default);

    Task<DisplayModeState> GetCurrentStateAsync(
        string deviceId,
        string deviceName,
        CancellationToken cancellationToken = default);

    Task ApplyTemporaryAsync(DisplayModeState state, CancellationToken cancellationToken = default);

    Task PersistAsync(DisplayModeState state, CancellationToken cancellationToken = default);

    Task RestoreAsync(DisplayModeState state, CancellationToken cancellationToken = default);
}

public sealed record DisplayModeState(
    string DeviceName,
    string DeviceId,
    string DisplayName,
    int Width,
    int Height,
    int RefreshRate,
    int BitsPerPixel,
    int PositionX,
    int PositionY,
    int Orientation,
    int FixedOutput);
