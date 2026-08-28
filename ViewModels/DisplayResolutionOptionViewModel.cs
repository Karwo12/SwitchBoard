using SwitchBoard.Services.Discovery;

namespace SwitchBoard.ViewModels;

public sealed class DisplayResolutionOptionViewModel
{
    public DisplayResolutionOptionViewModel(int width, int height, IReadOnlyList<DisplayModeCandidate> modes)
    {
        Width = width;
        Height = height;
        Modes = modes;
    }

    public int Width { get; }
    public int Height { get; }
    public string DisplayName => $"{Width} × {Height}";
    public IReadOnlyList<DisplayModeCandidate> Modes { get; }
}
