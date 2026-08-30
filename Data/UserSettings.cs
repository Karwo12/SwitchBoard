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

    /// <summary>Starts the app hidden while keeping the existing tray icon available.</summary>
    public bool StartMinimizedToTray { get; set; }

    public string CloseBehavior { get; set; } = "close";

    public bool PauseAnimatedBackgroundWhenMinimized { get; set; } = true;

    // Keep existing installations' playback behavior unless the user opts into this.
    public bool PauseAnimatedBackgroundWhenInactive { get; set; }

    public bool PauseAnimatedBackgroundDuringProfileExecution { get; set; }

    public string BackgroundPerformanceMode { get; set; } = BackgroundPerformanceModes.FullQuality;

    public string GifFrameRateLimit { get; set; } = GifFrameRateLimits.Native;

    /// <summary>
    /// Selects the MP4 renderer. Automatic retains the historical WPF MediaPlayer
    /// path and uses the optional LibVLC component only after that path fails.
    /// </summary>
    public string Mp4RendererPreference { get; set; } = Mp4RendererPreferences.Automatic;

    /// <summary>Whether a complete configuration archive is created before an explicit catalog save.</summary>
    public bool AutomaticBackupEnabled { get; set; }

    /// <summary>Number of the newest automatic archives retained on disk.</summary>
    public int AutomaticBackupCount { get; set; } = 5;

    /// <summary>Creates a non-rotated managed backup during a real application exit.</summary>
    public bool CreateBackupOnExit { get; set; }

    /// <summary>Number of days to retain resolved activity history; zero keeps it indefinitely.</summary>
    public int HistoryRetentionDays { get; set; } = HistoryRetentionOptions.DefaultDays;

    /// <summary>Runs the existing non-installing GitHub Release check after startup.</summary>
    public bool CheckForUpdatesAtStartup { get; set; }

    public string? LastKnownLatestVersion { get; set; }

    public string? LastKnownReleaseUrl { get; set; }

    public DateTimeOffset? LastUpdateCheckUtc { get; set; }

    public string? LastUpdateCheckStatus { get; set; }

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
