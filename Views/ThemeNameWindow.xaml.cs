using System.Windows;
using SwitchBoard.Localization;

namespace SwitchBoard.Views;

public partial class ThemeNameWindow : Window
{
    private readonly IReadOnlyCollection<string> _unavailableNames;
    private readonly ILocalizationService _localization;

    public ThemeNameWindow(string currentName, IReadOnlyCollection<string> unavailableNames,
        ILocalizationService localization)
    {
        InitializeComponent();
        _unavailableNames = unavailableNames;
        _localization = localization;
        NameBox.Text = currentName;
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    public string? Result { get; private set; }

    private void Save_OnClick(object sender, RoutedEventArgs e)
    {
        var candidate = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            ErrorText.Text = _localization.GetString("CustomTheme.NameRequired");
            return;
        }
        if (_unavailableNames.Any(item => string.Equals(item.Trim(), candidate, StringComparison.CurrentCultureIgnoreCase)))
        {
            ErrorText.Text = _localization.GetString("CustomTheme.DuplicateName");
            return;
        }
        Result = candidate;
        DialogResult = true;
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => Close();
}
