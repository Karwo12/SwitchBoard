using System.Windows;
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

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel && !viewModel.ConfirmCloseDuringCriticalOperation())
            e.Cancel = true;
    }
}
