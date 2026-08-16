using System.Windows.Media;

namespace SwitchBoard.Services.Discovery;

public sealed record ProcessCandidate(
    int ProcessId,
    string ProcessName,
    string ExecutableName,
    string? ExecutablePath,
    string? WindowTitle,
    string DisplayName,
    string SuggestedName,
    ImageSource? Icon)
{
    public override string ToString() => DisplayName;
}
