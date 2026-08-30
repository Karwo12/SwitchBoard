using System.Collections.Specialized;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using SwitchBoard.Controls;

namespace SwitchBoard.Views.Panels;

public partial class PerformancePanel : UserControl
{
    private const string ProcessIconTag = "PerformanceProcessIcon";
    private bool _iconLayoutPending;
    private bool _processItemsHandlersAttached;
    private readonly EventHandler _processItemsStatusChangedHandler;
    private readonly NotifyCollectionChangedEventHandler _processItemsChangedHandler;

    public PerformancePanel()
    {
        InitializeComponent();
        _processItemsStatusChangedHandler = ProcessItemsOnStatusChanged;
        _processItemsChangedHandler = ProcessItemsOnCollectionChanged;
        Loaded += ProcessPanelOnLoaded;
        Unloaded += ProcessPanelOnUnloaded;
    }

    public void ScrollToTop() => ContentScrollViewer.ScrollToTop();

    private void ProcessPanelOnLoaded(object sender, RoutedEventArgs e)
    {
        AttachProcessItemsHandlers();
        ScheduleIconLayout();
    }

    private void ProcessPanelOnUnloaded(object sender, RoutedEventArgs e)
    {
        DetachProcessItemsHandlers();
    }

    private void ProcessItemsOnStatusChanged(object? sender, EventArgs e) => ScheduleIconLayout();

    private void ProcessItemsOnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => ScheduleIconLayout();

    private void AttachProcessItemsHandlers()
    {
        if (_processItemsHandlersAttached) return;
        ProcessItems.ItemContainerGenerator.StatusChanged += _processItemsStatusChangedHandler;
        ((INotifyCollectionChanged)ProcessItems.Items).CollectionChanged += _processItemsChangedHandler;
        _processItemsHandlersAttached = true;
    }

    private void DetachProcessItemsHandlers()
    {
        if (!_processItemsHandlersAttached) return;
        ProcessItems.ItemContainerGenerator.StatusChanged -= _processItemsStatusChangedHandler;
        ((INotifyCollectionChanged)ProcessItems.Items).CollectionChanged -= _processItemsChangedHandler;
        _processItemsHandlersAttached = false;
    }

    private void ScheduleIconLayout()
    {
        if (_iconLayoutPending || !IsLoaded) return;
        _iconLayoutPending = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() =>
        {
            _iconLayoutPending = false;
            foreach (var rowCard in FindVisualChildren<CardSurfaceControl>(ProcessItems)) AddIconElementsToProcessRow(rowCard);
        }));
    }

    private static void AddIconElementsToProcessRow(CardSurfaceControl rowCard)
    {
        foreach (var stack in FindVisualChildren<StackPanel>(rowCard))
        {
            if (stack.Children.Count < 3) continue;
            if (stack.Children[0] is not Border || stack.Children[1] is not Button) continue;

            ConfigureProcessExpander((Button)stack.Children[1], rowCard);
            if (stack.Children.OfType<FrameworkElement>().Any(element => Equals(element.Tag, ProcessIconTag))
                || stack.Children[^1] is not TextBlock) continue;

            var image = new Image { Tag = ProcessIconTag, Width = 20, Height = 20, Margin = new Thickness(0, 0, 6, 0), Stretch = Stretch.Uniform };
            image.SetBinding(Image.SourceProperty, new Binding("Icon"));
            image.SetBinding(VisibilityProperty, new Binding("HasIcon") { Converter = new BooleanToVisibilityConverter() });

            var systemFallback = CreateFallbackIcon(rowCard, "M2,2 H14 V14 H2 Z M4,5 L6,7 L4,9 M7,9 H11");
            var applicationFallback = CreateFallbackIcon(rowCard, "M3,2 H13 V14 H3 Z M5,5 H11 M5,8 H11 M5,11 H9");
            SetFallbackVisibility(systemFallback, "system");
            SetFallbackVisibility(applicationFallback, "application");

            var nameIndex = stack.Children.Count - 1;
            stack.Children.Insert(nameIndex, image);
            stack.Children.Insert(nameIndex + 1, systemFallback);
            stack.Children.Insert(nameIndex + 2, applicationFallback);
        }
    }

    private static Path CreateFallbackIcon(CardSurfaceControl rowCard, string geometry)
    {
        return new Path
        {
            Tag = ProcessIconTag,
            Width = 16,
            Height = 16,
            Margin = new Thickness(0, 0, 6, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Data = Geometry.Parse(geometry),
            Fill = Brushes.Transparent,
            Stroke = (Brush)rowCard.FindResource("TextSecondaryBrush"),
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeThickness = 1.15
        };
    }

    private static void SetFallbackVisibility(Path icon, string kind)
    {
        var binding = new MultiBinding { Converter = new FallbackIconVisibilityConverter(), ConverterParameter = kind };
        binding.Bindings.Add(new Binding("HasIcon"));
        binding.Bindings.Add(new Binding("IsSystemProcess"));
        icon.SetBinding(VisibilityProperty, binding);
    }

    private static void ConfigureProcessExpander(Button expander, CardSurfaceControl rowCard)
    {
        expander.Width = 18;
        expander.Height = 18;
        expander.Padding = new Thickness(0);
        expander.Background = Brushes.Transparent;
        expander.BorderThickness = new Thickness(0);
        expander.SetBinding(VisibilityProperty, new Binding("IsGroup") { Converter = new BooleanToVisibilityConverter() });
        expander.Click -= ProcessExpanderClick;
        expander.Click += ProcessExpanderClick;

        var chevron = new Path
        {
            Width = 9,
            Height = 9,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Data = Geometry.Parse("M2,1 L7,5 L2,9"),
            Fill = Brushes.Transparent,
            Stroke = (Brush)rowCard.FindResource("TextSecondaryBrush"),
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeThickness = 1.35
        };
        var chevronStyle = new Style(typeof(Path));
        var expandedTrigger = new DataTrigger
        {
            Binding = new Binding("DataContext.IsExpanded")
            {
                RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(Button), 1)
            },
            Value = true
        };
        expandedTrigger.Setters.Add(new Setter(RenderTransformProperty, new RotateTransform(90, 4.5, 5)));
        chevronStyle.Triggers.Add(expandedTrigger);
        chevron.Style = chevronStyle;
        if (expander.Content is not Path) expander.Content = chevron;
    }

    private static void ProcessExpanderClick(object sender, RoutedEventArgs e) => e.Handled = true;

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    private sealed class InverseBooleanToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => value is bool hasIcon && !hasIcon ? Visibility.Visible : Visibility.Collapsed;
        public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
    }

    private sealed class FallbackIconVisibilityConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        {
            var hasIcon = values.Length > 0 && values[0] is bool value && value;
            var isSystem = values.Length > 1 && values[1] is bool value2 && value2;
            var wantsSystem = string.Equals(parameter as string, "system", StringComparison.Ordinal);
            return !hasIcon && isSystem == wantsSystem ? Visibility.Visible : Visibility.Collapsed;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, System.Globalization.CultureInfo culture) => throw new NotSupportedException();
    }
}
