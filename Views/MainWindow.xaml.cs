using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.ComponentModel;
using SwitchBoard.ViewModels;

namespace SwitchBoard.Views;

public partial class MainWindow : Window
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        var workArea = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, workArea.Width);
        MinHeight = Math.Min(MinHeight, workArea.Height);
        MaxWidth = workArea.Width;
        MaxHeight = workArea.Height;
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

    private void ActivityScrollViewer_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer viewer || e.ExtentHeightChange <= 0) return;
        var oldScrollableHeight = viewer.ExtentHeight - e.ExtentHeightChange - viewer.ViewportHeight;
        if (viewer.VerticalOffset >= Math.Max(0, oldScrollableHeight - 18)) viewer.ScrollToEnd();
    }

    private void ActivityLayoutSplitter_OnDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;
        var total = MainContentGrid.ActualHeight - ActivityLayoutSplitter.ActualHeight;
        if (total > 0) viewModel.UpdateActivityPanelRatio(ActivityRow.ActualHeight / total);
    }

    private void ActivityLayoutSplitter_OnMouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel) viewModel.ResetActivityPanelRatio();
    }
}
