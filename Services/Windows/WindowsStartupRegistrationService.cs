using System.IO;
using System.Security;
using Microsoft.Win32;

namespace SwitchBoard.Services.Windows;

public sealed class WindowsStartupRegistrationService : IStartupRegistrationService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "SwitchBoard";

    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                return key?.GetValue(ValueName) is string value && !string.IsNullOrWhiteSpace(value);
            }
            catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    public bool TrySetEnabled(bool enabled, out string? error)
    {
        error = null;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                error = "Windows did not allow access to the per-user startup registry key.";
                return false;
            }

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return true;
            }

            var executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
            {
                error = "The SwitchBoard executable path could not be resolved.";
                return false;
            }

            key.SetValue(ValueName, $"\"{executable}\"");
            return true;
        }
        catch (Exception exception) when (exception is SecurityException or UnauthorizedAccessException or IOException)
        {
            error = exception.Message;
            return false;
        }
    }
}
