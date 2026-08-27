using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using SwitchBoard.ViewModels;

namespace SwitchBoard.Controls;

/// <summary>
/// Adds in-process, mouse-driven reordering to a ListBox without changing its item template.
/// The behavior owns only drag visuals; the view model remains responsible for mutating and saving data.
/// </summary>
public static class ListBoxDragDrop
{
    private const string DataFormat = "SwitchBoard.ListBoxDragPayload.v1";
    private static Point _dragStart;
    private static object? _pressedItem;
    private static ListBox? _sourceList;
    private static ListBoxItem? _sourceContainer;
    private static bool _dragStarted;
    private static Adorner? _activeAdorner;
    private static AdornerLayer? _activeAdornerLayer;

    public static readonly DependencyProperty DragKindProperty = DependencyProperty.RegisterAttached(
        "DragKind", typeof(ReorderItemKind?), typeof(ListBoxDragDrop),
        new PropertyMetadata(null, OnDragKindChanged));

    public static readonly DependencyProperty AcceptKindsProperty = DependencyProperty.RegisterAttached(
        "AcceptKinds", typeof(string), typeof(ListBoxDragDrop), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty DropCommandProperty = DependencyProperty.RegisterAttached(
        "DropCommand", typeof(ICommand), typeof(ListBoxDragDrop));

    public static readonly DependencyProperty TargetParentIdProperty = DependencyProperty.RegisterAttached(
        "TargetParentId", typeof(object), typeof(ListBoxDragDrop));

    public static readonly DependencyProperty IsDragHandleProperty = DependencyProperty.RegisterAttached(
        "IsDragHandle", typeof(bool), typeof(ListBoxDragDrop), new PropertyMetadata(false));

    public static readonly DependencyProperty SuppressSelectionOnDragProperty = DependencyProperty.RegisterAttached(
        "SuppressSelectionOnDrag", typeof(bool), typeof(ListBoxDragDrop), new PropertyMetadata(false));

    public static readonly DependencyProperty IsRootNavigationTargetProperty = DependencyProperty.RegisterAttached(
        "IsRootNavigationTarget", typeof(bool), typeof(ListBoxDragDrop), new PropertyMetadata(false));

    public static readonly DependencyProperty IsProfileFolderDropTargetProperty = DependencyProperty.RegisterAttached(
        "IsProfileFolderDropTarget", typeof(bool), typeof(ListBoxDragDrop),
        new PropertyMetadata(false, OnIsProfileFolderDropTargetChanged));

    public static void SetDragKind(DependencyObject element, ReorderItemKind? value) => element.SetValue(DragKindProperty, value);
    public static ReorderItemKind? GetDragKind(DependencyObject element) => (ReorderItemKind?)element.GetValue(DragKindProperty);
    public static void SetAcceptKinds(DependencyObject element, string value) => element.SetValue(AcceptKindsProperty, value);
    public static string GetAcceptKinds(DependencyObject element) => (string)element.GetValue(AcceptKindsProperty);
    public static void SetDropCommand(DependencyObject element, ICommand value) => element.SetValue(DropCommandProperty, value);
    public static ICommand? GetDropCommand(DependencyObject element) => (ICommand?)element.GetValue(DropCommandProperty);
    public static void SetTargetParentId(DependencyObject element, object? value) => element.SetValue(TargetParentIdProperty, value);
    public static object? GetTargetParentId(DependencyObject element) => element.GetValue(TargetParentIdProperty);
    public static void SetIsDragHandle(DependencyObject element, bool value) => element.SetValue(IsDragHandleProperty, value);
    public static bool GetIsDragHandle(DependencyObject element) => (bool)element.GetValue(IsDragHandleProperty);
    public static void SetSuppressSelectionOnDrag(DependencyObject element, bool value) =>
        element.SetValue(SuppressSelectionOnDragProperty, value);
    public static bool GetSuppressSelectionOnDrag(DependencyObject element) =>
        (bool)element.GetValue(SuppressSelectionOnDragProperty);
    public static void SetIsRootNavigationTarget(DependencyObject element, bool value) => element.SetValue(IsRootNavigationTargetProperty, value);
    public static bool GetIsRootNavigationTarget(DependencyObject element) => (bool)element.GetValue(IsRootNavigationTargetProperty);
    public static void SetIsProfileFolderDropTarget(DependencyObject element, bool value) => element.SetValue(IsProfileFolderDropTargetProperty, value);
    public static bool GetIsProfileFolderDropTarget(DependencyObject element) => (bool)element.GetValue(IsProfileFolderDropTargetProperty);

    private static void OnDragKindChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not ListBox listBox) return;
        if (e.OldValue is not null)
        {
            listBox.PreviewMouseLeftButtonDown -= OnPreviewMouseLeftButtonDown;
            listBox.PreviewMouseLeftButtonUp -= OnPreviewMouseLeftButtonUp;
            listBox.PreviewMouseMove -= OnPreviewMouseMove;
            listBox.DragOver -= OnDragOver;
            listBox.DragLeave -= OnDragLeave;
            listBox.Drop -= OnDrop;
        }
        if (e.NewValue is null) return;
        listBox.AllowDrop = true;
        listBox.PreviewMouseLeftButtonDown += OnPreviewMouseLeftButtonDown;
        listBox.PreviewMouseLeftButtonUp += OnPreviewMouseLeftButtonUp;
        listBox.PreviewMouseMove += OnPreviewMouseMove;
        listBox.DragOver += OnDragOver;
        listBox.DragLeave += OnDragLeave;
        listBox.Drop += OnDrop;
    }

    private static void OnIsProfileFolderDropTargetChanged(DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not FrameworkElement target) return;
        if (e.OldValue is true)
        {
            target.DragOver -= OnProfileFolderDragOver;
            target.DragLeave -= OnProfileFolderDragLeave;
            target.Drop -= OnProfileFolderDrop;
        }
        if (e.NewValue is not true) return;

        target.AllowDrop = true;
        target.DragOver += OnProfileFolderDragOver;
        target.DragLeave += OnProfileFolderDragLeave;
        target.Drop += OnProfileFolderDrop;
    }

    private static void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox || e.OriginalSource is not DependencyObject source) return;
        var container = ItemsControl.ContainerFromElement(listBox, source) as ListBoxItem;
        if (container is null || !CanBeginFrom(source, container))
        {
            ResetPressedItem();
            return;
        }

        _dragStart = e.GetPosition(listBox);
        _pressedItem = container.DataContext;
        _sourceList = listBox;
        _sourceContainer = container;
        _dragStarted = false;
        if (GetSuppressSelectionOnDrag(listBox))
        {
            // Defer ListBox selection until mouse-up. This keeps a drag from
            // applying a different theme while still making a simple click select it.
            listBox.CaptureMouse();
            e.Handled = true;
        }
    }

    private static void OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox listBox || !ReferenceEquals(listBox, _sourceList)) return;

        if (GetSuppressSelectionOnDrag(listBox) && !_dragStarted && _pressedItem is { } item)
        {
            listBox.SelectedItem = item;
            e.Handled = true;
        }

        ResetPressedItem();
    }

    private static void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (sender is not ListBox listBox || !ReferenceEquals(listBox, _sourceList) ||
            _pressedItem is null || e.LeftButton != MouseButtonState.Pressed)
            return;

        var current = e.GetPosition(listBox);
        if (Math.Abs(current.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var kind = ResolveDragKind(listBox, _pressedItem);
        if (kind is null) return;
        _dragStarted = true;
        var payload = new DragPayload(kind.Value, _pressedItem);
        var data = new DataObject(DataFormat, payload);
        var container = _sourceContainer;
        if (container is not null) container.Opacity = 0.58;
        try
        {
            DragDrop.DoDragDrop(listBox, data, DragDropEffects.Move);
        }
        finally
        {
            if (container is not null) container.Opacity = 1;
            ClearAdorner();
            ResetPressedItem();
        }
    }

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        if (sender is not ListBox target || !TryGetAcceptedPayload(target, e.Data, out var payload))
        {
            // A category is reordered only by the shared root ListBox. Let its
            // event reach that parent when the pointer is over a category's
            // nested profile list.
            if (sender is ListBox nestedTarget && !GetIsRootNavigationTarget(nestedTarget) &&
                TryGetPayload(e.Data, out var categoryPayload) &&
                categoryPayload.Kind == ReorderItemKind.Category)
                return;

            e.Effects = DragDropEffects.None;
            e.Handled = true;
            ClearAdorner();
            return;
        }

        AutoScroll(target, e.GetPosition(target));
        var isCategoryTarget = payload.Kind == ReorderItemKind.Profile &&
            GetDragKind(target) == ReorderItemKind.Category && !GetIsRootNavigationTarget(target);
        var hit = GetDropPosition(target, e.GetPosition(target), isCategoryTarget);
        if (!hit.IsValid)
        {
            // Layout can be between virtualization passes while auto-scrolling. Keep the last stable cue
            // instead of jumping to an arbitrary edge of the ListBox.
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }
        if (isCategoryTarget && hit.Container is null)
        {
            e.Effects = DragDropEffects.None;
            ClearAdorner();
        }
        else
        {
            ShowAdorner(target, hit.Container, hit.PlaceAfter, isCategoryTarget);
            e.Effects = DragDropEffects.Move;
        }
        e.Handled = true;
    }

    private static void OnDragLeave(object sender, DragEventArgs e)
    {
        if (sender is not ListBox listBox) return;
        var point = e.GetPosition(listBox);
        if (point.X < 0 || point.Y < 0 || point.X > listBox.ActualWidth || point.Y > listBox.ActualHeight)
            ClearAdorner();
    }

    private static void OnDrop(object sender, DragEventArgs e)
    {
        ClearAdorner();
        if (sender is not ListBox target || !TryGetAcceptedPayload(target, e.Data, out var payload))
        {
            // See OnDragOver: category drops belong to the shared root target,
            // even when the pointer is over a nested profile list.
            if (sender is ListBox nestedTarget && !GetIsRootNavigationTarget(nestedTarget) &&
                TryGetPayload(e.Data, out var categoryPayload) &&
                categoryPayload.Kind == ReorderItemKind.Category)
                return;
            return;
        }
        var isCategoryTarget = payload.Kind == ReorderItemKind.Profile &&
            GetDragKind(target) == ReorderItemKind.Category && !GetIsRootNavigationTarget(target);
        var hit = GetDropPosition(target, e.GetPosition(target), isCategoryTarget);
        if (!hit.IsValid || isCategoryTarget && hit.Item is null) return;

        var targetIndex = isCategoryTarget ? target.Items.Count : hit.InsertionIndex;
        var parent = GetTargetParentId(target);
        Guid? parentId = parent switch
        {
            Guid guid => guid,
            _ => null
        };
        var request = new ReorderDropRequest(payload.Kind, payload.Item, hit.Item, targetIndex, parentId);
        var command = GetDropCommand(target);
        if (command?.CanExecute(request) == true)
        {
            command.Execute(request);
            e.Effects = DragDropEffects.Move;
        }
        else e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private static bool TryGetAcceptedPayload(ListBox target, IDataObject data, out DragPayload payload)
    {
        payload = null!;
        if (!TryGetPayload(data, out var candidate))
        {
            return false;
        }
        var accepts = GetAcceptKinds(target);
        if (string.IsNullOrWhiteSpace(accepts))
            return GetDragKind(target) == candidate.Kind && (payload = candidate) is not null;
        var accepted = accepts.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Any(value => Enum.TryParse<ReorderItemKind>(value, true, out var kind) && kind == candidate.Kind);
        if (accepted) payload = candidate;
        return accepted;
    }

    private static bool TryGetPayload(IDataObject data, out DragPayload payload)
    {
        payload = null!;
        if (!data.GetDataPresent(DataFormat) || data.GetData(DataFormat) is not DragPayload candidate) return false;
        payload = candidate;
        return true;
    }

    private static ReorderItemKind? ResolveDragKind(ListBox listBox, object item) => item switch
    {
        ProfileItemViewModel => ReorderItemKind.Profile,
        CategoryItemViewModel => ReorderItemKind.Category,
        _ => GetDragKind(listBox)
    };

    private static void OnProfileFolderDragOver(object sender, DragEventArgs e)
    {
        if (TryGetPayload(e.Data, out var categoryPayload) &&
            categoryPayload.Kind == ReorderItemKind.Category)
            return;

        if (sender is not FrameworkElement target || target.DataContext is not CategoryItemViewModel ||
            !TryGetPayload(e.Data, out var payload) || payload.Kind != ReorderItemKind.Profile)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            ClearAdorner();
            return;
        }

        ShowAdorner(target, null, after: false, highlight: true);
        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private static void OnProfileFolderDragLeave(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement target) return;
        var point = e.GetPosition(target);
        if (point.X < 0 || point.Y < 0 || point.X > target.ActualWidth || point.Y > target.ActualHeight)
            ClearAdorner();
    }

    private static void OnProfileFolderDrop(object sender, DragEventArgs e)
    {
        ClearAdorner();
        if (TryGetPayload(e.Data, out var categoryPayload) &&
            categoryPayload.Kind == ReorderItemKind.Category)
            return;

        if (sender is not FrameworkElement target || target.DataContext is not CategoryItemViewModel category ||
            !TryGetPayload(e.Data, out var payload) || payload.Kind != ReorderItemKind.Profile)
        {
            return;
        }

        var request = new ReorderDropRequest(payload.Kind, payload.Item, category, category.Profiles.Count, category.Id);
        var command = GetDropCommand(target);
        if (command?.CanExecute(request) == true)
        {
            command.Execute(request);
            e.Effects = DragDropEffects.Move;
        }
        else e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private static DropPosition GetDropPosition(ListBox listBox, Point point, bool selectNearestItem)
    {
        if (listBox.Items.Count == 0) return new(null, null, 0, false, true);

        var realized = new List<RealizedItem>();
        for (var index = 0; index < listBox.Items.Count; index++)
        {
            if (listBox.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem container ||
                !container.IsVisible || container.ActualHeight <= 0)
                continue;
            try
            {
                var top = container.TranslatePoint(new Point(0, 0), listBox).Y;
                realized.Add(new(container, container.DataContext, index, top, top + container.ActualHeight));
            }
            catch (InvalidOperationException)
            {
                // The item was recycled between enumeration and coordinate translation.
            }
        }

        if (realized.Count == 0) return new(null, null, 0, false, false);
        realized.Sort((left, right) => left.Top.CompareTo(right.Top));

        if (selectNearestItem)
        {
            var nearest = realized.MinBy(item => Math.Abs(item.Center - point.Y))!;
            return new(nearest.Container, nearest.Item, nearest.Index, false, true);
        }

        // An insertion boundary changes only when the pointer crosses an item's vertical center.
        // This also covers margins and template gaps where InputHitTest does not return a ListBoxItem.
        var next = realized.FirstOrDefault(item => point.Y <= item.Center);
        if (next is not null)
            return new(next.Container, next.Item, next.Index, false, true);

        var last = realized[^1];
        return new(last.Container, last.Item, Math.Min(listBox.Items.Count, last.Index + 1), true, true);
    }

    // Mouse events from formatted text can originate from Run/TextElement, which is a
    // ContentElement rather than a Visual. Walking only the visual tree throws there
    // and turns an ordinary click into an unhandled Dispatcher exception.
    internal static bool CanBeginFrom(DependencyObject source, ListBoxItem container)
    {
        var current = source;
        while (current is not null && !ReferenceEquals(current, container))
        {
            if (GetIsDragHandle(current)) return true;
            if (current is TextBoxBase or ComboBox or CheckBox or Slider or ScrollBar) return false;
            if (current is ButtonBase) return false;
            current = GetParent(current);
        }
        return true;
    }

    private static void AutoScroll(ListBox listBox, Point point)
    {
        var viewer = FindDescendant<ScrollViewer>(listBox) ?? FindAncestor<ScrollViewer>(listBox);
        if (viewer is null || viewer.ScrollableHeight <= 0) return;
        const double edge = 34;
        const double step = 14;
        if (point.Y < edge) viewer.ScrollToVerticalOffset(Math.Max(0, viewer.VerticalOffset - step));
        else if (point.Y > listBox.ActualHeight - edge)
            viewer.ScrollToVerticalOffset(Math.Min(viewer.ScrollableHeight, viewer.VerticalOffset + step));
    }

    private static void ShowAdorner(UIElement target, ListBoxItem? container, bool after, bool highlight)
    {
        var adorned = (UIElement?)container ?? target;
        if (_activeAdorner?.AdornedElement == adorned &&
            _activeAdorner is DropCueAdorner cue && cue.After == after && cue.Highlight == highlight)
            return;
        ClearAdorner();
        var layer = AdornerLayer.GetAdornerLayer(adorned);
        if (layer is null) return;
        _activeAdorner = new DropCueAdorner(adorned, after, highlight);
        _activeAdornerLayer = layer;
        layer.Add(_activeAdorner);
    }

    private static void ClearAdorner()
    {
        if (_activeAdorner is not null && _activeAdornerLayer is not null)
            _activeAdornerLayer.Remove(_activeAdorner);
        _activeAdorner = null;
        _activeAdornerLayer = null;
    }

    private static void ResetPressedItem()
    {
        if (_sourceList is not null && GetSuppressSelectionOnDrag(_sourceList) &&
            ReferenceEquals(Mouse.Captured, _sourceList))
            Mouse.Capture(null);
        _pressedItem = null;
        _sourceList = null;
        _sourceContainer = null;
        _dragStarted = false;
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private static T? FindAncestor<T>(DependencyObject child) where T : DependencyObject
    {
        for (DependencyObject? current = child; current is not null;)
        {
            if (current is T match) return match;
            current = GetParent(current);
        }

        return null;
    }

    private static DependencyObject? GetParent(DependencyObject current) =>
        current is Visual || current is System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetParent(current)
            : LogicalTreeHelper.GetParent(current);

    private sealed record DragPayload(ReorderItemKind Kind, object Item);
    private sealed record RealizedItem(ListBoxItem Container, object Item, int Index, double Top, double Bottom)
    {
        public double Center => Top + (Bottom - Top) / 2;
    }

    private sealed record DropPosition(
        ListBoxItem? Container,
        object? Item,
        int InsertionIndex,
        bool PlaceAfter,
        bool IsValid);

    private sealed class DropCueAdorner : Adorner
    {
        public DropCueAdorner(UIElement adornedElement, bool after, bool highlight) : base(adornedElement)
        {
            After = after;
            Highlight = highlight;
            IsHitTestVisible = false;
        }

        public bool After { get; }
        public bool Highlight { get; }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);
            var brush = Application.Current?.TryFindResource("AccentBrush") as Brush ?? Brushes.Transparent;
            if (Highlight)
            {
                drawingContext.DrawRoundedRectangle(null, new Pen(brush, 2),
                    new Rect(1, 1, Math.Max(0, AdornedElement.RenderSize.Width - 2),
                        Math.Max(0, AdornedElement.RenderSize.Height - 2)), 7, 7);
                return;
            }
            var y = After ? AdornedElement.RenderSize.Height - 1 : 1;
            drawingContext.DrawLine(new Pen(brush, 3), new Point(4, y),
                new Point(Math.Max(4, AdornedElement.RenderSize.Width - 4), y));
        }
    }
}
