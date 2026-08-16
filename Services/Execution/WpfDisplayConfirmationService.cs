using System.Windows;
using SwitchBoard.Localization;
using SwitchBoard.Views;

namespace SwitchBoard.Services.Execution;

public sealed class WpfDisplayConfirmationService(ILocalizationService localizationService) : IDisplayConfirmationService
{
    public async Task<bool> ConfirmAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (Application.Current is null)
        {
            return false;
        }

        return await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            var window = new DisplayConfirmationWindow(timeout, localizationService)
            {
                Owner = Application.Current.MainWindow
            };
            using var registration = cancellationToken.Register(() =>
                window.Dispatcher.BeginInvoke(window.Close));
            return window.ShowDialog() == true;
        });
    }
}
