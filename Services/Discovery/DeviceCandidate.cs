namespace SwitchBoard.Services.Discovery;

public sealed record DeviceCandidate(
    string InstanceId,
    string FriendlyName,
    string DeviceClass,
    bool IsEnabled,
    bool IsCritical)
{
    public string Status => IsEnabled ? "Enabled" : "Disabled";
}
