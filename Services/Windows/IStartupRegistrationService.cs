namespace SwitchBoard.Services.Windows;

public interface IStartupRegistrationService
{
    bool IsEnabled { get; }

    bool TrySetEnabled(bool enabled, out string? error);
}
