using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using SwitchBoard.ViewModels;

namespace SwitchBoard.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        Closing += OnClosing;
    }

    private void ThemeMenuButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel && !viewModel.ConfirmCloseDuringCriticalOperation())
            e.Cancel = true;
    }
}
