using SwitchBoard.Themes;

namespace SwitchBoard.Data;

public sealed class UserSettings
{
    public int SchemaVersion { get; set; } = SettingsSchema.CurrentVersion;

    public string ThemeId { get; set; } = ThemeIds.Graphite;

    public string? LanguageId { get; set; }
}
