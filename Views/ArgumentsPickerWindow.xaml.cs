using System.Windows;
using SwitchBoard.Localization;

namespace SwitchBoard.Views;

public partial class ArgumentsPickerWindow : Window
{
    public string Result { get; private set; } = string.Empty;

    public ArgumentsPickerWindow(string initialArguments, ILocalizationService localizationService)
    {
        InitializeComponent();
        ArgumentsTextBox.Text = initialArguments;
        ArgumentsTextBox.SelectAll();
        ArgumentsTextBox.Focus();
    }

    private void Select_OnClick(object sender, RoutedEventArgs e)
    {
        Result = ArgumentsTextBox.Text;
        DialogResult = true;
    }
}
