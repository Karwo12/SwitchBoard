using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SwitchBoard.Controls;

/// <summary>Commits a single-line text binding when the user confirms it with Enter.</summary>
public static class TextBoxEnterCommitBehavior
{
    public static readonly DependencyProperty CommitOnEnterProperty = DependencyProperty.RegisterAttached(
        "CommitOnEnter", typeof(bool), typeof(TextBoxEnterCommitBehavior),
        new PropertyMetadata(false, OnCommitOnEnterChanged));

    public static void SetCommitOnEnter(DependencyObject element, bool value) =>
        element.SetValue(CommitOnEnterProperty, value);

    public static bool GetCommitOnEnter(DependencyObject element) =>
        (bool)element.GetValue(CommitOnEnterProperty);

    private static void OnCommitOnEnterChanged(DependencyObject dependencyObject,
        DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is not TextBox textBox) return;
        textBox.PreviewKeyDown -= TextBox_OnPreviewKeyDown;
        if ((bool)e.NewValue) textBox.PreviewKeyDown += TextBox_OnPreviewKeyDown;
    }

    private static void TextBox_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox || (e.Key != Key.Enter && e.Key != Key.Return) ||
            textBox.AcceptsReturn || FindAncestor<ComboBox>(textBox) is not null)
            return;

        textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        Keyboard.ClearFocus();
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject child) where T : DependencyObject
    {
        for (DependencyObject? current = child; current is not null;)
        {
            if (current is T match) return match;
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }
        return null;
    }
}
