using SwitchBoard.Themes;
using SwitchBoard.Localization;

namespace SwitchBoard.ViewModels;

public sealed class ThemeOptionViewModel(
    ThemeDefinition definition,
    ILocalizationService localizationService) : ObservableObject
{
    private string _displayName = localizationService.GetString(definition.DisplayNameResourceKey);

    public string Id { get; } = definition.Id;

    public string DisplayName
    {
        get => _displayName;
        private set => SetProperty(ref _displayName, value);
    }

    public void RefreshDisplayName() =>
        DisplayName = localizationService.GetString(definition.DisplayNameResourceKey);
}
