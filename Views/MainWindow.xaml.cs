using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using System.Windows.Interop;
using System.Windows.Media;
using SwitchBoard.Controls;
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
    private HwndSource? _windowSource;
    private DispatcherOperation? _pendingBackgroundFit;
    private BackgroundNativeSize? _pendingNativeBackgroundSize;
    private double _defaultMaxWidth;
    private double _defaultMaxHeight;
    private bool _exitRequestedFromTray;
    private string? _lastVideoBackendProblemKey;

    private const int WmDpiChanged = 0x02E0;
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    public MainWindow(MainWindowViewModel viewModel)
    {
        _viewModel = viewModel;
        InitializeComponent();
        var workArea = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, workArea.Width);
        MinHeight = Math.Min(MinHeight, workArea.Height);
        MaxWidth = workArea.Width;
        MaxHeight = workArea.Height;
        _defaultMaxWidth = MaxWidth;
        _defaultMaxHeight = MaxHeight;
        DataContext = viewModel;
        // LibVLC temporarily reparents MainVisualOverlay into its VideoView.
        // Keep the window bindings alive when the overlay no longer inherits
        // DataContext from MainWindow.
        MainVisualOverlay.DataContext = viewModel;
        ThemeBackgroundHost.SetExternalOverlay(MainVisualOverlay);
        ThemeBackgroundHost.NativeSizeChanged += ThemeBackgroundHostOnNativeSizeChanged;
        ThemeBackgroundHost.VideoBackendProblem += ThemeBackgroundHostOnVideoBackendProblem;
        _viewModel.PropertyChanged += ViewModelOnPropertyChanged;
        Loaded += MainWindowOnLoaded;
        SourceInitialized += MainWindowOnSourceInitialized;
        SetSettingsCategory(_selectedSettingsCategory);
        SetMainView(viewModel.InitialMainView);
        RestoreWindowGeometry(viewModel, workArea);
        Closing += OnClosing;
        SizeChanged += (_, _) => viewModel.CaptureWindowGeometry(this);
        LocationChanged += (_, _) => viewModel.CaptureWindowGeometry(this);
        StateChanged += MainWindowOnStateChanged;
        InputManager.Current.PreProcessInput += MainWindowOnPreProcessInput;
        Closed += OnClosed;
        _trayService = new SystemTrayService(OpenFromTray, ExitFromTray, viewModel.GetTrayProfiles,
            () => viewModel.HasPendingRestore,
            id => RunOnUiAsync(() => viewModel.RunProfileFromTrayAsync(id)),
            () => RunOnUiAsync(viewModel.RestoreProfileFromTrayAsync), viewModel.GetLocalizedText);
    }

    private void HomeNavigationButton_OnClick(object sender, RoutedEventArgs e) => SetMainView(MainViewMode.Home);

    private void SystemNavigationButton_OnClick(object sender, RoutedEventArgs e) => SetMainView(MainViewMode.System);

    private void PerformanceNavigationButton_OnClick(object sender, RoutedEventArgs e) => SetMainView(MainViewMode.Performance);

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
        var isSystem = view == MainViewMode.System;
        var isPerformance = view == MainViewMode.Performance;
        var isActivity = view == MainViewMode.Activity;
        MainContentGrid.Visibility = view == MainViewMode.Settings ? Visibility.Collapsed : Visibility.Visible;
        ProfileNavigationContent.Visibility = isHome ? Visibility.Visible : Visibility.Collapsed;
        ActionEditor.Visibility = isHome ? Visibility.Visible : Visibility.Collapsed;
        SystemContent.Visibility = isSystem ? Visibility.Visible : Visibility.Collapsed;
        PerformanceContent.Visibility = isPerformance ? Visibility.Visible : Visibility.Collapsed;
        ActivityContent.Visibility = isActivity ? Visibility.Visible : Visibility.Collapsed;
        SettingsContent.Visibility = view == MainViewMode.Settings ? Visibility.Visible : Visibility.Collapsed;
        HomeNavigationButton.IsChecked = isHome;
        SystemNavigationButton.IsChecked = isSystem;
        PerformanceNavigationButton.IsChecked = isPerformance;
        ActivityNavigationButton.IsChecked = isActivity;
        SettingsNavigationButton.IsChecked = view == MainViewMode.Settings;
        if (DataContext is MainWindowViewModel viewModel)
        {
            viewModel.ActiveMainView = view;
            viewModel.IsActivityExpanded = isActivity;
        }
        if (isSystem) SystemContent.ScrollToTop();
        if (isPerformance) PerformanceContent.ScrollToTop();
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

    private void ThemeMenuItem_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string action, CommandParameter: string themeId } ||
            DataContext is not MainWindowViewModel viewModel)
            return;

        ICommand? command = action switch
        {
            "edit" => viewModel.EditThemeCommand,
            "duplicate" => viewModel.DuplicateThemeCommand,
            "rename" => viewModel.RenameThemeCommand,
            "export" => viewModel.ExportThemeCommand,
            "delete" => viewModel.DeleteThemeCommand,
            _ => null
        };
        if (command?.CanExecute(themeId) == true) command.Execute(themeId);
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
        if (e.ClickCount == 1 && e.LeftButton == MouseButtonState.Pressed &&
            sender is DependencyObject element && !ReferenceEquals(Window.GetWindow(element), this))
        {
            // LibVLCSharp displays the WPF content in its owned ForegroundWindow.
            // Forward caption drags to the real application window because that
            // separate overlay otherwise receives the mouse hit-test.
            try { DragMove(); } catch (InvalidOperationException) { }
            e.Handled = true;
            return;
        }
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

    /// <summary>
    /// Commits an active single-line editor when the user continues interacting
    /// elsewhere in the application. WPF does not always move keyboard focus
    /// when the destination is a non-focusable panel/card, which otherwise
    /// leaves a search or editor TextBox active after such a click.
    /// </summary>
    private void MainWindowOnPreProcessInput(object? sender, PreProcessInputEventArgs e)
    {
        if (e.StagingItem.Input is not MouseButtonEventArgs
            {
                RoutedEvent: var routedEvent,
                OriginalSource: DependencyObject source
            } || routedEvent != Mouse.PreviewMouseDownEvent ||
            !IsPartOfThisWindow(source) || IsInsideEditableInput(source))
            return;

        if (Keyboard.FocusedElement is TextBox textBox)
            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();

        Keyboard.ClearFocus();
    }

    private bool IsPartOfThisWindow(DependencyObject source)
    {
        for (DependencyObject? current = source; current is not null;)
        {
            if (ReferenceEquals(current, this)) return true;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return false;
    }

    private static bool IsInsideEditableInput(DependencyObject source)
    {
        for (DependencyObject? current = source; current is not null;)
        {
            if (current is TextBoxBase or PasswordBox ||
                current is ComboBox { IsEditable: true })
                return true;

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
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
            await viewModel.CreateBackupForShutdownAsync();
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
        Loaded -= MainWindowOnLoaded;
        SourceInitialized -= MainWindowOnSourceInitialized;
        StateChanged -= MainWindowOnStateChanged;
        InputManager.Current.PreProcessInput -= MainWindowOnPreProcessInput;
        ThemeBackgroundHost.NativeSizeChanged -= ThemeBackgroundHostOnNativeSizeChanged;
        ThemeBackgroundHost.VideoBackendProblem -= ThemeBackgroundHostOnVideoBackendProblem;
        _viewModel.PropertyChanged -= ViewModelOnPropertyChanged;
        if (_windowSource is not null) _windowSource.RemoveHook(WindowMessageHook);
        _windowSource = null;
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

    private void MainWindowOnLoaded(object sender, RoutedEventArgs e)
    {
        QueueBackgroundFit(ThemeBackgroundHost.NativeSize);
    }

    private void MainWindowOnStateChanged(object? sender, EventArgs e)
    {
        _viewModel.CaptureWindowGeometry(this);
    }

    private void MainWindowOnSourceInitialized(object? sender, EventArgs e)
    {
        _windowSource = PresentationSource.FromVisual(this) as HwndSource;
        _windowSource?.AddHook(WindowMessageHook);
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmDpiChanged) QueueBackgroundFit(ThemeBackgroundHost.NativeSize);
        return IntPtr.Zero;
    }

    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainWindowViewModel.AutoFitWindowToBackground)) return;
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(() => ViewModelOnPropertyChanged(sender, e));
            return;
        }

        if (_viewModel.AutoFitWindowToBackground) QueueBackgroundFit(ThemeBackgroundHost.NativeSize);
        else RestoreDefaultWindowLimits();
    }

    private void ThemeBackgroundHostOnNativeSizeChanged(object? sender, BackgroundNativeSizeChangedEventArgs e) =>
        QueueBackgroundFit(e.Size);

    private void ThemeBackgroundHostOnVideoBackendProblem(object? sender, VideoBackendProblemEventArgs e)
    {
        _viewModel.ReportVideoBackendProblem(e);
        var key = $"{e.Kind}|{e.SourcePath}";
        if (!e.CanInstallLibVlc || !_viewModel.CanInstallOptionalLibVlc ||
            string.Equals(_lastVideoBackendProblemKey, key, StringComparison.Ordinal))
            return;

        _lastVideoBackendProblemKey = key;
        var message = _viewModel.GetVideoBackendProblemText(e) + Environment.NewLine + Environment.NewLine +
            _viewModel.GetLocalizedText("Dialog.Mp4RendererInstallPrompt");
        if (MessageBox.Show(message, _viewModel.GetLocalizedText("Dialog.Mp4RendererProblemTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        SetMainView(MainViewMode.Settings);
        SetSettingsCategory(SettingsCategory.Interface);
    }

    private void QueueBackgroundFit(BackgroundNativeSize? nativeSize)
    {
        if (!_viewModel.AutoFitWindowToBackground || nativeSize is not BackgroundNativeSize size || !size.IsValid ||
            WindowState != WindowState.Normal)
            return;

        _pendingNativeBackgroundSize = size;
        if (_pendingBackgroundFit?.Status == DispatcherOperationStatus.Pending) return;
        _pendingBackgroundFit = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            _pendingBackgroundFit = null;
            var pending = _pendingNativeBackgroundSize;
            _pendingNativeBackgroundSize = null;
            if (pending is BackgroundNativeSize background) ApplyBackgroundFit(background);
        }));
    }

    private void ApplyBackgroundFit(BackgroundNativeSize nativeSize)
    {
        if (!_viewModel.AutoFitWindowToBackground || WindowState != WindowState.Normal || !nativeSize.IsValid ||
            !TryGetMonitorMetrics(out var metrics))
            return;

        var backgroundPixels = new Size(
            Math.Max(1, ThemeBackgroundHost.ActualWidth * metrics.Dpi.DpiScaleX),
            Math.Max(1, ThemeBackgroundHost.ActualHeight * metrics.Dpi.DpiScaleY));
        var fit = BackgroundWindowAutoSize.Calculate(nativeSize, metrics.WorkAreaPixels, metrics.WindowPixels,
            backgroundPixels, metrics.Dpi);
        if (fit is null) return;

        var maximumWidth = Math.Max(MinWidth, metrics.WorkAreaPixels.Width / metrics.Dpi.DpiScaleX);
        var maximumHeight = Math.Max(MinHeight, metrics.WorkAreaPixels.Height / metrics.Dpi.DpiScaleY);
        MaxWidth = maximumWidth;
        MaxHeight = maximumHeight;

        // The existing minimum window size protects the layout. Do not upscale a small
        // background merely to satisfy it; leave the user's current size unchanged.
        if (fit.Value.WindowDips.Width < MinWidth || fit.Value.WindowDips.Height < MinHeight) return;

        var targetWidth = Math.Clamp(fit.Value.WindowDips.Width, MinWidth, MaxWidth);
        var targetHeight = Math.Clamp(fit.Value.WindowDips.Height, MinHeight, MaxHeight);
        if (Math.Abs(Width - targetWidth) > 0.5) Width = targetWidth;
        if (Math.Abs(Height - targetHeight) > 0.5) Height = targetHeight;
        UpdateLayout();
        KeepWindowInsideWorkingArea(metrics.WorkArea, metrics.WindowHandle);
    }

    private void RestoreDefaultWindowLimits()
    {
        MaxWidth = _defaultMaxWidth;
        MaxHeight = _defaultMaxHeight;
    }

    private bool TryGetMonitorMetrics(out MonitorMetrics metrics)
    {
        metrics = default;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return false;
        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return false;
        var info = new MonitorInfo { CbSize = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info) || !GetWindowRect(handle, out var windowRect)) return false;

        uint dpiValue;
        try { dpiValue = GetDpiForWindow(handle); }
        catch (EntryPointNotFoundException) { dpiValue = 0; }
        var dpi = dpiValue > 0
            ? new DpiScale(dpiValue / 96d, dpiValue / 96d)
            : VisualTreeHelper.GetDpi(ThemeBackgroundHost);
        metrics = new MonitorMetrics(handle, info.Work, new Size(info.Work.Right - info.Work.Left,
            info.Work.Bottom - info.Work.Top), new Size(windowRect.Right - windowRect.Left,
            windowRect.Bottom - windowRect.Top), dpi);
        return true;
    }

    private static void KeepWindowInsideWorkingArea(NativeRect workingArea, IntPtr handle)
    {
        if (!GetWindowRect(handle, out var bounds)) return;
        var left = Math.Clamp(bounds.Left, workingArea.Left, Math.Max(workingArea.Left, workingArea.Right -
            (bounds.Right - bounds.Left)));
        var top = Math.Clamp(bounds.Top, workingArea.Top, Math.Max(workingArea.Top, workingArea.Bottom -
            (bounds.Bottom - bounds.Top)));
        if (left == bounds.Left && top == bounds.Top) return;
        _ = SetWindowPos(handle, IntPtr.Zero, left, top, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
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

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr window, IntPtr insertAfter, int x, int y, int width, int height,
        uint flags);

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

    private readonly record struct MonitorMetrics(IntPtr WindowHandle, NativeRect WorkArea, Size WorkAreaPixels,
        Size WindowPixels, DpiScale Dpi);

    private void ActivityContent_OnNavigationRequested(object? sender,
        Panels.ActivityNavigationRequestedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel &&
            viewModel.NavigateToProfileAction(e.ProfileId, e.ActionId))
        {
            SetMainView(MainViewMode.Home);
            ActionEditor.BringActionIntoView(e.ActionId);
        }
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
