using System.Windows;
using SwitchBoard.ViewModels;

namespace SwitchBoard.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
