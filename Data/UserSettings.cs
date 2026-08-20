using System.Text.Json.Serialization;
using SwitchBoard.Themes;

namespace SwitchBoard.Data;

public sealed class UserSettings
{
    public int SchemaVersion { get; set; } = SettingsSchema.CurrentVersion;

    public string ThemeId { get; set; } = ThemeIds.Graphite;

    public string? LanguageId { get; set; }

    public double ActivityPanelHeightRatio { get; set; } = 0.5;

    public bool ShowCurrentActionState { get; set; } = true;

    public int WindowWidth { get; set; } = 1340;

    public int WindowHeight { get; set; } = 820;

    public double? WindowX { get; set; }
    public double? WindowY { get; set; }
    public string WindowState { get; set; } = "Normal";

    public Guid? LastSelectedCategoryId { get; set; }
    public Guid? LastSelectedProfileId { get; set; }
    public int LastActivityTabIndex { get; set; }
    public bool IsActivityExpanded { get; set; }

    public List<CustomThemeDefinition> CustomThemes { get; set; } = [];

    // Schema 4 compatibility. Migrated once at startup and omitted from subsequent JSON.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CustomThemeSettings? CustomTheme { get; set; }
}
