namespace SwitchBoard.Data;

public static class BackgroundPerformanceModes
{
    public const string FullQuality = "full";
    public const string Economy = "economy";

    public static string Normalize(string? value) => string.Equals(value, Economy, StringComparison.OrdinalIgnoreCase)
        ? Economy
        : FullQuality;
}

public static class GifFrameRateLimits
{
    public const string Native = "native";
    public const string FramesPerSecond60 = "60";
    public const string FramesPerSecond30 = "30";

    public static string Normalize(string? value) => value?.Trim() switch
    {
        FramesPerSecond60 => FramesPerSecond60,
        FramesPerSecond30 => FramesPerSecond30,
        _ => Native
    };

    public static TimeSpan Apply(string? limit, TimeSpan nativeDelay)
    {
        var minimumDelay = Normalize(limit) switch
        {
            FramesPerSecond60 => TimeSpan.FromSeconds(1d / 60d),
            FramesPerSecond30 => TimeSpan.FromSeconds(1d / 30d),
            _ => TimeSpan.Zero
        };
        return nativeDelay < minimumDelay ? minimumDelay : nativeDelay;
    }
}

public static class HistoryRetentionOptions
{
    public const int ThirtyDays = 30;
    public const int NinetyDays = 90;
    public const int ThreeHundredSixtyFiveDays = 365;
    public const int Unlimited = 0;
    public const int DefaultDays = NinetyDays;

    public static int Normalize(int days) => days is ThirtyDays or NinetyDays or ThreeHundredSixtyFiveDays or Unlimited
        ? days
        : DefaultDays;
}
