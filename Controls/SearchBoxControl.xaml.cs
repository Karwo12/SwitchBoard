using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace SwitchBoard.Controls;

public partial class SearchBoxControl : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(SearchBoxControl),
        new FrameworkPropertyMetadata(string.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnTextChanged));

    public static readonly DependencyProperty HasTextProperty = DependencyProperty.Register(
        nameof(HasText), typeof(bool), typeof(SearchBoxControl), new PropertyMetadata(false));

    public SearchBoxControl()
    {
        InitializeComponent();
        SearchTextBox.TextChanged += SearchTextBox_OnTextChanged;
        Loaded += SearchBoxControl_OnLoaded;
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value ?? string.Empty);
    }

    public bool HasText => (bool)GetValue(HasTextProperty);

    private static void OnTextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is SearchBoxControl control)
            control.SetValue(HasTextProperty, !string.IsNullOrEmpty(args.NewValue as string));
    }

    private void SearchBoxControl_OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyLeftTextAlignment();
        ResetHorizontalScrollIfTextFits();
    }

    private void SearchTextBox_OnTextChanged(object sender, TextChangedEventArgs e)
    {
        ApplyLeftTextAlignment();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ResetHorizontalScrollIfTextFits);
    }

    private void ApplyLeftTextAlignment()
    {
        SearchTextBox.HorizontalContentAlignment = HorizontalAlignment.Left;
        SearchTextBox.TextAlignment = TextAlignment.Left;
    }

    private void SearchTextBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter && e.Key != Key.Return) return;

        // The inner binding normally updates on every keystroke. Force the
        // source update as well so Enter has the same result for callers that
        // change the binding trigger or commit through a validation rule.
        SearchTextBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private void ResetHorizontalScrollIfTextFits()
    {
        if (!IsLoaded || SearchTextBox.ActualWidth <= 0) return;

        var availableWidth = SearchTextBox.ActualWidth - SearchTextBox.Padding.Left -
                             SearchTextBox.Padding.Right - 2;
        if (availableWidth <= 0) return;

        var formattedText = new FormattedText(
            SearchTextBox.Text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(SearchTextBox.FontFamily, SearchTextBox.FontStyle,
                SearchTextBox.FontWeight, SearchTextBox.FontStretch),
            SearchTextBox.FontSize,
            Brushes.Transparent,
            VisualTreeHelper.GetDpi(SearchTextBox).PixelsPerDip);

        if (formattedText.WidthIncludingTrailingWhitespace <= availableWidth)
            SearchTextBox.ScrollToHome();
    }

    private void ClearButton_OnClick(object sender, RoutedEventArgs e)
    {
        Text = string.Empty;
        SearchTextBox.Focus();
    }
}
