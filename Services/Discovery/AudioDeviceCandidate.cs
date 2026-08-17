namespace SwitchBoard.Services.Discovery;

public sealed record AudioDeviceCandidate(
    string Id,
    string FriendlyName,
    bool IsInput,
    bool IsDefaultMultimedia,
    bool IsDefaultCommunications)
{
    public string Direction => IsInput ? "Input" : "Output";
}
