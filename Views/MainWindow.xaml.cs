using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using System.Windows.Interop;
using SwitchBoard.ViewModels;
using SwitchBoard.Services.Tray;

namespace SwitchBoard.Views;

public partial class MainWindow : Window
{
    private enum SettingsCategory
    {
        Profile,
        General,
        Interface,
        Themes,
        Data,
        Diagnostics
    }

    private SettingsCategory _selectedSettingsCategory = SettingsCategory.Profile;
    private readonly MainWindowViewModel _viewModel;
    private SystemTrayService? _trayService;
    private bool _exitRequestedFromTray;

    public MainWindow(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        var workArea = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, workArea.Width);
        MinHeight = Math.Min(MinHeight, workArea.Height);
        MaxWidth = workArea.Width;
        MaxHeight = workArea.Height;
        DataContext = viewModel;
        SetSettingsCategory(_selectedSettingsCategory);
        SetMainView(viewModel.InitialMainView);
        RestoreWindowGeometry(viewModel, workArea);
        Closing += OnClosing;
        SizeChanged += (_, _) => viewModel.CaptureWindowGeometry(this);
        LocationChanged += (_, _) => viewModel.CaptureWindowGeometry(this);
        StateChanged += (_, _) => viewModel.CaptureWindowGeometry(this);
        Closed += OnClosed;
        _trayService = new SystemTrayService(OpenFromTray, ExitFromTray, viewModel.GetTrayProfiles,
            () => viewModel.HasPendingRestore,
            id => RunOnUiAsync(() => viewModel.RunProfileFromTrayAsync(id)),
            () => RunOnUiAsync(viewModel.RestoreProfileFromTrayAsync), viewModel.GetLocalizedText);
    }

    private void HomeNavigationButton_OnClick(object sender, RoutedEventArgs e) => SetMainView(MainViewMode.Home);

    private void ActivityNavigationButton_OnClick(object sender, RoutedEventArgs e) => SetMainView(MainViewMode.Activity);

    private void SettingsNavigationButton_OnClick(object sender, RoutedEventArgs e) => SetMainView(MainViewMode.Settings);

    private void SettingsProfileCategoryButton_OnClick(object sender, RoutedEventArgs e) =>
        SetSettingsCategory(SettingsCategory.Profile);

    private void SettingsGeneralCategoryButton_OnClick(object sender, RoutedEventArgs e) =>
        SetSettingsCategory(SettingsCategory.General);

    private void SettingsInterfaceCategoryButton_OnClick(object sender, RoutedEventArgs e) =>
        SetSettingsCategory(SettingsCategory.Interface);

    private void SettingsThemesCategoryButton_OnClick(object sender, RoutedEventArgs e) =>
        SetSettingsCategory(SettingsCategory.Themes);

    private void SettingsDataCategoryButton_OnClick(object sender, RoutedEventArgs e) =>
        SetSettingsCategory(SettingsCategory.Data);

    private void SettingsDiagnosticsCategoryButton_OnClick(object sender, RoutedEventArgs e) =>
        SetSettingsCategory(SettingsCategory.Diagnostics);

    private void ProfileList_OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.AddedItems.Count != 1 || e.AddedItems[0] is not ProfileItemViewModel profile ||
            DataContext is not MainWindowViewModel viewModel || ReferenceEquals(viewModel.SelectedProfile, profile))
            return;

        viewModel.SelectedProfile = profile;
    }

    private void SetSettingsCategory(SettingsCategory category)
    {
        _selectedSettingsCategory = category;
        ProfileSettingsPanel.Visibility = category == SettingsCategory.Profile ? Visibility.Visible : Visibility.Collapsed;
        GeneralSettingsPanel.Visibility = category == SettingsCategory.General ? Visibility.Visible : Visibility.Collapsed;
        InterfaceSettingsPanel.Visibility = category == SettingsCategory.Interface ? Visibility.Visible : Visibility.Collapsed;
        ThemeSettingsPanel.Visibility = category == SettingsCategory.Themes ? Visibility.Visible : Visibility.Collapsed;
        DataSettingsPanel.Visibility = category == SettingsCategory.Data ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsSettingsPanel.Visibility = category == SettingsCategory.Diagnostics ? Visibility.Visible : Visibility.Collapsed;
        SettingsProfileCategoryButton.IsChecked = category == SettingsCategory.Profile;
        SettingsGeneralCategoryButton.IsChecked = category == SettingsCategory.General;
        SettingsInterfaceCategoryButton.IsChecked = category == SettingsCategory.Interface;
        SettingsThemesCategoryButton.IsChecked = category == SettingsCategory.Themes;
        SettingsDataCategoryButton.IsChecked = category == SettingsCategory.Data;
        SettingsDiagnosticsCategoryButton.IsChecked = category == SettingsCategory.Diagnostics;
        SettingsCategoryScrollViewer.ScrollToTop();
    }

    private void SetMainView(MainViewMode view)
    {
        var isHome = view == MainViewMode.Home;
        var isActivity = view == MainViewMode.Activity;
        MainContentGrid.Visibility = view == MainViewMode.Settings ? Visibility.Collapsed : Visibility.Visible;
        ProfileNavigationContent.Visibility = isHome ? Visibility.Visible : Visibility.Collapsed;
        ActionEditor.Visibility = isHome ? Visibility.Visible : Visibility.Collapsed;
        ActivityContent.Visibility = isActivity ? Visibility.Visible : Visibility.Collapsed;
        SettingsContent.Visibility = view == MainViewMode.Settings ? Visibility.Visible : Visibility.Collapsed;
        HomeNavigationButton.IsChecked = isHome;
        ActivityNavigationButton.IsChecked = isActivity;
        SettingsNavigationButton.IsChecked = view == MainViewMode.Settings;
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.ActiveMainView = view;
            viewModel.IsActivityExpanded = isActivity;
        }
    }

    private void ThemeMenuButton_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        if (button.DataContext is ProfileItemViewModel profile && DataContext is MainWindowViewModel viewModel)
            viewModel.SelectedProfile = profile;
        menu.PlacementTarget = button;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void ContextMenu_OnClosed(object? sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu { PlacementTarget: Button button }) return;
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (button.IsKeyboardFocusWithin) Keyboard.ClearFocus();
        });
    }

    private void TitleBar_OnMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        // WindowChrome performs native drag and resize hit testing. Handle only
        // the caption double-click so the empty titlebar keeps Windows behavior.
        if (e.OriginalSource is DependencyObject source && IsInsideButton(source)) return;
        if (e.ClickCount != 2) return;
        ToggleWindowState();
        e.Handled = true;
    }

    private static bool IsInsideButton(DependencyObject source)
    {
        for (DependencyObject? current = source; current is not null;)
        {
            if (current is Button) return true;
            current = current is System.Windows.Media.Visual || current is System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return false;
    }

    private void MinimizeButton_OnClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaximizeButton_OnClick(object sender, RoutedEventArgs e) => ToggleWindowState();

    private void CloseButton_OnClick(object sender, RoutedEventArgs e) => Close();

    private void ToggleWindowState() => WindowState = WindowState == WindowState.Maximized
        ? WindowState.Normal
        : WindowState.Maximized;

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
            if (viewModel.HasUnsavedChanges && viewModel.WarnBeforeClosingWithUnsavedChanges)
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
            if (string.Equals(viewModel.CloseBehavior, "tray", StringComparison.OrdinalIgnoreCase) &&
                !_exitRequestedFromTray)
            {
                Hide();
                return;
            }
            viewModel.Dispose();
            _closeApproved = true;
            Close();
        }
        finally
        {
            _closeInProgress = false;
            if (!_closeApproved) _exitRequestedFromTray = false;
        }
    }

    private void OpenFromTray()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(OpenFromTray);
            return;
        }
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Focus();
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero) SetForegroundWindow(handle);
    }

    internal void ActivateFromSingleInstance() => OpenFromTray();

    private void ExitFromTray()
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(ExitFromTray);
            return;
        }
        _exitRequestedFromTray = true;
        Close();
    }

    private void RunOnUiAsync(Func<Task> action)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => RunOnUiAsync(action));
            return;
        }
        _ = RunSafelyAsync(action);
    }

    private static async Task RunSafelyAsync(Func<Task> action)
    {
        try { await action(); }
        catch { /* individual view-model operations report their own failures */ }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Closed -= OnClosed;
        _trayService?.Dispose();
        _trayService = null;
        _viewModel.Dispose();
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

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

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

    private void SystemChangeEntry_OnMouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        if (sender is not FrameworkElement { DataContext: ViewModels.SystemChangeItemViewModel entry } ||
            DataContext is not MainWindowViewModel viewModel) return;
        if (viewModel.NavigateToProfileAction(entry.ProfileId, entry.ActionId))
        {
            SetMainView(MainViewMode.Home);
            ActionEditor.BringActionIntoView(entry.ActionId);
        }
    }

    private void SystemChangeTarget_OnMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: SystemChangeItemViewModel entry } ||
            DataContext is not MainWindowViewModel viewModel || !entry.IsNavigable) return;
        if (viewModel.NavigateToProfileAction(entry.ProfileId, entry.ActionId))
        {
            SetMainView(MainViewMode.Home);
            ActionEditor.BringActionIntoView(entry.ActionId);
        }
        e.Handled = true;
    }

    private void HistoryAction_OnMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ProfileExecutionActionViewModel entry } ||
            DataContext is not MainWindowViewModel viewModel) return;
        if (viewModel.NavigateToProfileAction(entry.ProfileId, entry.ActionId))
        {
            SetMainView(MainViewMode.Home);
            ActionEditor.BringActionIntoView(entry.ActionId);
        }
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
