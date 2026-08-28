using SwitchBoard.Themes;
using SwitchBoard.Localization;

namespace SwitchBoard.ViewModels;

public sealed class ThemeOptionViewModel : ObservableObject
{
    private readonly ThemeDefinition? _definition;
    private readonly ILocalizationService _localizationService;
    private string _displayName;
    private bool _isActive;

    public ThemeOptionViewModel(ThemeDefinition definition, ILocalizationService localizationService)
    {
        _definition = definition;
        _localizationService = localizationService;
        Id = definition.Id;
        _displayName = localizationService.GetString(definition.DisplayNameResourceKey);
        IsBuiltIn = true;
    }

    public ThemeOptionViewModel(CustomThemeDefinition definition, ILocalizationService localizationService)
    {
        _localizationService = localizationService;
        Id = definition.Id;
        _displayName = definition.Name;
        IsBuiltIn = false;
    }

    public string Id { get; }
    public bool IsBuiltIn { get; }
    public bool IsCustom => !IsBuiltIn;
    public bool IsActive { get => _isActive; set => SetProperty(ref _isActive, value); }

    public string DisplayName
    {
        get => _displayName;
        set => SetProperty(ref _displayName, value);
    }

    public void RefreshDisplayName()
    {
        if (_definition is not null)
            DisplayName = _localizationService.GetString(_definition.DisplayNameResourceKey);
    }
}
