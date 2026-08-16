using System.Text.Json.Serialization;
using SwitchBoard.Themes;

namespace SwitchBoard.Data;

public sealed class UserSettings
{
    public int SchemaVersion { get; set; } = SettingsSchema.CurrentVersion;

    public string ThemeId { get; set; } = ThemeIds.Graphite;

    public string? LanguageId { get; set; }

    public List<CustomThemeDefinition> CustomThemes { get; set; } = [];

    // Schema 4 compatibility. Migrated once at startup and omitted from subsequent JSON.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CustomThemeSettings? CustomTheme { get; set; }
}
