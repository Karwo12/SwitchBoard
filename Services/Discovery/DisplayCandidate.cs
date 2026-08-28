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
    public string MonitorDevicePath { get; init; } = string.Empty;
    public string SourceName { get; init; } = string.Empty;
    public string DeviceDescription { get; init; } = string.Empty;
    public string EdidProductName { get; init; } = string.Empty;
    public string DisplayNameSource { get; init; } = string.Empty;
    public string CurrentModeText => $"{CurrentWidth} × {CurrentHeight} @ {CurrentRefreshRate} Hz";
}
