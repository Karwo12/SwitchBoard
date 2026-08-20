using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
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
        RestoreWindowGeometry(viewModel, workArea);
        Closing += OnClosing;
        SizeChanged += (_, _) => viewModel.CaptureWindowGeometry(this);
        LocationChanged += (_, _) => viewModel.CaptureWindowGeometry(this);
        StateChanged += (_, _) => viewModel.CaptureWindowGeometry(this);
    }

    private void ThemeMenuButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void MainWindow_OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (System.Windows.Input.Keyboard.FocusedElement is TextBox or PasswordBox) return;
        if (DataContext is not MainWindowViewModel viewModel ||
            (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) == 0) return;
        var shift = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;
        switch (e.Key)
        {
            case System.Windows.Input.Key.Z when shift && viewModel.RedoCommand.CanExecute("keyboard"):
                viewModel.RedoCommand.Execute("keyboard"); e.Handled = true; break;
            case System.Windows.Input.Key.Y when !shift && viewModel.RedoCommand.CanExecute("keyboard"):
                viewModel.RedoCommand.Execute("keyboard"); e.Handled = true; break;
            case System.Windows.Input.Key.Z when !shift && viewModel.UndoCommand.CanExecute("keyboard"):
                viewModel.UndoCommand.Execute("keyboard"); e.Handled = true; break;
            case System.Windows.Input.Key.S when !shift && viewModel.SaveCommand.CanExecute(null):
                viewModel.SaveCommand.Execute(null); e.Handled = true; break;
        }
    }

    private bool _closeApproved;
    private bool _closeInProgress;

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_closeApproved || DataContext is not MainWindowViewModel viewModel) return;
        e.Cancel = true;
        if (_closeInProgress) return;
        _closeInProgress = true;
        try
        {
            if (!viewModel.ConfirmCloseDuringCriticalOperation()) return;
            if (viewModel.HasUnsavedChanges)
            {
                var dialog = new UnsavedChangesWindow(
                    viewModel.GetLocalizedText("Dialog.UnsavedChangesTitle"),
                    viewModel.GetLocalizedText("Dialog.UnsavedChangesMessage"),
                    viewModel.GetLocalizedText("Dialog.SaveAndClose"),
                    viewModel.GetLocalizedText("Dialog.CloseWithoutSaving"),
                    viewModel.GetLocalizedText("Common.Cancel")) { Owner = this };
                dialog.ShowDialog();
                if (dialog.Choice == UnsavedChangesChoice.Cancel) return;
                if (dialog.Choice == UnsavedChangesChoice.Save && !await viewModel.SaveForShutdownAsync()) return;
            }
            viewModel.CaptureWindowGeometry(this);
            await viewModel.FlushPendingSettingsSaveAsync();
            _closeApproved = true;
            Close();
        }
        finally { _closeInProgress = false; }
    }

    private void RestoreWindowGeometry(MainWindowViewModel viewModel, Rect workArea)
    {
        Width = Math.Clamp(viewModel.WindowWidth, MinWidth, MaxWidth);
        Height = Math.Clamp(viewModel.WindowHeight, MinHeight, MaxHeight);
        if (viewModel.WindowX is not double x || viewModel.WindowY is not double y) return;
        var savedBounds = new Rect(x, y, Width, Height);
        var targetArea = GetWorkingArea(savedBounds) ?? workArea;
        var visible = GetWorkingArea(savedBounds) is not null;
        if (!visible)
        {
            Left = targetArea.Left + Math.Max(0, (targetArea.Width - Width) / 2);
            Top = targetArea.Top + Math.Max(0, (targetArea.Height - Height) / 2);
        }
        else
        {
            Left = Math.Clamp(x, targetArea.Left, targetArea.Right - Width);
            Top = Math.Clamp(y, targetArea.Top, targetArea.Bottom - Height);
        }
        WindowStartupLocation = WindowStartupLocation.Manual;
        if (string.Equals(viewModel.SavedWindowState, "Maximized", StringComparison.OrdinalIgnoreCase))
            WindowState = WindowState.Maximized;
    }

    private static Rect? GetWorkingArea(Rect bounds)
    {
        var nativeRect = new NativeRect
        {
            Left = (int)Math.Round(bounds.Left), Top = (int)Math.Round(bounds.Top),
            Right = (int)Math.Round(bounds.Right), Bottom = (int)Math.Round(bounds.Bottom)
        };
        var monitor = MonitorFromRect(ref nativeRect, 0);
        if (monitor == IntPtr.Zero) return null;
        var info = new MonitorInfo { CbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return null;
        return new Rect(info.Work.Left, info.Work.Top, info.Work.Right - info.Work.Left, info.Work.Bottom - info.Work.Top);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref NativeRect rect, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfo
    {
        public int CbSize;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    private void ActivityScrollViewer_OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer viewer || e.ExtentHeightChange <= 0) return;
        var oldScrollableHeight = viewer.ExtentHeight - e.ExtentHeightChange - viewer.ViewportHeight;
        if (viewer.VerticalOffset >= Math.Max(0, oldScrollableHeight - 18)) viewer.ScrollToEnd();
    }

    private void ActivityEntry_OnMouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (sender is FrameworkElement { DataContext: ActivityEntryViewModel display } &&
            DataContext is MainWindowViewModel displayViewModel && display.IsNavigable &&
            displayViewModel.NavigateToProfileAction(display.ProfileId, display.ActionId))
        {
            if (display.ActionId is Guid displayActionId) ActionEditor.BringActionIntoView(displayActionId);
            return;
        }
        if (sender is not FrameworkElement { DataContext: Services.Activity.ActivityEntry entry } ||
            DataContext is not MainWindowViewModel viewModel) return;
        if (viewModel.NavigateToProfileAction(entry.ProfileId, entry.ActionId) && entry.ActionId is Guid actionId)
            ActionEditor.BringActionIntoView(actionId);
    }

    private void ActivityTarget_OnMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ActivityEntryViewModel entry } ||
            DataContext is not MainWindowViewModel viewModel || !entry.IsNavigable) return;
        if (viewModel.NavigateToProfileAction(entry.ProfileId, entry.ActionId) && entry.ActionId is Guid actionId)
            ActionEditor.BringActionIntoView(actionId);
        e.Handled = true;
    }

    private void SystemChangeEntry_OnMouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (sender is not FrameworkElement { DataContext: ViewModels.SystemChangeItemViewModel entry } ||
            DataContext is not MainWindowViewModel viewModel) return;
        if (viewModel.NavigateToProfileAction(entry.ProfileId, entry.ActionId))
            ActionEditor.BringActionIntoView(entry.ActionId);
    }

    private void SystemChangeTarget_OnMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SystemChangeItemViewModel entry } ||
            DataContext is not MainWindowViewModel viewModel || !entry.IsNavigable) return;
        if (viewModel.NavigateToProfileAction(entry.ProfileId, entry.ActionId))
            ActionEditor.BringActionIntoView(entry.ActionId);
        e.Handled = true;
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
