using System.Windows;
using System.Windows.Controls;

namespace SwitchBoard.Controls.ActionEditors;

public partial class ActionAdvancedOptionsEditor : UserControl
{
    public static readonly DependencyProperty CommandHostProperty = DependencyProperty.Register(
        nameof(CommandHost), typeof(object), typeof(ActionAdvancedOptionsEditor));

    public ActionAdvancedOptionsEditor() => InitializeComponent();

    public object? CommandHost
    {
        get => GetValue(CommandHostProperty);
        set => SetValue(CommandHostProperty, value);
    }
}
