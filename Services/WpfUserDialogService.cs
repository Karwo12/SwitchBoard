using System.Windows;

namespace SwitchBoard.Services;

public sealed class WpfUserDialogService : IUserDialogService
{
    public bool Confirm(string title, string message) =>
        MessageBox.Show(
            message,
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
}
