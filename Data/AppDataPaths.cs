using System.IO;

namespace SwitchBoard.Data;

public sealed class AppDataPaths
{
    public AppDataPaths(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SwitchBoard");
    }

    public string RootDirectory { get; }

    public string CatalogFilePath => Path.Combine(RootDirectory, "catalog.json");

    public string CatalogBackupFilePath => Path.Combine(RootDirectory, "catalog.json.bak");

    public string SettingsFilePath => Path.Combine(RootDirectory, "settings.json");

    public string SettingsBackupFilePath => Path.Combine(RootDirectory, "settings.json.bak");

    public string SessionsDirectory => Path.Combine(RootDirectory, "sessions");

    public string CustomThemeDirectory => Path.Combine(RootDirectory, "themes", "custom");

    /// <summary>
    /// Application-managed configuration archives. This directory is intentionally
    /// separate from the user-selected export destination and from transient files.
    /// </summary>
    public string AutoBackupsDirectory => Path.Combine(RootDirectory, "backups", "automatic");

    public string LogsDirectory => Path.Combine(RootDirectory, "logs");

    /// <summary>
    /// Components deliberately installed by the user. They are outside the
    /// application publish directory so the portable base package stays small.
    /// </summary>
    public string OptionalComponentsDirectory => Path.Combine(RootDirectory, "components");

    public string LibVlcComponentDirectory => Path.Combine(OptionalComponentsDirectory, "libvlc");

    public string LibVlcRemovalMarkerPath => Path.Combine(OptionalComponentsDirectory, "libvlc.remove-on-next-startup");
}
