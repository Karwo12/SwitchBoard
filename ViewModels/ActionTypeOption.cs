using SwitchBoard.Localization;

namespace SwitchBoard.ViewModels;

public sealed class ActionTypeOption(
    string typeId,
    string displayNameResourceKey,
    ILocalizationService localizationService,
    string categoryResourceKey = "ActionPicker.Category.Automation",
    params string[] keywords) : ObservableObject
{
    private string _displayName = localizationService.GetString(displayNameResourceKey);
    private string _category = localizationService.GetString(categoryResourceKey);

    public string TypeId { get; } = typeId;
    public string CategoryResourceKey { get; private set; } = categoryResourceKey;
    public IReadOnlyList<string> Keywords { get; } = keywords;
    public string Category => _category;

    public bool Matches(string query) => string.IsNullOrWhiteSpace(query) ||
        DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
        Keywords.Any(keyword => keyword.Contains(query, StringComparison.CurrentCultureIgnoreCase));

    public string DisplayName
    {
        get => _displayName;
        private set => SetProperty(ref _displayName, value);
    }

    public void RefreshDisplayName() =>
        DisplayName = localizationService.GetString(displayNameResourceKey);

    public void RefreshLocalization()
    {
        RefreshDisplayName();
        _category = localizationService.GetString(CategoryResourceKey);
        OnPropertyChanged(nameof(Category));
    }

    public void SetCategoryResourceKey(string resourceKey)
    {
        if (string.Equals(CategoryResourceKey, resourceKey, StringComparison.Ordinal)) return;
        CategoryResourceKey = resourceKey;
        _category = localizationService.GetString(resourceKey);
        OnPropertyChanged(nameof(Category));
    }

    public override string ToString() => DisplayName;
}
