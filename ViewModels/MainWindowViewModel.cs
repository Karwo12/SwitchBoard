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
using SwitchBoard.Services.Windows;

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
    private readonly ProfileRestoreRunner _profileRestoreRunner;
    private readonly IExecutionSessionRepository _sessionRepository;
    private readonly IProfileCompletionBehavior _profileCompletionBehavior;
    private readonly IDisplayManager _displayManager;
    private readonly ICustomThemeEditorService _customThemeEditorService;
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
    private readonly UndoService<SwitchBoardCatalog> _undoService = new();
    private SwitchBoardCatalog _undoBaseline;
    private bool _suppressUndoTracking;
    private bool _isRestoreRunning;
    private bool _isSaving;
    private bool _allowCloseWithoutConfirmation;
    private DateTimeOffset _lastAddActionAt;
    private DateTimeOffset _lastUndoAt;
    private PersistentExecutionSession? _pendingRestoreSession;
    private int _restoreChangeCount;
    private string _restoreNoticeText = string.Empty;

    public MainWindowViewModel(
        IProfileCatalogService catalogService,
        IUserDialogService dialogService,
        SwitchBoardCatalog catalog,
        IThemeManager themeManager,
        ILocalizationService localizationService,
        ISettingsRepository settingsRepository,
        UserSettings userSettings,
        ProfileRunner profileRunner,
        ProfileRestoreRunner profileRestoreRunner,
        IExecutionSessionRepository sessionRepository,
        IProfileCompletionBehavior profileCompletionBehavior,
        IDisplayManager displayManager,
        ICustomThemeEditorService customThemeEditorService)
    {
        _catalogService = catalogService;
        _dialogService = dialogService;
        _themeManager = themeManager;
        _localizationService = localizationService;
        _settingsRepository = settingsRepository;
        _userSettings = userSettings;
        _profileRunner = profileRunner;
        _profileRestoreRunner = profileRestoreRunner;
        _sessionRepository = sessionRepository;
        _profileCompletionBehavior = profileCompletionBehavior;
        _displayManager = displayManager;
        _customThemeEditorService = customThemeEditorService;
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
        AddActionCommand = new RelayCommand(AddAction, () => SelectedProfile is not null && SelectedActionType is not null && !HasCriticalOperation);
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
        CustomizeThemeCommand = new RelayCommand(CustomizeTheme,
            () => string.Equals(SelectedThemeOption?.Id, ThemeIds.Custom, StringComparison.OrdinalIgnoreCase));
        BrowseProgramCommand = new RelayCommand<ActionItemViewModel>(BrowseProgram, action => action?.Type == ActionTypeIds.ProgramRun);
        FindProgramCommand = new RelayCommand<ActionItemViewModel>(FindProgram, action => action?.Type == ActionTypeIds.ProgramRun);
        SelectProcessCommand = new RelayCommand<ActionItemViewModel>(SelectProcess, action => action?.Type == ActionTypeIds.ProcessSetState);
        SelectServiceCommand = new RelayCommand<ActionItemViewModel>(SelectService, action => action?.Type == ActionTypeIds.ServiceSetState);
        SelectPowerPlanCommand = new RelayCommand<ActionItemViewModel>(SelectPowerPlan, action => action?.Type == ActionTypeIds.PowerSetPlan);
        SelectDisplayCommand = new RelayCommand<ActionItemViewModel>(SelectDisplay, action => action?.Type == ActionTypeIds.DisplayConfigure);
        BrowseScriptCommand = new RelayCommand<ActionItemViewModel>(BrowseScript, action => action?.Type == ActionTypeIds.ScriptRun);
        BrowseRestoreScriptCommand = new RelayCommand<ActionItemViewModel>(BrowseRestoreScript, action => action?.Type == ActionTypeIds.ScriptRun);
        ToggleActionExpandedCommand = new RelayCommand<ActionItemViewModel>(ToggleActionExpanded, action => action is not null);
        ToggleAdvancedOptionsCommand = new RelayCommand<ActionItemViewModel>(action =>
        {
            if (action is not null) action.IsAdvancedOptionsExpanded = !action.IsAdvancedOptionsExpanded;
        }, action => action is not null);
        RunProfileCommand = new AsyncRelayCommand(RunProfileAsync, CanRunProfile);
        RestoreProfileCommand = new AsyncRelayCommand(RestoreProfileAsync, CanRestoreProfile);
        CancelProfileCommand = new RelayCommand(CancelProfile, () => IsProfileRunning || IsRestoreRunning);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsProfileRunning && !IsRestoreRunning);
        UndoCommand = new RelayCommand<string>(Undo, _ => _undoService.CanUndo && !HasCriticalOperation);

        SubscribeToItems();
        _undoBaseline = BuildCatalogSnapshot();
        SelectedCategory = Categories.FirstOrDefault();
        SetClean(_localizationService.GetString("Status.CatalogLoaded"));
        _ = HydrateDisplayActionsAsync();
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
                _ = RefreshPendingRestoreAsync();
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
                CustomizeThemeCommand.NotifyCanExecuteChanged();
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
                RestoreProfileCommand.NotifyCanExecuteChanged();
                CancelProfileCommand.NotifyCanExecuteChanged();
                AddActionCommand.NotifyCanExecuteChanged();
                SaveCommand.NotifyCanExecuteChanged();
                UndoCommand.NotifyCanExecuteChanged();
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

    public bool IsRestoreRunning
    {
        get => _isRestoreRunning;
        private set
        {
            if (!SetProperty(ref _isRestoreRunning, value)) return;
            RestoreProfileCommand.NotifyCanExecuteChanged();
            RunProfileCommand.NotifyCanExecuteChanged();
            UndoCommand.NotifyCanExecuteChanged();
            CancelProfileCommand.NotifyCanExecuteChanged();
            AddActionCommand.NotifyCanExecuteChanged();
            SaveCommand.NotifyCanExecuteChanged();
        }
    }

    public int RestoreChangeCount { get => _restoreChangeCount; private set => SetProperty(ref _restoreChangeCount, value); }
    public bool HasPendingRestore => RestoreChangeCount > 0;
    public string RestoreNoticeText
    {
        get => _restoreNoticeText;
        private set { if (SetProperty(ref _restoreNoticeText, value)) OnPropertyChanged(nameof(HasRestoreNotice)); }
    }
    public bool HasRestoreNotice => !string.IsNullOrWhiteSpace(RestoreNoticeText);
    public string RestoreButtonText => RestoreChangeCount > 0
        ? _localizationService.Format("Restore.ButtonCount", RestoreChangeCount)
        : _localizationService.GetString("Common.Restore");
    public bool IsSaving
    {
        get => _isSaving;
        private set
        {
            if (!SetProperty(ref _isSaving, value)) return;
            RunProfileCommand.NotifyCanExecuteChanged();
            RestoreProfileCommand.NotifyCanExecuteChanged();
            AddActionCommand.NotifyCanExecuteChanged();
            UndoCommand.NotifyCanExecuteChanged();
        }
    }
    public bool HasCriticalOperation => IsProfileRunning || IsRestoreRunning || IsSaving;

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
    public RelayCommand CustomizeThemeCommand { get; }

    public RelayCommand<ActionItemViewModel> BrowseProgramCommand { get; }

    public RelayCommand<ActionItemViewModel> FindProgramCommand { get; }

    public RelayCommand<ActionItemViewModel> SelectProcessCommand { get; }

    public RelayCommand<ActionItemViewModel> SelectServiceCommand { get; }

    public RelayCommand<ActionItemViewModel> SelectPowerPlanCommand { get; }

    public RelayCommand<ActionItemViewModel> SelectDisplayCommand { get; }

    public RelayCommand<ActionItemViewModel> BrowseScriptCommand { get; }
    public RelayCommand<ActionItemViewModel> BrowseRestoreScriptCommand { get; }

    public RelayCommand<ActionItemViewModel> ToggleAdvancedOptionsCommand { get; }

    public RelayCommand<ActionItemViewModel> ToggleActionExpandedCommand { get; }

    public AsyncRelayCommand RunProfileCommand { get; }
    public AsyncRelayCommand RestoreProfileCommand { get; }

    public RelayCommand CancelProfileCommand { get; }

    public AsyncRelayCommand SaveCommand { get; }
    public RelayCommand<string> UndoCommand { get; }

    private void AddCategory()
    {
        RecordStructuralUndo("add-category");
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

        RecordStructuralUndo("delete-category");

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

        RecordStructuralUndo("add-profile");

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

        RecordStructuralUndo("delete-profile");

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
        if (DateTimeOffset.UtcNow - _lastAddActionAt < TimeSpan.FromMilliseconds(350)) return;
        _lastAddActionAt = DateTimeOffset.UtcNow;
        if (SelectedProfile is null || SelectedActionType is null)
        {
            return;
        }

        RecordStructuralUndo("add-action");

        var action = new ActionItemViewModel(new ActionDefinition
        {
            Id = Guid.NewGuid(),
            Type = SelectedActionType.TypeId,
            Name = null,
            ActionSchemaVersion = 1,
            SortOrder = SelectedProfile.Actions.Count,
            IsEnabled = true,
            FailurePolicy = ActionFailurePolicy.Continue,
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

        RecordStructuralUndo("delete-action");

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
            RecordStructuralUndo("move-action");
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
            RecordStructuralUndo("move-action");
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
        if (IsSaving) return;
        IsSaving = true;
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
        finally { IsSaving = false; }
    }

    private bool CanRunProfile() => SelectedProfile is not null &&
        SelectedProfile.Actions.All(action => !action.IsEnabled || action.IsValid) &&
        !IsProfileRunning && !IsRestoreRunning && !IsSaving && !_profileRunner.IsRunning;

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
        _allowCloseWithoutConfirmation = false;
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
            await RefreshPendingRestoreAsync(profile.Id);
            if (RestoreChangeCount > 0)
            {
                RestoreNoticeText = _localizationService.Format("Restore.ProfileCompleted", RestoreChangeCount);
            }

            switch (session.Status)
            {
                case ExecutionSessionStatus.Completed:
                    SetExecutionStatus("Execution.Status.Success");
                    StatusMessage = _localizationService.GetString("Status.ProfileCompleted");
                    if (session.Journal.All(entry =>
                            entry.Status is ActionJournalStatus.Success or ActionJournalStatus.Skipped))
                    {
                        _allowCloseWithoutConfirmation = profile.CloseSwitchBoardAfterSuccessfulCompletion;
                        _profileCompletionBehavior.HandleSuccessfulCompletion(profile);
                    }
                    break;
                case ExecutionSessionStatus.Cancelled:
                    SetExecutionStatus("Execution.Status.Cancelled");
                    StatusMessage = _localizationService.GetString("Status.ProfileCancelled");
                    break;
                case ExecutionSessionStatus.CompletedWithErrors:
                    SetExecutionStatus("Execution.Status.CompletedWithErrors");
                    ExecutionErrorMessage = session.Journal.LastOrDefault(entry =>
                        entry.Status is ActionJournalStatus.Failed or ActionJournalStatus.Unsupported)?.ErrorMessage ?? string.Empty;
                    StatusMessage = _localizationService.GetString("Status.ProfileCompletedWithErrors");
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
        catch (Exception exception)
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

    private bool CanRestoreProfile() => SelectedProfile is not null && RestoreChangeCount > 0 &&
        !IsProfileRunning && !IsRestoreRunning && !IsSaving && !_profileRestoreRunner.IsRunning;

    private async Task RefreshPendingRestoreAsync(Guid? profileId = null)
    {
        var id = profileId ?? SelectedProfile?.Id;
        if (id is null)
        {
            _pendingRestoreSession = null;
            SetRestoreCount(0);
            RestoreNoticeText = string.Empty;
            return;
        }
        try
        {
            var loaded = await _sessionRepository.GetLatestPendingAsync(id.Value);
            if (SelectedProfile?.Id != id.Value) return;
            _pendingRestoreSession = loaded;
            SetRestoreCount(_pendingRestoreSession?.PendingRestoreCount ?? 0);
            if (_pendingRestoreSession?.Status == PersistentSessionStatus.RecoveryRequired)
                RestoreNoticeText = _localizationService.GetString("Restore.RecoveryPending");
            else if (RestoreChangeCount > 0)
                RestoreNoticeText = _localizationService.GetString("Restore.PreviousPending");
            else
                RestoreNoticeText = await _sessionRepository.GetLatestAttentionAsync(id.Value) is not null
                    ? _localizationService.GetString("Restore.RecoveryPending") : string.Empty;
        }
        catch (Exception exception)
        {
            if (SelectedProfile?.Id != id.Value) return;
            _pendingRestoreSession = null;
            SetRestoreCount(0);
            StatusMessage = _localizationService.Format("Restore.Failed", exception.Message);
        }
    }

    private async Task RestoreProfileAsync()
    {
        if (_pendingRestoreSession is null || !CanRestoreProfile()) return;
        IsRestoreRunning = true;
        HasExecutionStatus = true;
        CurrentExecutionActionNumber = 0;
        TotalExecutionActions = RestoreChangeCount;
        CurrentExecutionActionName = SelectedProfile?.Name ?? string.Empty;
        ExecutionErrorMessage = string.Empty;
        RestoreNoticeText = _localizationService.GetString("Restore.Running");
        _profileExecutionCancellation = new CancellationTokenSource();
        try
        {
            var progress = new Progress<ProfileRestoreProgress>(item =>
            {
                CurrentExecutionActionNumber = item.CurrentAction;
                TotalExecutionActions = item.TotalActions;
                CurrentExecutionActionName = item.Action.ActionName ?? item.Action.ActionType;
                ExecutionStatusText = _localizationService.Format("Restore.Progress", item.CurrentAction,
                    item.TotalActions, CurrentExecutionActionName);
                ExecutionErrorMessage = item.Status == PersistentActionRestoreStatus.Failed ? item.Message ?? string.Empty : string.Empty;
            });
            var result = await _profileRestoreRunner.RunAsync(_pendingRestoreSession, progress,
                _profileExecutionCancellation.Token);
            var completedCurrentSession = result.PendingRestoreCount == 0;
            await RefreshPendingRestoreAsync();
            RestoreNoticeText = !completedCurrentSession
                ? _localizationService.GetString("Restore.Partial")
                : RestoreChangeCount > 0
                    ? _localizationService.GetString("Restore.PreviousPending")
                    : _localizationService.GetString("Restore.Completed");
            SetExecutionStatus(result.PendingRestoreCount == 0 ? "Execution.Status.Success" : "Execution.Status.CompletedWithErrors");
        }
        catch (OperationCanceledException)
        {
            await RefreshPendingRestoreAsync();
            SetExecutionStatus("Execution.Status.Cancelled");
        }
        catch (Exception exception)
        {
            ExecutionErrorMessage = exception.Message;
            RestoreNoticeText = _localizationService.Format("Restore.Failed", exception.Message);
            await RefreshPendingRestoreAsync();
        }
        finally
        {
            _profileExecutionCancellation?.Dispose();
            _profileExecutionCancellation = null;
            IsRestoreRunning = false;
        }
    }

    private void SetRestoreCount(int value)
    {
        RestoreChangeCount = value;
        OnPropertyChanged(nameof(HasPendingRestore));
        OnPropertyChanged(nameof(RestoreButtonText));
        RestoreProfileCommand.NotifyCanExecuteChanged();
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
            RunGroupedConfigurationChange("select-program", () => ApplyProgramSelection(
                action, selectedPath, Path.GetDirectoryName(selectedPath) ?? string.Empty,
                GetFriendlyProgramName(selectedPath)));
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

        RunGroupedConfigurationChange("find-program", () => ApplyProgramSelection(
            action, selectedProgram.TargetPath, selectedProgram.WorkingDirectory, selectedProgram.DisplayName));
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

        RunGroupedConfigurationChange("select-process", () =>
        {
            action.TrySetSuggestedName(selectedProcess.SuggestedName);
            action.ProcessName = selectedProcess.ProcessName;
            action.ExecutablePath = selectedProcess.ExecutablePath ?? string.Empty;
            action.RuntimeProcessIdHint = selectedProcess.ProcessId;
        });
    }

    private void SelectService(ActionItemViewModel? action)
    {
        if (action is null || action.Type != ActionTypeIds.ServiceSetState) return;
        var selected = _dialogService.SelectService(_localizationService.GetString("Dialog.SelectServiceTitle"));
        if (selected is null) return;
        RunGroupedConfigurationChange("select-service", () =>
        {
            action.ServiceName = selected.ServiceName;
            action.ServiceDisplayName = selected.DisplayName;
            action.TrySetSuggestedName(selected.DisplayName);
        });
    }

    private void SelectPowerPlan(ActionItemViewModel? action)
    {
        if (action is null || action.Type != ActionTypeIds.PowerSetPlan) return;
        var selected = _dialogService.SelectPowerPlan(_localizationService.GetString("Dialog.SelectPowerPlanTitle"));
        if (selected is null) return;
        RunGroupedConfigurationChange("select-power-plan", () =>
        {
            action.PowerPlanGuid = selected.Id.ToString("D");
            action.PowerPlanName = selected.DisplayName;
            action.TrySetSuggestedName(selected.DisplayName);
        });
    }

    private void SelectDisplay(ActionItemViewModel? action)
    {
        if (action is null || action.Type != ActionTypeIds.DisplayConfigure) return;
        var selected = _dialogService.SelectDisplay(_localizationService.GetString("Dialog.SelectDisplayTitle"));
        if (selected is null) return;
        RunGroupedConfigurationChange("select-display", () =>
        {
            action.ApplyDisplayCandidate(selected);
            action.TrySetSuggestedName(selected.DisplayName);
        });
    }

    private async Task HydrateDisplayActionsAsync()
    {
        var actions = _allProfiles.SelectMany(profile => profile.Actions)
            .Where(action => action.Type == ActionTypeIds.DisplayConfigure &&
                             (!string.IsNullOrWhiteSpace(action.DisplayDeviceId) ||
                              !string.IsNullOrWhiteSpace(action.DisplayDeviceName)))
            .ToList();
        if (actions.Count == 0) return;
        try
        {
            var displays = await _displayManager.GetDisplaysAsync();
            foreach (var action in actions)
            {
                var display = displays.FirstOrDefault(candidate =>
                                  !string.IsNullOrWhiteSpace(action.DisplayDeviceId) &&
                                  string.Equals(candidate.DeviceId, action.DisplayDeviceId, StringComparison.OrdinalIgnoreCase))
                              ?? displays.FirstOrDefault(candidate =>
                                  string.Equals(candidate.DeviceName, action.DisplayDeviceName, StringComparison.OrdinalIgnoreCase));
                if (display is not null) action.ApplyDisplayCandidate(display, notifyChanges: false);
            }
        }
        catch
        {
            // Keep the persisted values visible. The picker can retry discovery on demand.
        }
    }

    private void BrowseScript(ActionItemViewModel? action)
    {
        if (action is null || action.Type != ActionTypeIds.ScriptRun) return;
        var selected = _dialogService.SelectFile(
            _localizationService.GetString("Dialog.SelectScriptTitle"),
            _localizationService.GetString("Dialog.ScriptFileFilter"),
            action.ScriptPath);
        if (selected is null) return;
        RunGroupedConfigurationChange("select-script", () =>
        {
            action.ScriptPath = selected;
            action.TrySetSuggestedName(Path.GetFileNameWithoutExtension(selected));
            if (string.IsNullOrWhiteSpace(action.WorkingDirectory))
                action.WorkingDirectory = Path.GetDirectoryName(selected) ?? string.Empty;
        });
    }

    private void BrowseRestoreScript(ActionItemViewModel? action)
    {
        if (action is null || action.Type != ActionTypeIds.ScriptRun) return;
        var selected = _dialogService.SelectFile(
            _localizationService.GetString("Dialog.SelectRestoreScriptTitle"),
            _localizationService.GetString("Dialog.ScriptFileFilter"), action.RestoreScriptPath);
        if (selected is null) return;
        RunGroupedConfigurationChange("select-restore-script", () =>
        {
            action.RestoreScriptPath = selected;
            if (string.IsNullOrWhiteSpace(action.RestoreScriptWorkingDirectory))
                action.RestoreScriptWorkingDirectory = Path.GetDirectoryName(selected) ?? string.Empty;
        });
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

        var appliedThemeId = _themeManager.ApplyTheme(option.Id, _userSettings.CustomTheme);
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

    private void CustomizeTheme()
    {
        if (!string.Equals(SelectedThemeOption?.Id, ThemeIds.Custom, StringComparison.OrdinalIgnoreCase)) return;
        var result = _customThemeEditorService.Edit(_userSettings.CustomTheme,
            preview => _themeManager.ApplyTheme(ThemeIds.Custom, preview));
        if (result is null)
        {
            _themeManager.ApplyTheme(ThemeIds.Custom, _userSettings.CustomTheme);
            return;
        }
        _userSettings.CustomTheme = result;
        _userSettings.ThemeId = ThemeIds.Custom;
        _themeManager.ApplyTheme(ThemeIds.Custom, result);
        _ = SaveCustomThemeSettingsAsync();
    }

    private async Task SaveCustomThemeSettingsAsync()
    {
        try
        {
            _userSettings.SchemaVersion = SettingsSchema.CurrentVersion;
            await _settingsRepository.SaveAsync(_userSettings);
            StatusMessage = _localizationService.GetString("CustomTheme.SavedStatus");
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
        OnPropertyChanged(nameof(RestoreButtonText));
        if (RestoreChangeCount > 0) RestoreNoticeText = _localizationService.GetString("Restore.PreviousPending");

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
            nameof(CategoryItemViewModel.EditName) or
            nameof(ActionItemViewModel.IsExpanded) or
            nameof(ActionItemViewModel.IsAdvancedOptionsExpanded) or
            nameof(ActionItemViewModel.DisplayName) or
            nameof(ActionItemViewModel.Summary) or
            nameof(ActionItemViewModel.IsValid) or
            nameof(ActionItemViewModel.ValidationMessage) or
            nameof(ActionItemViewModel.SupportsRestore) or
            nameof(ActionItemViewModel.IsRestoreScriptEnabled))
        {
            return;
        }
        if (!_suppressUndoTracking)
        {
            var id = sender switch
            {
                CategoryItemViewModel category => category.Id,
                ProfileItemViewModel profile => profile.Id,
                ActionItemViewModel action => action.Id,
                _ => Guid.Empty
            };
            var key = sender is ActionItemViewModel ? $"property:{id}" : $"property:{id}:{e.PropertyName}";
            _undoService.Record(_undoBaseline, key, allowCoalescing: true);
            _undoBaseline = BuildCatalogSnapshot();
            UndoCommand.NotifyCanExecuteChanged();
        }
        HasUnsavedChanges = true;
        RunProfileCommand.NotifyCanExecuteChanged();
    }

    private void MarkDirty(string message)
    {
        _suppressUndoTracking = false;
        _undoBaseline = BuildCatalogSnapshot();
        HasUnsavedChanges = true;
        StatusMessage = message;
    }

    private void RecordStructuralUndo(string key)
    {
        _undoService.Record(_undoBaseline, $"{key}:{Guid.NewGuid():N}");
        _suppressUndoTracking = true;
        UndoCommand.NotifyCanExecuteChanged();
    }

    private void RunGroupedConfigurationChange(string key, Action change)
    {
        RecordStructuralUndo(key);
        try { change(); }
        finally
        {
            _suppressUndoTracking = false;
            _undoBaseline = BuildCatalogSnapshot();
            HasUnsavedChanges = true;
        }
    }

    private void Undo(string? source)
    {
        if (source == "button")
        {
            if (DateTimeOffset.UtcNow - _lastUndoAt < TimeSpan.FromMilliseconds(300)) return;
            _lastUndoAt = DateTimeOffset.UtcNow;
        }
        if (!_undoService.TryUndo(out var catalog) || catalog is null) return;
        var categoryId = SelectedCategory?.Id;
        var profileId = SelectedProfile?.Id;
        _suppressUndoTracking = true;
        foreach (var category in Categories) category.PropertyChanged -= ItemOnPropertyChanged;
        foreach (var profile in _allProfiles)
        {
            profile.PropertyChanged -= ItemOnPropertyChanged;
            foreach (var action in profile.Actions) action.PropertyChanged -= ItemOnPropertyChanged;
        }
        Categories.Clear();
        _allProfiles.Clear();
        foreach (var category in catalog.Categories.OrderBy(item => item.SortOrder))
            Categories.Add(new CategoryItemViewModel(category));
        foreach (var profile in catalog.Profiles.OrderBy(item => item.SortOrder))
            _allProfiles.Add(new ProfileItemViewModel(profile, _localizationService));
        SubscribeToItems();
        SelectedCategory = Categories.FirstOrDefault(item => item.Id == categoryId) ?? Categories.FirstOrDefault();
        SelectedProfile = Profiles.FirstOrDefault(item => item.Id == profileId) ?? Profiles.FirstOrDefault();
        _suppressUndoTracking = false;
        _undoBaseline = BuildCatalogSnapshot();
        HasUnsavedChanges = true;
        StatusMessage = _localizationService.GetString("Common.Undo");
        UndoCommand.NotifyCanExecuteChanged();
        NotifyActionCommandStates();
    }

    public bool ConfirmCloseDuringCriticalOperation()
    {
        if (_allowCloseWithoutConfirmation || !HasCriticalOperation) return true;
        var close = _dialogService.Confirm(_localizationService.GetString("Dialog.CloseBusyTitle"),
            _localizationService.GetString("Dialog.CloseBusyMessage"));
        if (close) _profileExecutionCancellation?.Cancel();
        return close;
    }

    private SwitchBoardCatalog BuildCatalogSnapshot()
    {
        var categories = Categories.Select((item, index) =>
        {
            var model = item.ToModel();
            model.SortOrder = index;
            return model;
        }).ToList();
        var profiles = new List<ProfileDefinition>();
        foreach (var category in categories)
        {
            var categoryProfiles = _allProfiles.Where(item => item.CategoryId == category.Id)
                .OrderBy(item => item.SortOrder).ToList();
            for (var index = 0; index < categoryProfiles.Count; index++)
            {
                var model = categoryProfiles[index].ToModel();
                model.SortOrder = index;
                for (var actionIndex = 0; actionIndex < model.Actions.Count; actionIndex++)
                    model.Actions[actionIndex].SortOrder = actionIndex;
                profiles.Add(model);
            }
        }
        return new SwitchBoardCatalog { SchemaVersion = CatalogSchema.CurrentVersion, Categories = categories, Profiles = profiles };
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
        ActionTypeIds.ServiceSetState => new JsonObject
        {
            [ActionParameterNames.DesiredState] = ServiceDesiredStateIds.Unchanged
        },
        ActionTypeIds.ScriptRun => new JsonObject
        {
            [ActionParameterNames.ScriptType] = ScriptTypeIds.AutoDetect,
            [ActionParameterNames.WaitForExit] = true,
            [ActionParameterNames.RunAsAdministrator] = false
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
