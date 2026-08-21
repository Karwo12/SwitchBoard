namespace SwitchBoard.RuntimeTests.TestInfrastructure;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class EnvironmentFactAttribute : FactAttribute
{
    public EnvironmentFactAttribute(string requirement)
    {
        Skip = EnvironmentRequirements.GetSkipReason(requirement);
    }
}

internal static class EnvironmentRequirements
{
    public static string? GetSkipReason(string requirement)
    {
        if (!OperatingSystem.IsWindows()) return "The integration test requires Windows.";
        return requirement switch
        {
            "Administrator" when !IsAdministrator() => "The integration test requires Administrator privileges.",
            "AudioEndpoint" when !HasAudioEndpoint() => "The test environment has no compatible Core Audio endpoint.",
            "Display" when !HasDisplay() => "The test environment exposes no display.",
            "AlternateDisplayMode" when !HasAlternateDisplayMode() => "The test environment exposes no alternate display mode.",
            "Notepad" when !HasNotepad() => "Notepad is not available in the current test environment.",
            "Edge" when !HasEdge() => "Microsoft Edge is not available in the current test environment.",
            "PowerQoS" when !HasPowerQoS() => "The Power QoS API is unavailable in this Windows environment.",
            "RunningService" when !HasRunningService() => "The test environment exposes no running Windows service.",
            _ => null
        };
    }

    private static bool IsAdministrator() => new System.Security.Principal.WindowsPrincipal(
        System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(
            System.Security.Principal.WindowsBuiltInRole.Administrator);

    private static bool HasAudioEndpoint()
    {
        try { return new WindowsAudioManager().GetDevicesAsync().GetAwaiter().GetResult().Count > 0; }
        catch (Exception exception) when (exception is PlatformNotSupportedException or Win32Exception or
                                          System.Runtime.InteropServices.COMException) { return false; }
    }

    private static bool HasDisplay()
    {
        try { return new WindowsDisplayManager().GetDisplaysAsync().GetAwaiter().GetResult().Count > 0; }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException) { return false; }
    }

    private static bool HasAlternateDisplayMode()
    {
        try
        {
            return new WindowsDisplayManager().GetDisplaysAsync().GetAwaiter().GetResult().Any(display =>
                display.Modes.Any(mode => mode.Width != display.CurrentWidth ||
                                         mode.Height != display.CurrentHeight ||
                                         mode.RefreshRate != display.CurrentRefreshRate));
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException) { return false; }
    }

    private static bool HasNotepad()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return File.Exists(Path.Combine(windows, "notepad.exe"));
    }

    private static bool HasEdge()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
        };
        return roots.Where(root => !string.IsNullOrWhiteSpace(root)).Select(root =>
            Path.Combine(root, "Microsoft", "Edge", "Application", "msedge.exe"))
            .Any(File.Exists);
    }

    private static bool HasPowerQoS()
    {
        try
        {
            _ = new ProcessSettingsService().Capture(Process.GetCurrentProcess(), new JsonObject
            { [ActionParameterNames.ProcessPerformanceMode] = ProcessPerformanceModeIds.HighPerformance });
            return true;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or
                                          NotSupportedException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HasRunningService()
    {
        try
        {
            return new WindowsServiceManager().GetServicesAsync().GetAwaiter().GetResult()
                .Any(service => string.Equals(service.Status, "Running", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or
                                          UnauthorizedAccessException) { return false; }
    }
}
