namespace SwitchBoard.ViewModels;

public sealed class SettingsOptionViewModel(string value, string displayName) : ObservableObject
{
    public string Value { get; } = value;

    private string _displayName = displayName;

    public string DisplayName
    {
        get => _displayName;
        private set => SetProperty(ref _displayName, value);
    }

    public void RefreshDisplayName(string displayName) => DisplayName = displayName;
}
