using System.Windows.Media;

namespace SwitchBoard.Themes;

public static class ThemeColorContrast
{
    public static readonly Color DarkForeground = Color.FromRgb(0x11, 0x13, 0x18);
    public static readonly Color LightForeground = Color.FromRgb(0xF7, 0xF8, 0xFA);

    public static Color GetContrastingForeground(Color background, Color? surfaceBehind = null)
        => GetContrastingForeground([background], surfaceBehind);

    public static Color GetContrastingForeground(IEnumerable<Color> backgrounds, Color? surfaceBehind = null)
    {
        var effectiveBackgrounds = backgrounds
            .Select(background => background.A == byte.MaxValue
                ? background
                : Composite(background, surfaceBehind ?? Colors.White))
            .DefaultIfEmpty(surfaceBehind ?? Colors.White)
            .ToArray();
        var darkRatio = effectiveBackgrounds.Min(background => GetContrastRatio(background, DarkForeground));
        var lightRatio = effectiveBackgrounds.Min(background => GetContrastRatio(background, LightForeground));
        var preferred = darkRatio >= lightRatio ? DarkForeground : LightForeground;
        if (Math.Max(darkRatio, lightRatio) >= 4.5) return preferred;

        // Pure endpoints guarantee that at least one candidate reaches WCAG AA for every opaque color.
        var blackRatio = effectiveBackgrounds.Min(background => GetContrastRatio(background, Colors.Black));
        var whiteRatio = effectiveBackgrounds.Min(background => GetContrastRatio(background, Colors.White));
        return blackRatio >= whiteRatio ? Colors.Black : Colors.White;
    }

    public static bool MeetsContrast(Color foreground, IEnumerable<Color> backgrounds,
        double minimum = 4.5, Color? surfaceBehind = null) => backgrounds.All(background =>
    {
        var effective = background.A == byte.MaxValue
            ? background
            : Composite(background, surfaceBehind ?? Colors.White);
        return GetContrastRatio(effective, foreground) >= minimum;
    });

    public static double GetContrastRatio(Color first, Color second)
    {
        var lighter = Math.Max(GetRelativeLuminance(first), GetRelativeLuminance(second));
        var darker = Math.Min(GetRelativeLuminance(first), GetRelativeLuminance(second));
        return (lighter + 0.05) / (darker + 0.05);
    }

    public static double GetRelativeLuminance(Color color) =>
        0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);

    private static double Linear(byte channel)
    {
        var value = channel / 255d;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private static Color Composite(Color foreground, Color background)
    {
        var alpha = foreground.A / 255d;
        return Color.FromRgb(
            (byte)Math.Round(foreground.R * alpha + background.R * (1 - alpha)),
            (byte)Math.Round(foreground.G * alpha + background.G * (1 - alpha)),
            (byte)Math.Round(foreground.B * alpha + background.B * (1 - alpha)));
    }
}
