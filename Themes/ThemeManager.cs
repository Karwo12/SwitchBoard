using System.IO;
using System.Windows;
using System.Windows.Media;
using SwitchBoard.Data;

namespace SwitchBoard.Themes;

public sealed class ThemeManager(AppDataPaths paths) : IThemeManager
{
    private const string ThemeMarker = "SwitchBoard.Theme.Dictionary";
    private readonly IReadOnlyList<ThemeDefinition> _availableThemes =
    [
        Create(ThemeIds.Graphite, "Theme.GraphiteGlass", "GraphiteTheme.xaml"),
        Create(ThemeIds.Dark, "Theme.Dark", "DarkTheme.xaml"),
        Create(ThemeIds.DarkBlue, "Theme.DarkBlue", "DarkBlueTheme.xaml"),
        Create(ThemeIds.Light, "Theme.Light", "LightTheme.xaml"),
        Create(ThemeIds.MidnightTools, "Theme.MidnightTools", "MidnightToolsTheme.xaml"),
        Create(ThemeIds.NordicFrost, "Theme.NordicFrost", "NordicFrostTheme.xaml"),
        Create(ThemeIds.VioletDusk, "Theme.VioletDusk", "VioletDuskTheme.xaml"),
        Create(ThemeIds.WarmAmber, "Theme.WarmAmber", "WarmAmberTheme.xaml"),
        Create(ThemeIds.SolarDepths, "Theme.SolarDepths", "SolarDepthsTheme.xaml"),
        Create(ThemeIds.OledBlack, "Theme.OledBlack", "OledBlackTheme.xaml"),
        new(ThemeIds.Custom, "Theme.Custom", null)
    ];

    public IReadOnlyList<ThemeDefinition> AvailableThemes => _availableThemes;
    public string CurrentThemeId { get; private set; } = ThemeIds.Graphite;

    public string ApplyTheme(string? themeId, CustomThemeSettings? customTheme = null)
    {
        var theme = _availableThemes.FirstOrDefault(candidate =>
                        string.Equals(candidate.Id, themeId, StringComparison.OrdinalIgnoreCase))
                    ?? _availableThemes[0];
        ResourceDictionary dictionary;
        if (theme.Id == ThemeIds.Custom)
        {
            dictionary = CreateCustomDictionary(customTheme ?? CustomThemeSettings.CreateDefault());
        }
        else
        {
            dictionary = new ResourceDictionary { Source = theme.ResourceUri };
            CompleteBackgroundContract(dictionary);
            if (!dictionary.Contains("CardSurfaceBrush"))
                dictionary["CardSurfaceBrush"] = dictionary["ElevatedSurfaceBrush"];
            dictionary[ThemeMarker] = true;
        }

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        foreach (var existing in dictionaries.Where(IsThemeDictionary).ToList()) dictionaries.Remove(existing);
        dictionaries.Insert(0, dictionary);
        CurrentThemeId = theme.Id;
        return theme.Id;
    }

    private ResourceDictionary CreateCustomDictionary(CustomThemeSettings settings)
    {
        var background = Parse(settings.Background, "#FF11141B");
        var panel = Parse(settings.Panel, "#F01A1F29");
        var card = Parse(settings.Card, "#F0232935");
        var elevated = Parse(settings.Elevated, "#FF2B3240");
        var border = Parse(settings.Border, "#52FFFFFF");
        var primaryText = Parse(settings.PrimaryText, "#FFF5F7FA");
        var secondaryText = Parse(settings.SecondaryText, "#FFB5BDCA");
        var accent = Parse(settings.Accent, "#FF72A7FF");
        var hover = Parse(settings.Hover, "#3372A7FF");
        var selected = Parse(settings.Selected, "#5572A7FF");
        var primaryButton = Parse(settings.PrimaryButton, "#FF72A7FF");
        var iconAccent = Parse(settings.IconAccent, "#FFA9C9FF");
        var accentForeground = Contrast(primaryButton);
        var dictionary = new ResourceDictionary
        {
            [ThemeMarker] = true,
            ["BackgroundBrush"] = Brush(background),
            ["SurfaceBrush"] = Brush(panel),
            ["CardSurfaceBrush"] = Brush(card),
            ["ElevatedSurfaceBrush"] = Brush(elevated),
            ["InputBackgroundBrush"] = Brush(Blend(elevated, background, 0.35)),
            ["PopupBackgroundBrush"] = Brush(panel),
            ["BorderBrush"] = Brush(border),
            ["BorderHighlightBrush"] = Brush(WithAlpha(accent, 0.58)),
            ["TopHighlightBrush"] = Brush(WithAlpha(primaryText, 0.12)),
            ["BottomEdgeBrush"] = Brush(WithAlpha(Colors.Black, 0.3)),
            ["TextPrimaryBrush"] = Brush(primaryText),
            ["TextSecondaryBrush"] = Brush(secondaryText),
            ["TextTertiaryBrush"] = Brush(WithAlpha(secondaryText, 0.62)),
            ["AccentBrush"] = Brush(accent),
            ["AccentForegroundBrush"] = Brush(Contrast(accent)),
            ["AccentHoverBrush"] = Brush(Adjust(accent, 1.12)),
            ["PrimaryButtonBackground"] = Brush(primaryButton),
            ["PrimaryButtonForeground"] = Brush(accentForeground),
            ["PrimaryButtonHoverBackground"] = Brush(Adjust(primaryButton, 1.12)),
            ["PrimaryButtonPressedBackground"] = Brush(Adjust(primaryButton, 0.82)),
            ["PrimaryButtonDisabledBackground"] = Brush(Blend(primaryButton, background, 0.65)),
            ["PrimaryButtonDisabledForeground"] = Brush(WithAlpha(accentForeground, 0.62)),
            ["HoverBrush"] = Brush(hover),
            ["SelectedBrush"] = Brush(selected),
            ["PressedBrush"] = Brush(WithAlpha(Colors.Black, 0.22)),
            ["FocusBrush"] = Brush(WithAlpha(accent, 0.82)),
            ["DangerBrush"] = Brush(Color.FromRgb(255, 104, 120)),
            ["IconPrimary"] = Brush(primaryText),
            ["IconAccent"] = Brush(iconAccent),
            ["IconMuted"] = Brush(WithAlpha(secondaryText, 0.56)),
            ["ShadowColor"] = Color.FromArgb(190, 0, 0, 0)
        };
        CompleteBackgroundContract(dictionary, settings);
        return dictionary;
    }

