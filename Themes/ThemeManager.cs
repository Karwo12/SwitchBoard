using System.Windows;

namespace SwitchBoard.Themes;

public sealed class ThemeManager : IThemeManager
{
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
        Create(ThemeIds.OledBlack, "Theme.OledBlack", "OledBlackTheme.xaml")
    ];

    public IReadOnlyList<ThemeDefinition> AvailableThemes => _availableThemes;

    public string CurrentThemeId { get; private set; } = ThemeIds.Graphite;

    public string ApplyTheme(string? themeId)
    {
        var theme = _availableThemes.FirstOrDefault(candidate =>
                        string.Equals(candidate.Id, themeId, StringComparison.OrdinalIgnoreCase))
                    ?? _availableThemes[0];

        var dictionaries = Application.Current.Resources.MergedDictionaries;
        var themeDictionaries = dictionaries
            .Where(dictionary => dictionary.Source is not null && IsThemeResource(dictionary.Source))
            .ToList();

        if (themeDictionaries.Count != 1 ||
            !themeDictionaries[0].Source.OriginalString.EndsWith(
                GetResourceFileName(theme.ResourceUri),
                StringComparison.OrdinalIgnoreCase))
        {
            foreach (var dictionary in themeDictionaries)
            {
                dictionaries.Remove(dictionary);
            }

            dictionaries.Insert(0, new ResourceDictionary { Source = theme.ResourceUri });
        }

        CurrentThemeId = theme.Id;
        return theme.Id;
    }

    private bool IsThemeResource(Uri resourceUri) => _availableThemes.Any(theme =>
        resourceUri.OriginalString.EndsWith(
            GetResourceFileName(theme.ResourceUri),
            StringComparison.OrdinalIgnoreCase));

    private static string GetResourceFileName(Uri resourceUri) =>
        resourceUri.OriginalString[(resourceUri.OriginalString.LastIndexOf('/') + 1)..];

    private static ThemeDefinition Create(string id, string displayName, string fileName) =>
        new(
            id,
            displayName,
            new Uri($"/SwitchBoard;component/Themes/{fileName}", UriKind.Relative));
}
