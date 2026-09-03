using SwitchBoard.Services.Monitoring;

namespace SwitchBoard.ViewModels.Panels;

/// <summary>
/// Separates the Windows process ancestry from the application groups shown in the UI.
/// A shell process can launch any application, but that does not make the application a
/// child of the shell from the user's point of view.
/// </summary>
internal static class PerformanceProcessGrouping
{
    private static readonly HashSet<string> ApplicationHelpers = new(StringComparer.OrdinalIgnoreCase)
    {
        "crashpad_handler", "crashpad_handler64", "steamwebhelper", "steamservice",
        "cefsubprocess", "electron", "gpu-process", "msedgewebview2", "webviewhost"
    };

    private static readonly HashSet<string> TechnicalLaunchers = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "startmenuexperiencehost", "shellexperiencehost", "applicationframehost",
        "runtimebroker", "taskhostw", "cmd", "powershell", "pwsh", "wscript", "cscript"
    };

    public static IReadOnlyDictionary<int, List<PerformanceProcessSnapshot>> BuildLogicalChildren(
        IEnumerable<PerformanceProcessSnapshot> processes)
    {
        var items = processes.ToList();
        var byId = items.ToDictionary(item => item.ProcessId);
        var children = new Dictionary<int, List<PerformanceProcessSnapshot>>();

        foreach (var child in items)
        {
            if (child.ParentProcessId is not { } parentId || parentId == child.ProcessId ||
                !byId.TryGetValue(parentId, out var parent) || !IsLogicalChild(parent, child))
                continue;

            if (!children.TryGetValue(parentId, out var list))
                children[parentId] = list = [];
            list.Add(child);
        }

        return children;
    }

    public static IReadOnlySet<int> GetLogicalChildProcessIds(
        IReadOnlyDictionary<int, List<PerformanceProcessSnapshot>> children) =>
        children.Values.SelectMany(items => items).Select(item => item.ProcessId).ToHashSet();

    public static bool IsLogicalChild(PerformanceProcessSnapshot parent,
        PerformanceProcessSnapshot child)
    {
        var parentName = PerformanceMonitoringService.NormalizeProcessName(parent.ProcessName);
        var childName = PerformanceMonitoringService.NormalizeProcessName(child.ProcessName);
        if (parentName.Length == 0 || childName.Length == 0) return false;

        // Chromium, Electron and the majority of multi-process desktop applications keep
        // their own executable name for browser, renderer and GPU processes.
        if (string.Equals(parentName, childName, StringComparison.OrdinalIgnoreCase)) return true;

        // A small set of dedicated helper executables deliberately has another image name.
        // They belong to their immediate application parent, but are never attached to shell
        // or scripting launchers that happen to be their technical PPID ancestor.
        return !TechnicalLaunchers.Contains(parentName) && ApplicationHelpers.Contains(childName);
    }
}
