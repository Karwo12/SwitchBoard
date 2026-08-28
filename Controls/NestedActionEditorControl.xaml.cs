using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SwitchBoard.ViewModels;

namespace SwitchBoard.Controls;

public partial class NestedActionEditorControl : UserControl
{
    public NestedActionEditorControl() => InitializeComponent();

    public static readonly DependencyProperty ActionProperty = DependencyProperty.Register(
        nameof(Action), typeof(ActionItemViewModel), typeof(NestedActionEditorControl));

    public static readonly DependencyProperty DeleteCommandProperty = DependencyProperty.Register(
        nameof(DeleteCommand), typeof(ICommand), typeof(NestedActionEditorControl));

    public static readonly DependencyProperty MoveUpCommandProperty = DependencyProperty.Register(
        nameof(MoveUpCommand), typeof(ICommand), typeof(NestedActionEditorControl));

    public static readonly DependencyProperty MoveDownCommandProperty = DependencyProperty.Register(
        nameof(MoveDownCommand), typeof(ICommand), typeof(NestedActionEditorControl));

    public ActionItemViewModel? Action
    {
        get => (ActionItemViewModel?)GetValue(ActionProperty);
        set => SetValue(ActionProperty, value);
    }

    public ICommand? DeleteCommand
    {
        get => (ICommand?)GetValue(DeleteCommandProperty);
        set => SetValue(DeleteCommandProperty, value);
    }

    public ICommand? MoveUpCommand
    {
        get => (ICommand?)GetValue(MoveUpCommandProperty);
        set => SetValue(MoveUpCommandProperty, value);
    }

    public ICommand? MoveDownCommand
    {
        get => (ICommand?)GetValue(MoveDownCommandProperty);
        set => SetValue(MoveDownCommandProperty, value);
    }
}
