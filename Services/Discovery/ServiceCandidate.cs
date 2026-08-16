namespace SwitchBoard.Services.Discovery;

public sealed record ServiceCandidate(
    string DisplayName,
    string ServiceName,
    string Status,
    string? StartupType,
    string? Description)
{
    public override string ToString() => DisplayName;
}
