using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SwitchBoard.ViewModels;

namespace SwitchBoard.Controls;

public partial class ActionEditorControl : UserControl
{
    public ActionEditorControl()
    {
        InitializeComponent();
    }

    private void RunProfile_OnClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainWindowViewModel viewModel) viewModel.TraceRunClicked();
    }

    public void BringActionIntoView(Guid actionId)
    {
        BringActionIntoView(actionId, 0);
    }

    private void BringActionIntoView(Guid actionId, int attempt)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            if (DataContext is not ViewModels.MainWindowViewModel viewModel) return;
            var action = viewModel.SelectedProfile?.Actions.SelectMany(Flatten).FirstOrDefault(item => item.Id == actionId);
            if (action is null) return;
            ActionList.SelectedItem = action;
            ActionList.UpdateLayout();
            if (ActionList.ItemContainerGenerator.ContainerFromItem(action) is FrameworkElement container)
                container.BringIntoView();
            else if (attempt < 4)
                BringActionIntoView(actionId, attempt + 1);
        });
    }

    private static IEnumerable<ViewModels.ActionItemViewModel> Flatten(ViewModels.ActionItemViewModel action)
    {
        yield return action;
        foreach (var child in action.ThenActions.Concat(action.ElseActions))
            foreach (var nested in Flatten(child)) yield return nested;
    }

    private void ActionPicker_OnOpened(object? sender, EventArgs e)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            ActionPickerSearchBox.Focus();
            ActionPickerSearchBox.SelectAll();
        });
    }

    private void ActionOverflow_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.ContextMenu is null) return;
        button.ContextMenu.PlacementTarget = button;
        button.ContextMenu.IsOpen = true;
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

    private void ActionPickerSearch_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is ViewModels.MainWindowViewModel viewModel)
        {
            viewModel.IsActionPickerOpen = false;
            e.Handled = true;
        }
    }

    private void ActionList_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.D || Keyboard.Modifiers != ModifierKeys.Control ||
            Keyboard.FocusedElement is TextBox || DataContext is not ViewModels.MainWindowViewModel viewModel ||
            ActionList.SelectedItem is not ViewModels.ActionItemViewModel action) return;
        if (viewModel.DuplicateActionCommand.CanExecute(action))
        {
            viewModel.DuplicateActionCommand.Execute(action);
            e.Handled = true;
        }
    }

    private void ActionList_OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (FindAncestor<ComboBox>(e.OriginalSource as DependencyObject) is { IsDropDownOpen: true }) return;
        var viewer = FindDescendant<ScrollViewer>(ActionList);
        if (viewer is null || viewer.ScrollableHeight <= 0) return;
        var target = Math.Clamp(viewer.VerticalOffset - (e.Delta / 3.0), 0, viewer.ScrollableHeight);
        viewer.ScrollToVerticalOffset(target);
        e.Handled = true;
    }

    private void ActionList_OnRequestBringIntoView(object sender, RequestBringIntoViewEventArgs e)
    {
        // WPF's item-based ListBox scrolling otherwise moves a whole action card when a nested editor gets focus.
        e.Handled = true;
    }

    private void ActionHeader_OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button header) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => KeepHeaderVisible(header));
    }

    private void KeepHeaderVisible(FrameworkElement header)
    {
        var viewer = FindDescendant<ScrollViewer>(ActionList);
        if (viewer is null || !header.IsVisible) return;
        try
        {
            var topLeft = header.TransformToAncestor(viewer).Transform(new Point(0, 0));
            var top = topLeft.Y;
            var bottom = top + header.ActualHeight;
            if (top < 0) viewer.ScrollToVerticalOffset(Math.Max(0, viewer.VerticalOffset + top - 6));
            else if (bottom > viewer.ViewportHeight)
                viewer.ScrollToVerticalOffset(Math.Min(viewer.ScrollableHeight,
                    viewer.VerticalOffset + bottom - viewer.ViewportHeight + 6));
        }
        catch (InvalidOperationException) { }
    }

    private static T? FindDescendant<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent is null) return null;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child is not null)
        {
            if (child is T match) return match;
            child = child is Visual || child is System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(child)
                : LogicalTreeHelper.GetParent(child);
        }
        return null;
    }
}
