using SwitchBoard.Localization;

namespace SwitchBoard.ViewModels;

public sealed class LocalizedValueOptionViewModel(
    string value,
    string displayNameResourceKey,
    ILocalizationService localizationService) : ObservableObject
{
    private string _displayName = localizationService.GetString(displayNameResourceKey);

    public string Value { get; } = value;

    public string DisplayName
    {
        get => _displayName;
        private set => SetProperty(ref _displayName, value);
    }

    public void RefreshDisplayName() =>
        DisplayName = localizationService.GetString(displayNameResourceKey);

    public override string ToString() => DisplayName;
}