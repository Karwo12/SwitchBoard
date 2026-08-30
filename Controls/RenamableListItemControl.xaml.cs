using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace SwitchBoard.Controls;

public partial class RenamableListItemControl : UserControl
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(RenamableListItemControl),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty EditTextProperty = DependencyProperty.Register(
        nameof(EditText),
        typeof(string),
        typeof(RenamableListItemControl),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty IsEditingProperty = DependencyProperty.Register(
        nameof(IsEditing),
        typeof(bool),
        typeof(RenamableListItemControl),
        new PropertyMetadata(false, OnIsEditingChanged));

    public static readonly DependencyProperty BeginEditCommandProperty = DependencyProperty.Register(
        nameof(BeginEditCommand),
        typeof(ICommand),
        typeof(RenamableListItemControl));

    public static readonly DependencyProperty CommitEditCommandProperty = DependencyProperty.Register(
        nameof(CommitEditCommand),
        typeof(ICommand),
        typeof(RenamableListItemControl));

    public static readonly DependencyProperty CancelEditCommandProperty = DependencyProperty.Register(
        nameof(CancelEditCommand),
        typeof(ICommand),
        typeof(RenamableListItemControl));

    public static readonly DependencyProperty CommandParameterProperty = DependencyProperty.Register(
        nameof(CommandParameter),
        typeof(object),
        typeof(RenamableListItemControl));

    public RenamableListItemControl()
    {
        InitializeComponent();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public string EditText
    {
        get => (string)GetValue(EditTextProperty);
        set => SetValue(EditTextProperty, value);
    }

    public bool IsEditing
    {
        get => (bool)GetValue(IsEditingProperty);
        set => SetValue(IsEditingProperty, value);
    }

    public ICommand? BeginEditCommand
    {
        get => (ICommand?)GetValue(BeginEditCommandProperty);
        set => SetValue(BeginEditCommandProperty, value);
    }

    public ICommand? CommitEditCommand
    {
        get => (ICommand?)GetValue(CommitEditCommandProperty);
        set => SetValue(CommitEditCommandProperty, value);
    }

    public ICommand? CancelEditCommand
    {
        get => (ICommand?)GetValue(CancelEditCommandProperty);
        set => SetValue(CancelEditCommandProperty, value);
    }

    public object? CommandParameter
    {
        get => GetValue(CommandParameterProperty);
        set => SetValue(CommandParameterProperty, value);
    }

    private static void OnIsEditingChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        if (dependencyObject is RenamableListItemControl control && e.NewValue is true)
        {
            control.FocusEditor();
        }
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || IsEditing || BeginEditCommand?.CanExecute(CommandParameter) != true)
        {
            return;
        }

        BeginEditCommand.Execute(CommandParameter);
        e.Handled = true;
        FocusEditor();
    }

    private void OnEditorPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CommitEdit();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CancelEdit();
            e.Handled = true;
        }
    }

    private void OnEditorLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (IsEditing)
        {
            CommitEdit();
        }
    }

    private void CommitEdit()
    {
        Editor.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        if (CommitEditCommand?.CanExecute(CommandParameter) == true)
        {
            CommitEditCommand.Execute(CommandParameter);
        }

        MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        if (!IsEditing) Keyboard.ClearFocus();
    }

    private void CancelEdit()
    {
        if (CancelEditCommand?.CanExecute(CommandParameter) == true)
        {
            CancelEditCommand.Execute(CommandParameter);
        }

        MoveFocus(new TraversalRequest(FocusNavigationDirection.Next));
        if (!IsEditing) Keyboard.ClearFocus();
    }

    private void FocusEditor()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!IsEditing)
            {
                return;
            }

            Editor.Focus();
            Editor.SelectAll();
        }, DispatcherPriority.Input);
    }
}
