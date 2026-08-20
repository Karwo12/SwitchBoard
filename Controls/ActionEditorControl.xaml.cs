using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SwitchBoard.Controls;

public partial class ActionEditorControl : UserControl
{
    public ActionEditorControl()
    {
        InitializeComponent();
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
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }
}
