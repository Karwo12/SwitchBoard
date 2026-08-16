using SwitchBoard.Localization;

namespace SwitchBoard.ViewModels;

public sealed class ActionTypeOption(
    string typeId,
    string displayNameResourceKey,
    ILocalizationService localizationService) : ObservableObject
{
    private string _displayName = localizationService.GetString(displayNameResourceKey);

    public string TypeId { get; } = typeId;

    public string DisplayName
    {
        get => _displayName;
        private set => SetProperty(ref _displayName, value);
    }

    public void RefreshDisplayName() =>
        DisplayName = localizationService.GetString(displayNameResourceKey);
}
