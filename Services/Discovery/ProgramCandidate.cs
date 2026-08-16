using System.Windows.Media;

namespace SwitchBoard.Services.Discovery;

public sealed record ProgramCandidate(
    string DisplayName,
    string ExecutableName,
    string TargetPath,
    string WorkingDirectory,
    bool IsRunning,
    ImageSource? Icon)
{
    public override string ToString() => DisplayName;
}
