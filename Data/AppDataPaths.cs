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
}
