using System.Collections.ObjectModel;
using System.ComponentModel;
using SwitchBoard.Data;
using SwitchBoard.Localization;
using SwitchBoard.Models.Actions;
using SwitchBoard.Models.Categories;
using SwitchBoard.Models.Profiles;
using SwitchBoard.Services;
using SwitchBoard.Services.Persistence;
using SwitchBoard.Services.Profiles;
using SwitchBoard.Themes;

namespace SwitchBoard.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly IProfileCatalogService _catalogService;
    private readonly IUserDialogService _dialogService;
    private readonly ILocalizationService _localizationService;
    private readonly IThemeManager _themeManager;
    private readonly ISettingsRepository _settingsRepository;
    private readonly UserSettings _userSettings;
    private readonly List<ProfileItemViewModel> _allProfiles;
    private CategoryItemViewModel? _selectedCategory;
    private ProfileItemViewModel? _selectedProfile;
    private ActionTypeOption? _selectedActionType;
    private ThemeOptionViewModel? _selectedThemeOption;
    private LanguageOptionViewModel? _selectedLanguageOption;
    private string _statusMessage;
    private bool _hasUnsavedChanges;
    private bool _isThemeMenuOpen;
    private bool _closeSwitchBoardAfterProfileFinishes;

    public MainWindowViewModel(
        IProfileCatalogService catalogService,
        IUserDialogService dialogService,
        SwitchBoardCatalog catalog,
        IThemeManager themeManager,
        ILocalizationService localizationService,
        ISettingsRepository settingsRepository,
        UserSettings userSettings)
    {
        _catalogService = catalogService;
        _dialogService = dialogService;
        _themeManager = themeManager;
        _localizationService = localizationService;
        _settingsRepository = settingsRepository;
        _userSettings = userSettings;
        _statusMessage = localizationService.GetString("Status.Ready");
        _closeSwitchBoardAfterProfileFinishes = userSettings.CloseSwitchBoardAfterProfileFinishes;

        Categories = new ObservableCollection<CategoryItemViewModel>(
            catalog.Categories
                .OrderBy(category => category.SortOrder)
                .Select(category => new CategoryItemViewModel(category)));

        _allProfiles = catalog.Profiles
            .OrderBy(profile => profile.SortOrder)
            .Select(profile => new ProfileItemViewModel(profile, localizationService))
            .ToList();

        Profiles = [];
        AvailableActionTypes =
        [
            new(ActionTypeIds.ProcessSetState, "Action.ProcessState", localizationService),
            new(ActionTypeIds.ProgramRun, "Action.RunProgram", localizationService),
            new(ActionTypeIds.SteamLaunch, "Action.LaunchSteamGame", localizationService),
            new(ActionTypeIds.ServiceSetState, "Action.WindowsServiceState", localizationService),
            new(ActionTypeIds.DisplayConfigure, "Action.DisplayConfiguration", localizationService),
            new(ActionTypeIds.PowerSetPlan, "Action.PowerPlan", localizationService),
            new(ActionTypeIds.ScriptRun, "Action.RunScript", localizationService),
            new(ActionTypeIds.Delay, "Action.Delay", localizationService)
        ];
        _selectedActionType = AvailableActionTypes[0];

        ThemeOptions = new ObservableCollection<ThemeOptionViewModel>(
            themeManager.AvailableThemes.Select(theme => new ThemeOptionViewModel(theme, localizationService)));
        _selectedThemeOption = ThemeOptions.First(option =>
            string.Equals(option.Id, themeManager.CurrentThemeId, StringComparison.OrdinalIgnoreCase));

        LanguageOptions = new ObservableCollection<LanguageOptionViewModel>(
            localizationService.AvailableLanguages.Select(language =>
                new LanguageOptionViewModel(language, localizationService)));
        _selectedLanguageOption = LanguageOptions.First(option =>
            string.Equals(option.Id, localizationService.CurrentLanguageId, StringComparison.OrdinalIgnoreCase));

        AddCategoryCommand = new RelayCommand(AddCategory);
        DeleteCategoryCommand = new RelayCommand<CategoryItemViewModel>(DeleteCategory, category => category is not null);
        AddProfileCommand = new RelayCommand(AddProfile, () => SelectedCategory is not null);
        DeleteProfileCommand = new RelayCommand<ProfileItemViewModel>(DeleteProfile, profile => profile is not null);
        AddActionCommand = new RelayCommand(AddAction, () => SelectedProfile is not null && SelectedActionType is not null);
        DeleteActionCommand = new RelayCommand<ActionItemViewModel>(DeleteAction, action => action is not null);
        MoveActionUpCommand = new RelayCommand<ActionItemViewModel>(MoveActionUp, CanMoveActionUp);
        MoveActionDownCommand = new RelayCommand<ActionItemViewModel>(MoveActionDown, CanMoveActionDown);
        BeginCategoryRenameCommand = new RelayCommand<CategoryItemViewModel>(category => category?.BeginEdit());
        CommitCategoryRenameCommand = new RelayCommand<CategoryItemViewModel>(category => category?.CommitEdit());
        CancelCategoryRenameCommand = new RelayCommand<CategoryItemViewModel>(category => category?.CancelEdit());
        BeginProfileRenameCommand = new RelayCommand<ProfileItemViewModel>(profile => profile?.BeginEdit());
        CommitProfileRenameCommand = new RelayCommand<ProfileItemViewModel>(profile => profile?.CommitEdit());
        CancelProfileRenameCommand = new RelayCommand<ProfileItemViewModel>(profile => profile?.CancelEdit());
        ToggleThemeMenuCommand = new RelayCommand(() => IsThemeMenuOpen = !IsThemeMenuOpen);
        SaveCommand = new AsyncRelayCommand(SaveAsync);

        SubscribeToItems();
        SelectedCategory = Categories.FirstOrDefault();
        SetClean(_localizationService.GetString("Status.CatalogLoaded"));
    }

    public ObservableCollection<CategoryItemViewModel> Categories { get; }

    public ObservableCollection<ProfileItemViewModel> Profiles { get; }

    public IReadOnlyList<ActionTypeOption> AvailableActionTypes { get; }

    public ObservableCollection<ThemeOptionViewModel> ThemeOptions { get; }

    public ObservableCollection<LanguageOptionViewModel> LanguageOptions { get; }

    public CategoryItemViewModel? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (!SetProperty(ref _selectedCategory, value))
            {
                return;
            }

            RefreshProfiles();
            AddProfileCommand.NotifyCanExecuteChanged();
        }
    }

    public ProfileItemViewModel? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetProperty(ref _selectedProfile, value))
            {
                AddActionCommand.NotifyCanExecuteChanged();
                NotifyActionCommandStates();
            }
        }
    }

    public ActionTypeOption? SelectedActionType
    {
        get => _selectedActionType;
        set
        {
            if (SetProperty(ref _selectedActionType, value))
            {
                AddActionCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public ThemeOptionViewModel? SelectedThemeOption
    {
        get => _selectedThemeOption;
        set
        {
            if (SetProperty(ref _selectedThemeOption, value) && value is not null)
            {
                _ = ChangeThemeAsync(value);
            }
        }
    }

    public LanguageOptionViewModel? SelectedLanguageOption
    {
        get => _selectedLanguageOption;
        set
        {
            if (SetProperty(ref _selectedLanguageOption, value) && value is not null)
            {
                _ = ChangeLanguageAsync(value);
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public bool HasUnsavedChanges
    {
        get => _hasUnsavedChanges;
        private set => SetProperty(ref _hasUnsavedChanges, value);
    }

    public bool IsThemeMenuOpen
    {
        get => _isThemeMenuOpen;
        set => SetProperty(ref _isThemeMenuOpen, value);
    }

    public bool CloseSwitchBoardAfterProfileFinishes
    {
        get => _closeSwitchBoardAfterProfileFinishes;
        set
        {
            if (!SetProperty(ref _closeSwitchBoardAfterProfileFinishes, value))
            {
                return;
            }

            _userSettings.CloseSwitchBoardAfterProfileFinishes = value;
            _ = SaveBehaviorSettingAsync(value);
        }
    }

    public RelayCommand AddCategoryCommand { get; }

    public RelayCommand<CategoryItemViewModel> DeleteCategoryCommand { get; }

    public RelayCommand AddProfileCommand { get; }

    public RelayCommand<ProfileItemViewModel> DeleteProfileCommand { get; }

    public RelayCommand AddActionCommand { get; }

    public RelayCommand<ActionItemViewModel> DeleteActionCommand { get; }

    public RelayCommand<ActionItemViewModel> MoveActionUpCommand { get; }

    public RelayCommand<ActionItemViewModel> MoveActionDownCommand { get; }

    public RelayCommand<CategoryItemViewModel> BeginCategoryRenameCommand { get; }

    public RelayCommand<CategoryItemViewModel> CommitCategoryRenameCommand { get; }

    public RelayCommand<CategoryItemViewModel> CancelCategoryRenameCommand { get; }

    public RelayCommand<ProfileItemViewModel> BeginProfileRenameCommand { get; }

    public RelayCommand<ProfileItemViewModel> CommitProfileRenameCommand { get; }

    public RelayCommand<ProfileItemViewModel> CancelProfileRenameCommand { get; }

    public RelayCommand ToggleThemeMenuCommand { get; }

    public AsyncRelayCommand SaveCommand { get; }

    private void AddCategory()
    {
        var category = new CategoryItemViewModel(new CategoryDefinition
        {
            Id = Guid.NewGuid(),
            Name = CreateUniqueName(
                _localizationService.GetString("Default.NewCategory"),
                Categories.Select(item => item.Name)),
            SortOrder = Categories.Count
        });
        Subscribe(category);
        Categories.Add(category);
        SelectedCategory = category;
        category.BeginEdit();
        MarkDirty(_localizationService.GetString("Status.CategoryCreated"));
    }

    private void DeleteCategory(CategoryItemViewModel? category)
    {
        if (category is null || !_dialogService.Confirm(
                _localizationService.GetString("Dialog.DeleteCategoryTitle"),
                _localizationService.Format("Dialog.DeleteCategoryMessage", category.Name)))
        {
            return;
        }

        var removedProfiles = _allProfiles.Where(profile => profile.CategoryId == category.Id).ToList();
        foreach (var profile in removedProfiles)
        {
            _allProfiles.Remove(profile);
        }

        var oldIndex = Categories.IndexOf(category);
        Categories.Remove(category);
        SelectedCategory = Categories.Count == 0
            ? null
            : Categories[Math.Min(oldIndex, Categories.Count - 1)];
        MarkDirty(_localizationService.GetString("Status.CategoryDeleted"));
    }

    private void AddProfile()
    {
        if (SelectedCategory is null)
        {
            return;
        }

        var profilesInCategory = _allProfiles.Where(profile => profile.CategoryId == SelectedCategory.Id).ToList();
        var profile = new ProfileItemViewModel(new ProfileDefinition
        {
            Id = Guid.NewGuid(),
            CategoryId = SelectedCategory.Id,
            Name = CreateUniqueName(
                _localizationService.GetString("Default.NewProfile"),
                profilesInCategory.Select(item => item.Name)),
            SortOrder = profilesInCategory.Count
        }, _localizationService);
        Subscribe(profile);
        _allProfiles.Add(profile);
        Profiles.Add(profile);
        SelectedProfile = profile;
        profile.BeginEdit();
        MarkDirty(_localizationService.GetString("Status.ProfileCreated"));
    }

    private void DeleteProfile(ProfileItemViewModel? profile)
    {
        if (profile is null || !_dialogService.Confirm(
                _localizationService.GetString("Dialog.DeleteProfileTitle"),
                _localizationService.Format("Dialog.DeleteProfileMessage", profile.Name)))
        {
            return;
        }

        var oldIndex = Profiles.IndexOf(profile);
        Profiles.Remove(profile);
        _allProfiles.Remove(profile);
        SelectedProfile = Profiles.Count == 0
            ? null
            : Profiles[Math.Min(oldIndex, Profiles.Count - 1)];
        MarkDirty(_localizationService.GetString("Status.ProfileDeleted"));
    }

    private void AddAction()
    {
        if (SelectedProfile is null || SelectedActionType is null)
        {
            return;
        }

        var action = new ActionItemViewModel(new ActionDefinition
        {
            Id = Guid.NewGuid(),
            Type = SelectedActionType.TypeId,
            Name = SelectedActionType.DisplayName,
            ActionSchemaVersion = 1,
            IsEnabled = true
        }, _localizationService);
        Subscribe(action);
        SelectedProfile.Actions.Add(action);
        NotifyActionCommandStates();
        MarkDirty(_localizationService.GetString("Status.ActionAdded"));
    }

    private void DeleteAction(ActionItemViewModel? action)
    {
        if (SelectedProfile is null || action is null)
        {
            return;
        }

        SelectedProfile.Actions.Remove(action);
        NotifyActionCommandStates();
        MarkDirty(_localizationService.GetString("Status.ActionRemoved"));
    }

    private void MoveActionUp(ActionItemViewModel? action)
    {
        if (SelectedProfile is null || action is null)
        {
            return;
        }

        var index = SelectedProfile.Actions.IndexOf(action);
        if (index > 0)
        {
            SelectedProfile.Actions.Move(index, index - 1);
            NotifyActionCommandStates();
            MarkDirty(_localizationService.GetString("Status.ActionOrderChanged"));
        }
    }

    private void MoveActionDown(ActionItemViewModel? action)
    {
        if (SelectedProfile is null || action is null)
        {
            return;
        }

        var index = SelectedProfile.Actions.IndexOf(action);
        if (index >= 0 && index < SelectedProfile.Actions.Count - 1)
        {
            SelectedProfile.Actions.Move(index, index + 1);
            NotifyActionCommandStates();
            MarkDirty(_localizationService.GetString("Status.ActionOrderChanged"));
        }
    }

    private bool CanMoveActionUp(ActionItemViewModel? action) =>
        SelectedProfile is not null && action is not null && SelectedProfile.Actions.IndexOf(action) > 0;

    private bool CanMoveActionDown(ActionItemViewModel? action) =>
        SelectedProfile is not null &&
        action is not null &&
        SelectedProfile.Actions.IndexOf(action) is var index &&
        index >= 0 &&
        index < SelectedProfile.Actions.Count - 1;

    private async Task SaveAsync()
    {
        try
        {
            NormalizeSortOrders();
            var catalog = new SwitchBoardCatalog
            {
                SchemaVersion = CatalogSchema.CurrentVersion,
                Categories = Categories.Select(category => category.ToModel()).ToList(),
                Profiles = _allProfiles.Select(profile => profile.ToModel()).ToList()
            };

            await _catalogService.SaveAsync(catalog);
            SetClean(_localizationService.Format("Status.Saved", DateTime.Now.ToString("HH:mm:ss")));
        }
        catch (InvalidOperationException)
        {
            StatusMessage = _localizationService.GetString("Status.InvalidCatalog");
        }
        catch (Exception exception)
        {
            StatusMessage = _localizationService.Format("Status.SaveFailed", exception.Message);
        }
    }

    private async Task ChangeThemeAsync(ThemeOptionViewModel? option)
    {
        if (option is null)
        {
            return;
        }

        var appliedThemeId = _themeManager.ApplyTheme(option.Id);
        _userSettings.SchemaVersion = SettingsSchema.CurrentVersion;
        _userSettings.ThemeId = appliedThemeId;

        try
        {
            await _settingsRepository.SaveAsync(_userSettings);
            StatusMessage = _localizationService.Format("Status.ThemeChanged", option.DisplayName);
        }
        catch (Exception exception)
        {
            StatusMessage = _localizationService.Format("Status.SettingsSaveFailed", exception.Message);
        }
    }

    private async Task ChangeLanguageAsync(LanguageOptionViewModel option)
    {
        var appliedLanguageId = _localizationService.ApplyLanguage(option.Id);
        _userSettings.SchemaVersion = SettingsSchema.CurrentVersion;
        _userSettings.LanguageId = appliedLanguageId;

        foreach (var actionType in AvailableActionTypes)
        {
            actionType.RefreshDisplayName();
        }

        foreach (var action in _allProfiles.SelectMany(profile => profile.Actions))
        {
            action.RefreshDisplayName();
        }

        foreach (var themeOption in ThemeOptions)
        {
            themeOption.RefreshDisplayName();
        }

        foreach (var languageOption in LanguageOptions)
        {
            languageOption.RefreshDisplayName();
        }

        try
        {
            await _settingsRepository.SaveAsync(_userSettings);
            StatusMessage = _localizationService.Format("Status.LanguageChanged", option.DisplayName);
        }
        catch (Exception exception)
        {
            StatusMessage = _localizationService.Format("Status.SettingsSaveFailed", exception.Message);
        }
    }

    private async Task SaveBehaviorSettingAsync(bool isEnabled)
    {
        _userSettings.SchemaVersion = SettingsSchema.CurrentVersion;
        try
        {
            await _settingsRepository.SaveAsync(_userSettings);
            StatusMessage = _localizationService.GetString(
                isEnabled ? "Status.BehaviorEnabled" : "Status.BehaviorDisabled");
        }
        catch (Exception exception)
        {
            StatusMessage = _localizationService.Format("Status.SettingsSaveFailed", exception.Message);
        }
    }

    private void RefreshProfiles()
    {
        Profiles.Clear();
        if (SelectedCategory is not null)
        {
            foreach (var profile in _allProfiles
                         .Where(profile => profile.CategoryId == SelectedCategory.Id)
                         .OrderBy(profile => profile.SortOrder))
            {
                Profiles.Add(profile);
            }
        }

        SelectedProfile = Profiles.FirstOrDefault();
    }

    private void NormalizeSortOrders()
    {
        for (var categoryIndex = 0; categoryIndex < Categories.Count; categoryIndex++)
        {
            Categories[categoryIndex].SortOrder = categoryIndex;
        }

        foreach (var category in Categories)
        {
            var profiles = _allProfiles
                .Where(profile => profile.CategoryId == category.Id)
                .OrderBy(profile => profile.SortOrder)
                .ToList();
            for (var profileIndex = 0; profileIndex < profiles.Count; profileIndex++)
            {
                profiles[profileIndex].SortOrder = profileIndex;
            }
        }
    }

    private void SubscribeToItems()
    {
        foreach (var category in Categories)
        {
            Subscribe(category);
        }

        foreach (var profile in _allProfiles)
        {
            Subscribe(profile);
            foreach (var action in profile.Actions)
            {
                Subscribe(action);
            }
        }
    }

    private void Subscribe(ObservableObject item) => item.PropertyChanged += ItemOnPropertyChanged;

    private void ItemOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(CategoryItemViewModel.IsEditing) or
            nameof(CategoryItemViewModel.EditName))
        {
            return;
        }

        MarkDirty(_localizationService.GetString("Status.UnsavedChanges"));
    }

    private void MarkDirty(string message)
    {
        HasUnsavedChanges = true;
        StatusMessage = message;
    }

    private void SetClean(string message)
    {
        HasUnsavedChanges = false;
        StatusMessage = message;
    }

    private void NotifyActionCommandStates()
    {
        MoveActionUpCommand.NotifyCanExecuteChanged();
        MoveActionDownCommand.NotifyCanExecuteChanged();
    }

    private static string CreateUniqueName(string baseName, IEnumerable<string> existingNames)
    {
        var names = existingNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!names.Contains(baseName))
        {
            return baseName;
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{baseName} {suffix}";
            if (!names.Contains(candidate))
            {
                return candidate;
            }
        }
    }
}
