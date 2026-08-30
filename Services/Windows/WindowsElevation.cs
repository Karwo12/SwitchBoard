using System.Security.Principal;

namespace SwitchBoard.Services.Windows;

/// <summary>Single source of truth for the elevation state of this process.</summary>
public static class WindowsElevation
{
    public static bool IsProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }
}
