namespace SwitchBoard.Services.Discovery;

public sealed record DisplayModeCandidate(int Width, int Height, int RefreshRate, int BitsPerPixel)
{
    public string ResolutionText => $"{Width} × {Height}";

    public string RefreshRateText => $"{RefreshRate} Hz";
}
