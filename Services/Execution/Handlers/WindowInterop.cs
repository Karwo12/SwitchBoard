using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SwitchBoard.Models.Actions;

namespace SwitchBoard.Services.Execution.Handlers;

internal static class WindowInterop
{
    public sealed record WindowInfo(nint Handle, int ProcessId, string Title);

    public static IReadOnlyList<WindowInfo> FindWindows(string processName, string? executablePath,
        string matchMode, string title)
    {
        var processIds = ProcessTargetResolver.Find(processName, executablePath).Select(process =>
        {
            try { return process.Id; } finally { process.Dispose(); }
        }).ToHashSet();
        if (processIds.Count == 0) return [];
        var result = new List<WindowInfo>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle)) return true;
            GetWindowThreadProcessId(handle, out var processId);
            if (!processIds.Contains(unchecked((int)processId))) return true;
            var length = GetWindowTextLength(handle);
            var builder = new StringBuilder(Math.Max(1, length + 1));
            GetWindowText(handle, builder, builder.Capacity);
            var actual = builder.ToString();
            var matches = matchMode switch
            {
                WindowMatchModeIds.Contains => actual.Contains(title, StringComparison.CurrentCultureIgnoreCase),
                WindowMatchModeIds.Exact => string.Equals(actual, title, StringComparison.CurrentCultureIgnoreCase),
                _ => true
            };
            if (matches) result.Add(new WindowInfo(handle, (int)processId, actual));
            return true;
        }, 0);
        return result;
    }

    public static void ApplyBehavior(nint handle, string behavior)
    {
        var command = behavior switch
        {
            WindowBehaviorIds.Hide => 0,
            WindowBehaviorIds.Minimize => 6,
            WindowBehaviorIds.Maximize => 3,
            WindowBehaviorIds.Restore => 9,
            _ => -1
        };
        if (command >= 0 && !ShowWindowAsync(handle, command))
            throw new InvalidOperationException("Windows rejected the requested window operation.");
    }

    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(nint handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(nint handle, StringBuilder text, int maximumCount);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(nint handle, int command);
}
