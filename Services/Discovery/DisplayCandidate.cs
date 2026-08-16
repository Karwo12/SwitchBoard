namespace SwitchBoard.Services.Discovery;

public sealed record DisplayCandidate(
    string DeviceName,
    string DeviceId,
    string DisplayName,
    int MonitorNumber,
    int CurrentWidth,
    int CurrentHeight,
    int CurrentRefreshRate,
    bool IsPrimary,
    IReadOnlyList<DisplayModeCandidate> Modes)
{
    public string CurrentModeText => $"{CurrentWidth} × {CurrentHeight} @ {CurrentRefreshRate} Hz";
}
