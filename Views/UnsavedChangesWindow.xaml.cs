using System.Windows;

namespace SwitchBoard.Views;

public enum UnsavedChangesChoice { Save, Discard, Cancel }

public partial class UnsavedChangesWindow : Window
{
    public UnsavedChangesChoice Choice { get; private set; } = UnsavedChangesChoice.Cancel;

    public UnsavedChangesWindow(string title, string message, string save, string discard, string cancel)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
        SaveButton.Content = save;
        DiscardButton.Content = discard;
        CancelButton.Content = cancel;
    }

    private void Save_OnClick(object sender, RoutedEventArgs e) => Finish(UnsavedChangesChoice.Save);
    private void Discard_OnClick(object sender, RoutedEventArgs e) => Finish(UnsavedChangesChoice.Discard);
    private void Cancel_OnClick(object sender, RoutedEventArgs e) => Finish(UnsavedChangesChoice.Cancel);

    private void Finish(UnsavedChangesChoice choice)
    {
        Choice = choice;
        DialogResult = choice != UnsavedChangesChoice.Cancel;
        Close();
    }
}
