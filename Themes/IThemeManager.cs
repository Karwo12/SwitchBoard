namespace SwitchBoard.Themes;

public interface IThemeManager
{
    IReadOnlyList<ThemeDefinition> AvailableThemes { get; }

    string CurrentThemeId { get; }

    string ApplyTheme(string? themeId, CustomThemeSettings? customTheme = null);
}
