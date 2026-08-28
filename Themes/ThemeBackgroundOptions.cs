using System.IO;

namespace SwitchBoard.Themes;

public enum BackgroundAssetKind { None, Image, Gif, Video }

public static class BackgroundAssetKinds
{
    public static BackgroundAssetKind Detect(string? path) => Path.GetExtension(path)?.ToLowerInvariant() switch
    {
        ".gif" => BackgroundAssetKind.Gif,
        ".mp4" => BackgroundAssetKind.Video,
        ".png" or ".jpg" or ".jpeg" or ".bmp" => BackgroundAssetKind.Image,
        _ => BackgroundAssetKind.None
    };
}

public static class BackgroundImageFits
{
    public const string Fill = "uniformToFill";
    public const string Fit = "uniform";
    public const string Stretch = "stretch";
    public const string Center = "center";

    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        // "fill" was exposed by earlier editor versions and mapped to Stretch.Fill.
        // Keep that visual behavior when reading an existing theme.
        "fill" or Stretch => Stretch,
        Fit => Fit,
        Center => Center,
        _ => Fill
    };
}

public static class GifAnimationDirections
{
    public const string Normal = "normal";
    public const string Reverse = "reverse";
    public const string PingPong = "pingPong";

    public static string Normalize(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        Reverse => Reverse,
        "pingpong" or "ping-pong" => PingPong,
        _ => Normal
    };
}

public static class GifAnimationSpeeds
{
    public static readonly IReadOnlyList<double> Supported = [0.5d, 0.75d, 1d, 1.25d, 1.5d, 2d];

    public static double Normalize(double value)
    {
        if (!double.IsFinite(value)) return 1d;
        var closest = 1d;
        var distance = double.MaxValue;
        foreach (var supported in Supported)
        {
            var candidateDistance = Math.Abs(supported - value);
            if (candidateDistance >= distance) continue;
            closest = supported;
            distance = candidateDistance;
        }
        return closest;
    }
}
