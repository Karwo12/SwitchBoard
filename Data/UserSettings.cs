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

    public bool LaunchAtStartup { get; set; }

    public string CloseBehavior { get; set; } = "close";

    /// <summary>Whether a complete configuration archive is created before an explicit catalog save.</summary>
    public bool AutomaticBackupEnabled { get; set; }

    /// <summary>Number of the newest automatic archives retained on disk.</summary>
    public int AutomaticBackupCount { get; set; } = 5;

    public bool RememberLastView { get; set; }

    public string LastMainView { get; set; } = "Home";

    public bool WarnBeforeClosingWithUnsavedChanges { get; set; } = true;

    public string InterfaceDensity { get; set; } = "standard";

    public bool ShowCardDetails { get; set; } = true;

    /// <summary>Resize the main window to the native dimensions of a theme background asset.</summary>
    public bool AutoFitWindowToBackground { get; set; }

    public int WindowWidth { get; set; } = 1340;

    public int WindowHeight { get; set; } = 820;

    public double? WindowX { get; set; }
    public double? WindowY { get; set; }
    public string WindowState { get; set; } = "Normal";

    public Guid? LastSelectedCategoryId { get; set; }
    public Guid? LastSelectedProfileId { get; set; }
    public int LastActivityTabIndex { get; set; } = 2;
    public bool IsActivityExpanded { get; set; }

    public List<CustomThemeDefinition> CustomThemes { get; set; } = [];

    /// <summary>
    /// Stable IDs in the user-defined order shown by the Themes settings page.
    /// Missing or unknown IDs are ignored and newly discovered themes are appended
    /// in the historical default order.
    /// </summary>
    public List<string> ThemeOrder { get; set; } = [];

    // Schema 4 compatibility. Migrated once at startup and omitted from subsequent JSON.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CustomThemeSettings? CustomTheme { get; set; }
}
