using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using SwitchBoard.Data;
using SwitchBoard.Localization;
using SwitchBoard.Models.Actions;
using SwitchBoard.Models.Categories;
using SwitchBoard.Models.Execution;
using SwitchBoard.Models.Profiles;
using SwitchBoard.Services;
using SwitchBoard.Services.Execution;
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
    private readonly ProfileRunner _profileRunner;
    private readonly IProfileCompletionBehavior _profileCompletionBehavior;
    private readonly List<ProfileItemViewModel> _allProfiles;
    private CategoryItemViewModel? _selectedCategory;
    private ProfileItemViewModel? _selectedProfile;
    private ActionItemViewModel? _selectedAction;
    private ActionTypeOption? _selectedActionType;
    private ThemeOptionViewModel? _selectedThemeOption;
    private LanguageOptionViewModel? _selectedLanguageOption;
    private string _statusMessage;
    private bool _hasUnsavedChanges;
    private bool _isThemeMenuOpen;
    private bool _isProfileRunning;
    private bool _hasExecutionStatus;
    private int _currentExecutionActionNumber;
    private int _totalExecutionActions;
    private Guid? _currentExecutionActionId;
    private string _currentExecutionActionName = string.Empty;
    private string _executionStatusResourceKey = "Execution.Status.Pending";
    private string _executionStatusText = string.Empty;
    private string _executionErrorMessage = string.Empty;
    private ExecutionSession? _lastExecutionSession;
    private CancellationTokenSource? _profileExecutionCancellation;

    public MainWindowViewModel(
        IProfileCatalogService catalogService,
        IUserDialogService dialogService,
        SwitchBoardCatalog catalog,
        IThemeManager themeManager,
        ILocalizationService localizationService,
        ISettingsRepository settingsRepository,
        UserSettings userSettings,
        ProfileRunner profileRunner,
        IProfileCompletionBehavior profileCompletionBehavior)
    {
        _catalogService = catalogService;
        _dialogService = dialogService;
        _themeManager = themeManager;
        _localizationService = localizationService;
        _settingsRepository = settingsRepository;
        _userSettings = userSettings;
        _profileRunner = profileRunner;
        _profileCompletionBehavior = profileCompletionBehavior;
        _statusMessage = localizationService.GetString("Status.Ready");
        _executionStatusText = localizationService.GetString(_executionStatusResourceKey);

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
            new(ActionTypeIds.ServiceSetState, "Action.WindowsServiceState", localizationService),
            new(ActionTypeIds.DisplayConfigure, "Action.DisplaySettings", localizationService),
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
        BrowseProgramCommand = new RelayCommand<ActionItemViewModel>(BrowseProgram, action => action?.Type == ActionTypeIds.ProgramRun);
        FindProgramCommand = new RelayCommand<ActionItemViewModel>(FindProgram, action => action?.Type == ActionTypeIds.ProgramRun);
        SelectProcessCommand = new RelayCommand<ActionItemViewModel>(SelectProcess, action => action?.Type == ActionTypeIds.ProcessSetState);
        ToggleActionExpandedCommand = new RelayCommand<ActionItemViewModel>(ToggleActionExpanded, action => action is not null);
        RunProfileCommand = new AsyncRelayCommand(RunProfileAsync, CanRunProfile);
        CancelProfileCommand = new RelayCommand(CancelProfile, () => IsProfileRunning);
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
                SelectedAction = value?.Actions.FirstOrDefault();
                AddActionCommand.NotifyCanExecuteChanged();
                RunProfileCommand.NotifyCanExecuteChanged();
                NotifyActionCommandStates();
            }
        }
    }

    public ActionItemViewModel? SelectedAction
    {
        get => _selectedAction;
        set => SetProperty(ref _selectedAction, value);
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

    public bool IsProfileRunning
    {
        get => _isProfileRunning;
        private set
        {
            if (SetProperty(ref _isProfileRunning, value))
            {
                RunProfileCommand.NotifyCanExecuteChanged();
                CancelProfileCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasExecutionStatus
    {
        get => _hasExecutionStatus;
        private set => SetProperty(ref _hasExecutionStatus, value);
    }

    public int CurrentExecutionActionNumber
    {
        get => _currentExecutionActionNumber;
        private set => SetProperty(ref _currentExecutionActionNumber, value);
    }

    public int TotalExecutionActions
    {
        get => _totalExecutionActions;
        private set => SetProperty(ref _totalExecutionActions, value);
    }

    public string CurrentExecutionActionName
    {
        get => _currentExecutionActionName;
        private set => SetProperty(ref _currentExecutionActionName, value);
    }

    public string ExecutionStatusText
    {
        get => _executionStatusText;
        private set => SetProperty(ref _executionStatusText, value);
    }

    public string ExecutionErrorMessage
    {
        get => _executionErrorMessage;
        private set => SetProperty(ref _executionErrorMessage, value);
    }

    public ExecutionSession? LastExecutionSession
    {
        get => _lastExecutionSession;
        private set => SetProperty(ref _lastExecutionSession, value);
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

    public RelayCommand<ActionItemViewModel> BrowseProgramCommand { get; }

    public RelayCommand<ActionItemViewModel> FindProgramCommand { get; }

    public RelayCommand<ActionItemViewModel> SelectProcessCommand { get; }

    public RelayCommand<ActionItemViewModel> ToggleActionExpandedCommand { get; }

    public AsyncRelayCommand RunProfileCommand { get; }

    public RelayCommand CancelProfileCommand { get; }

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
            Name = null,
            ActionSchemaVersion = 1,
            SortOrder = SelectedProfile.Actions.Count,
            IsEnabled = true,
            Parameters = CreateDefaultActionParameters(SelectedActionType.TypeId)
        }, _localizationService);
        Subscribe(action);
        foreach (var existingAction in SelectedProfile.Actions)
        {
            existingAction.IsExpanded = false;
        }

        action.IsExpanded = true;
        SelectedProfile.Actions.Add(action);
        SelectedAction = action;
        NotifyActionCommandStates();
        MarkDirty(_localizationService.GetString("Status.ActionAdded"));
    }

    private void DeleteAction(ActionItemViewModel? action)
    {
        if (SelectedProfile is null || action is null)
        {
            return;
        }

        var removedIndex = SelectedProfile.Actions.IndexOf(action);
        SelectedProfile.Actions.Remove(action);
        if (ReferenceEquals(SelectedAction, action))
        {
            SelectedAction = SelectedProfile.Actions.Count == 0
                ? null
                : SelectedProfile.Actions[Math.Min(removedIndex, SelectedProfile.Actions.Count - 1)];
        }
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

    private bool CanRunProfile() =>
        SelectedProfile is not null && !IsProfileRunning && !_profileRunner.IsRunning;

    private async Task RunProfileAsync()
    {
        var profileViewModel = SelectedProfile;
        if (profileViewModel is null || IsProfileRunning)
        {
            return;
        }

        for (var actionIndex = 0; actionIndex < profileViewModel.Actions.Count; actionIndex++)
        {
            profileViewModel.Actions[actionIndex].SortOrder = actionIndex;
        }

        var profile = profileViewModel.ToModel();
        _profileExecutionCancellation = new CancellationTokenSource();
        IsProfileRunning = true;
        HasExecutionStatus = true;
        CurrentExecutionActionNumber = 0;
        TotalExecutionActions = profile.Actions.Count(action => action.IsEnabled);
        CurrentExecutionActionName = profileViewModel.Name;
        _currentExecutionActionId = null;
        ExecutionErrorMessage = string.Empty;
        SetExecutionStatus("Execution.Status.Running");
        StatusMessage = _localizationService.GetString("Status.ProfileRunning");

        var progress = new Progress<ProfileExecutionProgress>(ApplyExecutionProgress);
        try
        {
            var session = await _profileRunner.RunAsync(
                profile,
                progress,
                _profileExecutionCancellation.Token);
            LastExecutionSession = session;

            switch (session.Status)
            {
                case ExecutionSessionStatus.Completed:
                    SetExecutionStatus("Execution.Status.Success");
                    StatusMessage = _localizationService.GetString("Status.ProfileCompleted");
                    if (session.Journal.All(entry =>
                            entry.Status is ActionJournalStatus.Success or ActionJournalStatus.Skipped))
                    {
                        _profileCompletionBehavior.HandleSuccessfulCompletion(profile);
                    }
                    break;
                case ExecutionSessionStatus.Cancelled:
                    SetExecutionStatus("Execution.Status.Cancelled");
                    StatusMessage = _localizationService.GetString("Status.ProfileCancelled");
                    break;
                default:
                    SetExecutionStatus("Execution.Status.Failed");
                    ExecutionErrorMessage = session.Journal.LastOrDefault(entry =>
                        entry.Status is ActionJournalStatus.Failed or ActionJournalStatus.Unsupported)?.ErrorMessage ?? string.Empty;
                    StatusMessage = _localizationService.GetString("Status.ProfileFailed");
                    break;
            }
        }
        catch (InvalidOperationException exception)
        {
            SetExecutionStatus("Execution.Status.Failed");
            ExecutionErrorMessage = exception.Message;
            StatusMessage = _localizationService.GetString("Status.ProfileFailed");
        }
        finally
        {
            _profileExecutionCancellation.Dispose();
            _profileExecutionCancellation = null;
            IsProfileRunning = false;
        }
    }

    private void ApplyExecutionProgress(ProfileExecutionProgress progress)
    {
        CurrentExecutionActionNumber = progress.CurrentActionNumber;
        TotalExecutionActions = progress.TotalActiveActions;
        _currentExecutionActionId = progress.Action.Id;
        CurrentExecutionActionName = _allProfiles
            .SelectMany(profile => profile.Actions)
            .FirstOrDefault(action => action.Id == progress.Action.Id)?.DisplayName
            ?? progress.Action.Name
            ?? progress.Action.Type;
        SetExecutionStatus(GetExecutionStatusResourceKey(progress.JournalEntry.Status));
        ExecutionErrorMessage = progress.JournalEntry.Status is ActionJournalStatus.Failed or ActionJournalStatus.Unsupported
            ? progress.JournalEntry.ErrorMessage ?? string.Empty
            : string.Empty;
    }

    private void CancelProfile() => _profileExecutionCancellation?.Cancel();

    private void BrowseProgram(ActionItemViewModel? action)
    {
        if (action is null || action.Type != ActionTypeIds.ProgramRun)
        {
            return;
        }

        var selectedPath = _dialogService.SelectFile(
            _localizationService.GetString("Dialog.SelectProgramTitle"),
            _localizationService.GetString("Dialog.ProgramFileFilter"),
            action.Target);
        if (selectedPath is not null)
        {
            ApplyProgramSelection(
                action,
                selectedPath,
                Path.GetDirectoryName(selectedPath) ?? string.Empty,
                GetFriendlyProgramName(selectedPath));
        }
    }

    private void FindProgram(ActionItemViewModel? action)
    {
        if (action is null || action.Type != ActionTypeIds.ProgramRun)
        {
            return;
        }

        var selectedProgram = _dialogService.FindProgram(
            _localizationService.GetString("Dialog.FindProgramTitle"));
        if (selectedProgram is null)
        {
            return;
        }

        ApplyProgramSelection(
            action,
            selectedProgram.TargetPath,
            selectedProgram.WorkingDirectory,
            selectedProgram.DisplayName);
    }

    private void SelectProcess(ActionItemViewModel? action)
    {
        if (action is null || action.Type != ActionTypeIds.ProcessSetState)
        {
            return;
        }

        var selectedProcess = _dialogService.SelectProcess(
            _localizationService.GetString("Dialog.SelectProcessTitle"));
        if (selectedProcess is null)
        {
            return;
        }

        action.TrySetSuggestedName(selectedProcess.SuggestedName);
        action.ProcessName = selectedProcess.ProcessName;
        action.ExecutablePath = selectedProcess.ExecutablePath ?? string.Empty;
    }

    private void ToggleActionExpanded(ActionItemViewModel? action)
    {
        if (action is null || SelectedProfile is null)
        {
            return;
        }

        var shouldExpand = !action.IsExpanded;
        foreach (var candidate in SelectedProfile.Actions)
        {
            candidate.IsExpanded = false;
        }

        action.IsExpanded = shouldExpand;
        SelectedAction = action;
    }

    private static void ApplyProgramSelection(
        ActionItemViewModel action,
        string target,
        string workingDirectory,
        string suggestedName)
    {
        action.Target = target;
        action.TrySetSuggestedName(suggestedName);
        action.WorkingDirectory = workingDirectory;
    }

    private static string GetFriendlyProgramName(string path)
    {
        if (string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var version = FileVersionInfo.GetVersionInfo(path);
                var friendlyName = new[] { version.FileDescription, version.ProductName }
                    .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                if (!string.IsNullOrWhiteSpace(friendlyName))
                {
                    return friendlyName.Trim();
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               ArgumentException or NotSupportedException)
            {
                // Fall back to the file name below.
            }
        }

        return Path.GetFileNameWithoutExtension(path);
    }
    private void SetExecutionStatus(string resourceKey)
    {
        _executionStatusResourceKey = resourceKey;
        ExecutionStatusText = _localizationService.GetString(resourceKey);
    }

    private void RefreshExecutionDisplay()
    {
        ExecutionStatusText = _localizationService.GetString(_executionStatusResourceKey);
        if (_currentExecutionActionId is Guid actionId)
        {
            var action = _allProfiles.SelectMany(profile => profile.Actions)
                .FirstOrDefault(candidate => candidate.Id == actionId);
            if (action is not null)
            {
                CurrentExecutionActionName = action.DisplayName;
            }
        }
    }

    private static string GetExecutionStatusResourceKey(ActionJournalStatus status) => status switch
    {
        ActionJournalStatus.Pending => "Execution.Status.Pending",
        ActionJournalStatus.Running => "Execution.Status.Running",
        ActionJournalStatus.Success => "Execution.Status.Success",
        ActionJournalStatus.Skipped => "Execution.Status.Skipped",
        ActionJournalStatus.Cancelled => "Execution.Status.Cancelled",
        _ => "Execution.Status.Failed"
    };
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

        RefreshExecutionDisplay();

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
                var profile = profiles[profileIndex];
                profile.SortOrder = profileIndex;
                for (var actionIndex = 0; actionIndex < profile.Actions.Count; actionIndex++)
                {
                    profile.Actions[actionIndex].SortOrder = actionIndex;
                }
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

        HasUnsavedChanges = true;
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

    private static JsonObject CreateDefaultActionParameters(string actionType) => actionType switch
    {
        ActionTypeIds.ProgramRun => new JsonObject
        {
            [ActionParameterNames.StartOnlyIfNotAlreadyRunning] = true
        },
        ActionTypeIds.ProcessSetState => new JsonObject
        {
            [ActionParameterNames.DesiredState] = ProcessDesiredStateIds.Stopped
        },
        ActionTypeIds.Delay => new JsonObject
        {
            [ActionParameterNames.DelaySeconds] = 0
        },
        _ => []
    };
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