    private void CompleteBackgroundContract(ResourceDictionary dictionary, CustomThemeSettings? settings = null)
    {
        var path = settings?.PreviewBackgroundPath;
        if (string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(settings?.BackgroundAssetFileName))
            path = Path.Combine(paths.CustomThemeDirectory, settings.BackgroundAssetFileName);
        if (!File.Exists(path)) path = string.Empty;
        dictionary["CustomBackgroundPath"] = path ?? string.Empty;
        dictionary["CustomBackgroundOpacity"] = settings is null ? 0d : Math.Clamp(settings.BackgroundOpacity, 0, 1);
        dictionary["CustomBackgroundStretch"] = ParseStretch(settings?.ImageFit);
        dictionary["CustomBackgroundOverlayBrush"] = Brush(Color.FromArgb(
            (byte)Math.Round(Math.Clamp(settings?.DarkOverlay ?? 0, 0, 1) * 255), 0, 0, 0));
    }

    private bool IsThemeDictionary(ResourceDictionary dictionary)
    {
        if (dictionary.Contains(ThemeMarker)) return true;
        return dictionary.Source is not null && _availableThemes.Any(theme => theme.ResourceUri is not null &&
            dictionary.Source.OriginalString.EndsWith(GetResourceFileName(theme.ResourceUri), StringComparison.OrdinalIgnoreCase));
    }

    private static Stretch ParseStretch(string? value) => value?.ToLowerInvariant() switch
    {
        "fill" => Stretch.Fill,
        "uniform" => Stretch.Uniform,
        "stretch" => Stretch.Fill,
        _ => Stretch.UniformToFill
    };

    private static Color Parse(string? value, string fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(value ?? fallback); }
        catch (FormatException) { return (Color)ColorConverter.ConvertFromString(fallback); }
    }

    private static SolidColorBrush Brush(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
    private static Color WithAlpha(Color color, double alpha) => Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), color.R, color.G, color.B);
    private static Color Contrast(Color value) => value.R * 0.299 + value.G * 0.587 + value.B * 0.114 > 150 ? Colors.Black : Colors.White;
    private static Color Blend(Color value, Color other, double otherAmount) => Color.FromArgb(255,
        (byte)(value.R * (1 - otherAmount) + other.R * otherAmount),
        (byte)(value.G * (1 - otherAmount) + other.G * otherAmount),
        (byte)(value.B * (1 - otherAmount) + other.B * otherAmount));
    private static Color Adjust(Color value, double factor) => Color.FromArgb(value.A,
        (byte)Math.Clamp(value.R * factor, 0, 255), (byte)Math.Clamp(value.G * factor, 0, 255),
        (byte)Math.Clamp(value.B * factor, 0, 255));
    private static string GetResourceFileName(Uri resourceUri) => resourceUri.OriginalString[(resourceUri.OriginalString.LastIndexOf('/') + 1)..];
    private static ThemeDefinition Create(string id, string displayName, string fileName) => new(id, displayName,
        new Uri($"/SwitchBoard;component/Themes/{fileName}", UriKind.Relative));
}
