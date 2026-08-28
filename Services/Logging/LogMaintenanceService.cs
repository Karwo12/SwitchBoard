using System.IO;
using SwitchBoard.Data;

namespace SwitchBoard.Services.Logging;

public sealed class LogMaintenanceService(AppDataPaths paths)
{
    public void Clear()
    {
        Directory.CreateDirectory(paths.LogsDirectory);
        var files = Directory.EnumerateFiles(paths.LogsDirectory, "switchboard.log*").ToList();
        foreach (var file in files)
        {
            if (string.Equals(Path.GetFileName(file), "switchboard.log", StringComparison.OrdinalIgnoreCase))
                File.WriteAllText(file, string.Empty);
            else
                File.Delete(file);
        }
    }
}
