using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.Json;
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
using SwitchBoard.Services.Activity;
using SwitchBoard.Services.Monitoring;
using System.Windows;
using System.Windows.Threading;

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
    private readonly IActivityService? _activityService;
    private bool _isActivityExpanded;
    private int _activityAlertCount;
    private int _activityTabIndex;
    private double _activityPanelHeightRatio;
    private readonly StatusMonitoringService? _statusMonitoring;
    private bool _isStatusRefreshing;
    private string _statusRefreshText = string.Empty;
    private DispatcherTimer? _statusMonitorTimer;

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
        ICustomThemeEditorService customThemeEditorService,
        IActivityService? activityService = null,
        StatusMonitoringService? statusMonitoring = null)
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
        _activityService = activityService;
        _statusMonitoring = statusMonitoring;
        _activityPanelHeightRatio = Math.Clamp(userSettings.ActivityPanelHeightRatio, 0.2, 0.8);
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
            new(ActionTypeIds.Delay, "Action.Delay", localizationService),
            new(ActionTypeIds.ProcessConfigure, "Action.ProcessSettings", localizationService),
            new(ActionTypeIds.WaitProcessStart, "Action.WaitProcess", localizationService),
            new(ActionTypeIds.WaitProcessExit, "Action.WaitProcessExit", localizationService),
            new(ActionTypeIds.WaitWindow, "Action.WaitWindow", localizationService),
            new(ActionTypeIds.AudioConfigure, "Action.AudioSettings", localizationService),
            new(ActionTypeIds.DeviceSetState, "Action.DeviceState", localizationService),
            new(ActionTypeIds.ProfileRun, "Action.RunProfile", localizationService),
            new(ActionTypeIds.ConditionIf, "Action.If", localizationService),
            new(ActionTypeIds.NotificationShow, "Action.Notification", localizationService)
        ];
        _selectedActionType = AvailableActionTypes[0];

        ThemeOptions = new ObservableCollection<ThemeOptionViewModel>(
            themeManager.AvailableThemes.Select(theme => new ThemeOptionViewModel(theme, localizationService)));
        foreach (var customTheme in userSettings.CustomThemes)
            ThemeOptions.Add(new ThemeOptionViewModel(customTheme, localizationService));
        _selectedThemeOption = ThemeOptions.FirstOrDefault(option =>
            string.Equals(option.Id, themeManager.CurrentThemeId, StringComparison.OrdinalIgnoreCase)) ?? ThemeOptions[0];
        UpdateActiveThemeMarker();

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
        ReorderDropCommand = new AsyncRelayCommand<ReorderDropRequest>(ApplyReorderAsync,
            request => request is not null && !HasCriticalOperation);
        BeginCategoryRenameCommand = new RelayCommand<CategoryItemViewModel>(category => category?.BeginEdit());
        CommitCategoryRenameCommand = new RelayCommand<CategoryItemViewModel>(category => category?.CommitEdit());
        CancelCategoryRenameCommand = new RelayCommand<CategoryItemViewModel>(category => category?.CancelEdit());
        BeginProfileRenameCommand = new RelayCommand<ProfileItemViewModel>(profile => profile?.BeginEdit());
        CommitProfileRenameCommand = new RelayCommand<ProfileItemViewModel>(profile => profile?.CommitEdit());
        CancelProfileRenameCommand = new RelayCommand<ProfileItemViewModel>(profile => profile?.CancelEdit());
        ToggleThemeMenuCommand = new RelayCommand(() => IsThemeMenuOpen = !IsThemeMenuOpen);
        AddThemeCommand = new AsyncRelayCommand(AddThemeAsync);
        EditThemeCommand = new AsyncRelayCommand<string>(EditThemeAsync, id => !string.IsNullOrWhiteSpace(id));
        DuplicateThemeCommand = new AsyncRelayCommand<string>(DuplicateThemeAsync, id => !string.IsNullOrWhiteSpace(id));
        RenameThemeCommand = new AsyncRelayCommand<string>(RenameThemeAsync, id => FindCustomTheme(id ?? string.Empty) is not null);
        DeleteThemeCommand = new AsyncRelayCommand<string>(DeleteThemeAsync, id => FindCustomTheme(id ?? string.Empty) is not null);
        BrowseProgramCommand = new RelayCommand<ActionItemViewModel>(BrowseProgram, action => action?.Type == ActionTypeIds.ProgramRun);
        FindProgramCommand = new RelayCommand<ActionItemViewModel>(FindProgram, action => action?.Type == ActionTypeIds.ProgramRun);
        SelectProcessCommand = new RelayCommand<ActionItemViewModel>(SelectProcess, action => action?.Type is
            ActionTypeIds.ProcessSetState or ActionTypeIds.ProcessConfigure or ActionTypeIds.WaitProcessStart or
            ActionTypeIds.WaitProcessExit or ActionTypeIds.WaitWindow);
        SelectServiceCommand = new RelayCommand<ActionItemViewModel>(SelectService, action => action?.Type == ActionTypeIds.ServiceSetState);
        SelectPowerPlanCommand = new RelayCommand<ActionItemViewModel>(SelectPowerPlan, action => action?.Type == ActionTypeIds.PowerSetPlan);
        SelectDisplayCommand = new RelayCommand<ActionItemViewModel>(SelectDisplay, action => action?.Type == ActionTypeIds.DisplayConfigure);
        BrowseScriptCommand = new RelayCommand<ActionItemViewModel>(BrowseScript, action => action?.Type == ActionTypeIds.ScriptRun);
        BrowseRestoreScriptCommand = new RelayCommand<ActionItemViewModel>(BrowseRestoreScript, action => action?.Type == ActionTypeIds.ScriptRun);
        SelectAllCpusCommand = new RelayCommand<ActionItemViewModel>(action => action?.SelectAllCpus(true),
            action => action?.Type == ActionTypeIds.ProcessConfigure);
        ClearAllCpusCommand = new RelayCommand<ActionItemViewModel>(action => action?.SelectAllCpus(false),
            action => action?.Type == ActionTypeIds.ProcessConfigure);
        SelectAllExceptCpu0Command = new RelayCommand<ActionItemViewModel>(action => action?.SelectAllExceptCpu0(),
            action => action?.Type == ActionTypeIds.ProcessConfigure);
        SelectAudioOutputCommand = new RelayCommand<ActionItemViewModel>(action => SelectAudio(action, false),
            action => action?.Type == ActionTypeIds.AudioConfigure);
        SelectAudioInputCommand = new RelayCommand<ActionItemViewModel>(action => SelectAudio(action, true),
            action => action?.Type == ActionTypeIds.AudioConfigure);
        SelectDeviceCommand = new RelayCommand<ActionItemViewModel>(SelectDevice,
            action => action?.Type == ActionTypeIds.DeviceSetState);
        ToggleActionExpandedCommand = new RelayCommand<ActionItemViewModel>(ToggleActionExpanded, action => action is not null);
        ToggleAdvancedOptionsCommand = new RelayCommand<ActionItemViewModel>(action =>
        {
            if (action is not null) action.IsAdvancedOptionsExpanded = !action.IsAdvancedOptionsExpanded;
        }, action => action is not null);
        RunProfileCommand = new AsyncRelayCommand(RunProfileAsync, CanRunProfile);
        RestoreProfileCommand = new AsyncRelayCommand(RestoreProfileAsync, CanRestoreProfile);
        DiscardPendingRestoreCommand = new AsyncRelayCommand(DiscardPendingRestoreAsync, CanDiscardPendingRestore);
        RefreshCurrentStatesCommand = new AsyncRelayCommand(RefreshCurrentStatesAsync, CanRefreshCurrentStates);
        CancelProfileCommand = new RelayCommand(CancelProfile, () => IsProfileRunning || IsRestoreRunning);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsProfileRunning && !IsRestoreRunning);
        UndoCommand = new RelayCommand<string>(Undo, _ => _undoService.CanUndo && !HasCriticalOperation);
        ToggleActivityCommand = new RelayCommand(() => IsActivityExpanded = !IsActivityExpanded);
        ClearActivityCommand = new RelayCommand(ClearActivity);
        SelectActivityTabCommand = new RelayCommand<string>(index =>
        {
            ActivityTabIndex = int.TryParse(index, out var parsed) ? Math.Clamp(parsed, 0, 2) : 0;
            IsActivityExpanded = true;
        });

        ActivityEntries = new ObservableCollection<ActivityEntry>(activityService?.Entries ?? []);
        HistoryEntries = [];
        SystemChangeEntries = [];
        if (activityService is not null)
        {
            activityService.EntryAdded += ActivityServiceOnEntryAdded;
            activityService.PersistentViewsChanged += ActivityServiceOnPersistentViewsChanged;
            RefreshPersistentActivityViews();
        }

        SubscribeToItems();
        _undoBaseline = BuildCatalogSnapshot();
        SelectedCategory = Categories.FirstOrDefault();
        SetClean(_localizationService.GetString("Status.CatalogLoaded"));
        _ = HydrateDisplayActionsAsync();
        if (_statusMonitoring is not null)
        {
            _statusMonitorTimer = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.Background,
                (_, _) => { if (ShowCurrentActionState && CanRefreshCurrentStates()) _ = RefreshCurrentStatesAsync(); },
                Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher);
            _statusMonitorTimer.Start();
        }
    }

    public ObservableCollection<CategoryItemViewModel> Categories { get; }

    public ObservableCollection<ProfileItemViewModel> Profiles { get; }

    public IReadOnlyList<ActionTypeOption> AvailableActionTypes { get; }

    public ObservableCollection<ThemeOptionViewModel> ThemeOptions { get; }

    public ObservableCollection<LanguageOptionViewModel> LanguageOptions { get; }
    public ObservableCollection<ActivityEntry> ActivityEntries { get; }
    public ObservableCollection<ActivityEntry> HistoryEntries { get; }
    public ObservableCollection<SystemChangeItemViewModel> SystemChangeEntries { get; }
    public IReadOnlyList<ProfileItemViewModel> AllProfiles => _allProfiles;
    public bool IsActivityExpanded
    {
        get => _isActivityExpanded;
        set
        {
            if (!SetProperty(ref _isActivityExpanded, value)) return;
            OnPropertyChanged(nameof(ActivityPanelHeight));
            OnPropertyChanged(nameof(TopContentRowHeight));
            OnPropertyChanged(nameof(ActivityContentRowHeight));
            if (value) ActivityAlertCount = 0;
        }
    }
    public double ActivityPanelHeight => IsActivityExpanded ? 220 : 52;
    public GridLength TopContentRowHeight => IsActivityExpanded
        ? new GridLength(1 - ActivityPanelHeightRatio, GridUnitType.Star) : new GridLength(1, GridUnitType.Star);
    public GridLength ActivityContentRowHeight => IsActivityExpanded
        ? new GridLength(ActivityPanelHeightRatio, GridUnitType.Star) : GridLength.Auto;
    public double ActivityMinimumHeight => IsActivityExpanded ? 110 : 0;
    public double ActivityPanelHeightRatio
    {
        get => _activityPanelHeightRatio;
        private set => SetProperty(ref _activityPanelHeightRatio, value);
    }
    public bool ShowCurrentActionState
    {
        get => _userSettings.ShowCurrentActionState;
        set
        {
            if (_userSettings.ShowCurrentActionState == value) return;
            _userSettings.ShowCurrentActionState = value;
            OnPropertyChanged();
            _ = _settingsRepository.SaveAsync(_userSettings);
        }
    }
    public bool IsStatusRefreshing
    {
        get => _isStatusRefreshing;
        private set { if (SetProperty(ref _isStatusRefreshing, value)) { OnPropertyChanged(nameof(StatusRefreshText)); RefreshCurrentStatesCommand.NotifyCanExecuteChanged(); } }
    }
    public string StatusRefreshText => IsStatusRefreshing ? _localizationService.GetString("Status.Refreshing") : string.Empty;
    public string RefreshButtonTooltip => _localizationService.GetString("Tooltip.RefreshCurrentStates");
    public int ActivityAlertCount
    {
        get => _activityAlertCount;
        private set
        {
            if (SetProperty(ref _activityAlertCount, value)) OnPropertyChanged(nameof(ActivityAlertText));
        }
    }
    public string ActivityAlertText => ActivityAlertCount > 0 ? $"• {ActivityAlertCount}" : string.Empty;
    public int ActivityTabIndex
    {
        get => _activityTabIndex;
        set => SetProperty(ref _activityTabIndex, value);
    }
    public int UnresolvedSystemChangeCount => SystemChangeEntries.Count(item => item.IsUnresolved);
    public string SystemChangeTabText => UnresolvedSystemChangeCount > 0
        ? _localizationService.Format("Activity.SystemChangesCount", UnresolvedSystemChangeCount)
        : _localizationService.GetString("Activity.SystemChanges");
    public string SystemChangeNoticeText => UnresolvedSystemChangeCount > 0
        ? _localizationService.Format("Activity.UnrestoredChanges", UnresolvedSystemChangeCount)
        : string.Empty;

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
                _ = RefreshCurrentStatesAsync();
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
                UpdateActiveThemeMarker();
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
            DiscardPendingRestoreCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(RestoreButtonText));
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
    public string RestoreButtonText => IsRestoreRunning
        ? _localizationService.GetString("Restore.ButtonRunning")
        : RestoreChangeCount > 0
        ? _localizationService.Format("Restore.ButtonCount", RestoreChangeCount)
        : _localizationService.GetString("Common.Restore");
    public string RestorePreviewText
    {
        get
        {
            if (_pendingRestoreSession is null || _pendingRestoreSession.PendingRestoreCount == 0)
                return _localizationService.GetString("Restore.NoPendingPreview");
            var items = _pendingRestoreSession.GetPendingRestoreEntries()
                .OrderByDescending(item => item.ExecutionSequence)
                .Select(GetRestorePreviewName)
                .Distinct(StringComparer.CurrentCultureIgnoreCase);
            return _localizationService.GetString("Restore.PreviewHeading") + Environment.NewLine +
                   string.Join(Environment.NewLine, items.Select(item => "• " + item));
        }
    }

    private string GetRestorePreviewName(PersistentSessionAction item)
    {
        if (item.ActionType == ActionTypeIds.ServiceSetState &&
            item.Parameters[ActionParameterNames.ServiceDisplayName]?.GetValue<string>() is { Length: > 0 } service)
            return service;
        if (!string.IsNullOrWhiteSpace(item.ActionName)) return item.ActionName;
        if (item.ActionType == ActionTypeIds.ProcessSetState &&
            item.Parameters[ActionParameterNames.ProcessName]?.GetValue<string>() is { Length: > 0 } process)
            return process;
        return ActivityText.ActionName(item.ActionName, item.ActionType, _localizationService);
    }
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

    public AsyncRelayCommand<ReorderDropRequest> ReorderDropCommand { get; }

    public RelayCommand<CategoryItemViewModel> BeginCategoryRenameCommand { get; }

    public RelayCommand<CategoryItemViewModel> CommitCategoryRenameCommand { get; }

    public RelayCommand<CategoryItemViewModel> CancelCategoryRenameCommand { get; }

    public RelayCommand<ProfileItemViewModel> BeginProfileRenameCommand { get; }

    public RelayCommand<ProfileItemViewModel> CommitProfileRenameCommand { get; }

    public RelayCommand<ProfileItemViewModel> CancelProfileRenameCommand { get; }

    public RelayCommand ToggleThemeMenuCommand { get; }
    public AsyncRelayCommand AddThemeCommand { get; }
    public AsyncRelayCommand<string> EditThemeCommand { get; }
    public AsyncRelayCommand<string> DuplicateThemeCommand { get; }
    public AsyncRelayCommand<string> RenameThemeCommand { get; }
    public AsyncRelayCommand<string> DeleteThemeCommand { get; }

    public RelayCommand<ActionItemViewModel> BrowseProgramCommand { get; }

    public RelayCommand<ActionItemViewModel> FindProgramCommand { get; }

    public RelayCommand<ActionItemViewModel> SelectProcessCommand { get; }

    public RelayCommand<ActionItemViewModel> SelectServiceCommand { get; }

    public RelayCommand<ActionItemViewModel> SelectPowerPlanCommand { get; }

    public RelayCommand<ActionItemViewModel> SelectDisplayCommand { get; }

    public RelayCommand<ActionItemViewModel> BrowseScriptCommand { get; }
    public RelayCommand<ActionItemViewModel> BrowseRestoreScriptCommand { get; }
    public RelayCommand<ActionItemViewModel> SelectAllCpusCommand { get; }
    public RelayCommand<ActionItemViewModel> ClearAllCpusCommand { get; }
    public RelayCommand<ActionItemViewModel> SelectAllExceptCpu0Command { get; }
    public RelayCommand<ActionItemViewModel> SelectAudioOutputCommand { get; }
    public RelayCommand<ActionItemViewModel> SelectAudioInputCommand { get; }
    public RelayCommand<ActionItemViewModel> SelectDeviceCommand { get; }

    public RelayCommand<ActionItemViewModel> ToggleAdvancedOptionsCommand { get; }

    public RelayCommand<ActionItemViewModel> ToggleActionExpandedCommand { get; }

    public AsyncRelayCommand RunProfileCommand { get; }
    public AsyncRelayCommand RestoreProfileCommand { get; }
    public AsyncRelayCommand DiscardPendingRestoreCommand { get; }
    public AsyncRelayCommand RefreshCurrentStatesCommand { get; }

    public RelayCommand CancelProfileCommand { get; }

    public AsyncRelayCommand SaveCommand { get; }
    public RelayCommand<string> UndoCommand { get; }
    public RelayCommand ToggleActivityCommand { get; }
    public RelayCommand ClearActivityCommand { get; }
    public RelayCommand<string> SelectActivityTabCommand { get; }

    public ProfileDefinition? ResolveProfileDefinition(Guid id) =>
        _allProfiles.FirstOrDefault(item => item.Id == id)?.ToModel();

    private void ActivityServiceOnEntryAdded(object? sender, ActivityEntry entry)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => ActivityServiceOnEntryAdded(sender, entry));
            return;
        }
        ActivityEntries.Add(entry);
        while (ActivityEntries.Count > 300) ActivityEntries.RemoveAt(0);
        if (!IsActivityExpanded && entry.Level is ActivityLevel.Warning or ActivityLevel.Error)
            ActivityAlertCount++;
    }

    private void ClearActivity()
    {
        _activityService?.Clear();
        ActivityEntries.Clear();
        ActivityAlertCount = 0;
    }

    private void ActivityServiceOnPersistentViewsChanged(object? sender, EventArgs args)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => ActivityServiceOnPersistentViewsChanged(sender, args));
            return;
        }
        RefreshPersistentActivityViews();
    }

    private void RefreshPersistentActivityViews()
    {
        HistoryEntries.Clear();
        foreach (var entry in _activityService?.HistoryEntries ?? []) HistoryEntries.Add(entry);
        SystemChangeEntries.Clear();
        foreach (var change in _activityService?.SystemChanges ?? [])
            SystemChangeEntries.Add(new SystemChangeItemViewModel(change, _localizationService));
        OnPropertyChanged(nameof(UnresolvedSystemChangeCount));
        OnPropertyChanged(nameof(SystemChangeTabText));
        OnPropertyChanged(nameof(SystemChangeNoticeText));
    }

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
            OnPropertyChanged(nameof(AllProfiles));
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
        OnPropertyChanged(nameof(AllProfiles));
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
        OnPropertyChanged(nameof(AllProfiles));
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

    public async Task ApplyReorderAsync(ReorderDropRequest? request)
    {
        if (request is null || HasCriticalOperation) return;
        var changed = request.Kind switch
        {
            ReorderItemKind.Category => ReorderCategory(request),
            ReorderItemKind.Profile => ReorderProfile(request),
            ReorderItemKind.Action => ReorderAction(request),
            _ => false
        };
        if (!changed) return;

        NormalizeSortOrders();
        NotifyActionCommandStates();
        await SaveAsync();
    }

    private bool ReorderCategory(ReorderDropRequest request)
    {
        if (request.Item is not CategoryItemViewModel category) return false;
        var oldIndex = Categories.IndexOf(category);
        if (oldIndex < 0) return false;
        var insertionIndex = Math.Clamp(request.TargetIndex, 0, Categories.Count);
        var newIndex = insertionIndex > oldIndex ? insertionIndex - 1 : insertionIndex;
        newIndex = Math.Clamp(newIndex, 0, Categories.Count - 1);
        if (newIndex == oldIndex) return false;

        RecordStructuralUndo("drag-category");
        Categories.Move(oldIndex, newIndex);
        MarkDirty(_localizationService.GetString("Status.CategoryOrderChanged"));
        return true;
    }

    private bool ReorderProfile(ReorderDropRequest request)
    {
        if (request.Item is not ProfileItemViewModel profile || !_allProfiles.Contains(profile)) return false;
        var sourceCategoryId = profile.CategoryId;
        Guid? targetCategoryId = request.TargetItem switch
        {
            CategoryItemViewModel category => category.Id,
            ProfileItemViewModel targetProfile => targetProfile.CategoryId,
            _ => request.TargetParentId
        };
        if (targetCategoryId is null || Categories.All(category => category.Id != targetCategoryId.Value)) return false;

        var sourceProfiles = _allProfiles.Where(item => item.CategoryId == sourceCategoryId)
            .OrderBy(item => item.SortOrder).ToList();
        var oldIndex = sourceProfiles.IndexOf(profile);
        if (oldIndex < 0) return false;

        var targetProfiles = sourceCategoryId == targetCategoryId.Value
            ? sourceProfiles
            : _allProfiles.Where(item => item.CategoryId == targetCategoryId.Value)
                .OrderBy(item => item.SortOrder).ToList();
        var insertionIndex = request.TargetItem is CategoryItemViewModel
            ? targetProfiles.Count
            : Math.Clamp(request.TargetIndex, 0, targetProfiles.Count);
        if (ReferenceEquals(sourceProfiles, targetProfiles) && insertionIndex > oldIndex) insertionIndex--;
        insertionIndex = Math.Clamp(insertionIndex, 0, Math.Max(0, targetProfiles.Count -
            (ReferenceEquals(sourceProfiles, targetProfiles) ? 1 : 0)));
        if (sourceCategoryId == targetCategoryId.Value && insertionIndex == oldIndex) return false;

        RecordStructuralUndo(sourceCategoryId == targetCategoryId.Value ? "drag-profile" : "drag-profile-category");
        sourceProfiles.Remove(profile);
        if (!ReferenceEquals(sourceProfiles, targetProfiles)) targetProfiles.Remove(profile);
        insertionIndex = Math.Clamp(insertionIndex, 0, targetProfiles.Count);
        targetProfiles.Insert(insertionIndex, profile);
        profile.MoveToCategory(targetCategoryId.Value);
        for (var index = 0; index < sourceProfiles.Count; index++) sourceProfiles[index].SortOrder = index;
        for (var index = 0; index < targetProfiles.Count; index++) targetProfiles[index].SortOrder = index;

        var targetCategory = Categories.First(category => category.Id == targetCategoryId.Value);
        if (!ReferenceEquals(SelectedCategory, targetCategory)) SelectedCategory = targetCategory;
        else RefreshProfiles();
        SelectedProfile = Profiles.FirstOrDefault(item => item.Id == profile.Id) ?? Profiles.FirstOrDefault();
        MarkDirty(_localizationService.GetString(sourceCategoryId == targetCategoryId.Value
            ? "Status.ProfileOrderChanged"
            : "Status.ProfileCategoryChanged"));
        return true;
    }

    private bool ReorderAction(ReorderDropRequest request)
    {
        if (SelectedProfile is null || request.Item is not ActionItemViewModel action) return false;
        var actions = SelectedProfile.Actions;
        var oldIndex = actions.IndexOf(action);
        if (oldIndex < 0) return false;
        var insertionIndex = Math.Clamp(request.TargetIndex, 0, actions.Count);
        var newIndex = insertionIndex > oldIndex ? insertionIndex - 1 : insertionIndex;
        newIndex = Math.Clamp(newIndex, 0, actions.Count - 1);
        if (newIndex == oldIndex) return false;

        RecordStructuralUndo("drag-action");
        actions.Move(oldIndex, newIndex);
        SelectedAction = action;
        MarkDirty(_localizationService.GetString("Status.ActionOrderChanged"));
        return true;
    }

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

    public void UpdateActivityPanelRatio(double ratio)
    {
        ActivityPanelHeightRatio = Math.Clamp(ratio, 0.2, 0.8);
        _userSettings.ActivityPanelHeightRatio = ActivityPanelHeightRatio;
        OnPropertyChanged(nameof(TopContentRowHeight));
            OnPropertyChanged(nameof(ActivityContentRowHeight));
            OnPropertyChanged(nameof(ActivityMinimumHeight));
        _ = _settingsRepository.SaveAsync(_userSettings);
    }

    public void ResetActivityPanelRatio() => UpdateActivityPanelRatio(0.5);

    private bool CanRefreshCurrentStates() => _statusMonitoring is not null && SelectedProfile is not null && !IsStatusRefreshing;

    private async Task RefreshCurrentStatesAsync()
    {
        if (_statusMonitoring is null || SelectedProfile is null || IsStatusRefreshing) return;
        IsStatusRefreshing = true;
        try
        {
            await _statusMonitoring.RefreshSelectedProfileAsync(SelectedProfile.Actions);
        }
        catch (Exception exception)
        {
            _activityService?.Add(ActivityLevel.Error,
                _localizationService.Format("Activity.RefreshFailed", exception.Message));
        }
        finally { IsStatusRefreshing = false; }
    }

    private bool CanRunProfile() => SelectedProfile is not null &&
        SelectedProfile.Actions.All(action => !action.IsEnabled || action.IsValid) &&
        ProfileReferencesAreValid(SelectedProfile.Id) &&
        !IsProfileRunning && !IsRestoreRunning && !IsSaving && !_profileRunner.IsRunning;

    private bool ProfileReferencesAreValid(Guid rootProfileId)
    {
        var profiles = _allProfiles.ToDictionary(item => item.Id, item => item.ToModel());
        var visiting = new HashSet<Guid>();
        var visited = new HashSet<Guid>();
        return Visit(rootProfileId);

        bool Visit(Guid id)
        {
            if (!profiles.TryGetValue(id, out var profile)) return false;
            if (visited.Contains(id)) return true;
            if (!visiting.Add(id)) return false;
            foreach (var target in profile.Actions.SelectMany(EnumerateProfileTargets))
                if (!Visit(target)) return false;
            visiting.Remove(id);
            visited.Add(id);
            return true;
        }
    }

    private static IEnumerable<Guid> EnumerateProfileTargets(ActionDefinition action)
    {
        if (action.Type == ActionTypeIds.ProfileRun &&
            Guid.TryParse(action.Parameters[ActionParameterNames.ProfileId]?.GetValue<string>(), out var id)) yield return id;
        if (action.Type != ActionTypeIds.ConditionIf) yield break;
        foreach (var name in new[] { ActionParameterNames.ThenActions, ActionParameterNames.ElseActions })
            if (action.Parameters[name] is JsonArray array)
                foreach (var node in array)
                {
                    ActionDefinition? nested = null;
                    try { nested = node?.Deserialize<ActionDefinition>(); } catch (JsonException) { }
                    if (nested is null) continue;
                    foreach (var target in EnumerateProfileTargets(nested)) yield return target;
                }
    }

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

    private bool CanDiscardPendingRestore() => _pendingRestoreSession is not null && RestoreChangeCount > 0 &&
        !IsProfileRunning && !IsRestoreRunning && !IsSaving;

    private async Task DiscardPendingRestoreAsync()
    {
        var session = _pendingRestoreSession;
        if (session is null || !CanDiscardPendingRestore()) return;
        if (!_dialogService.Confirm(_localizationService.GetString("Restore.DiscardTitle"),
                _localizationService.GetString("Restore.DiscardConfirm"))) return;
        var discardedItems = session.DiscardPendingRestore();
        foreach (var item in discardedItems)
        {
            item.RestoreStatus = PersistentActionRestoreStatus.NotRequired;
            item.RestoreMessage = _localizationService.GetString("Restore.Discarded");
            _activityService?.Record(new PersistentActivityRecord
            {
                SessionId = session.SessionId,
                ProfileId = item.ProfileId == Guid.Empty ? session.ProfileId : item.ProfileId,
                ProfileName = session.ProfileName,
                ActionId = item.ActionId,
                ActionType = item.ActionType,
                FriendlyName = GetRestorePreviewName(item),
                EventType = ActivityEventTypes.Discard,
                Level = ActivityLevel.Warning,
                StateBefore = item.PreviousState?.DeepClone().AsObject(),
                StateAfter = item.StateAfter?.DeepClone().AsObject(),
                Result = "discarded",
                RestoreStatus = SystemChangeStatuses.Discarded,
                Message = _localizationService.Format("Activity.ChangeDiscarded", GetRestorePreviewName(item))
            });
        }
        await _sessionRepository.SaveAsync(session);
        _activityService?.Add(ActivityLevel.Warning,
            _localizationService.Format("Activity.RestoreDiscarded", session.ProfileName), session.ProfileId);
        _pendingRestoreSession = null;
        SetRestoreCount(0);
        RestoreNoticeText = _localizationService.GetString("Restore.Discarded");
    }

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
            OnPropertyChanged(nameof(RestorePreviewText));
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
        OnPropertyChanged(nameof(RestorePreviewText));
        RestoreProfileCommand.NotifyCanExecuteChanged();
        DiscardPendingRestoreCommand.NotifyCanExecuteChanged();
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
        if (action is null || action.Type is not (ActionTypeIds.ProcessSetState or ActionTypeIds.ProcessConfigure or
            ActionTypeIds.WaitProcessStart or ActionTypeIds.WaitProcessExit or ActionTypeIds.WaitWindow))
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

    private void SelectAudio(ActionItemViewModel? action, bool input)
    {
        if (action is null || action.Type != ActionTypeIds.AudioConfigure) return;
        var selected = _dialogService.SelectAudioDevice(
            _localizationService.GetString(input ? "Dialog.SelectAudioInput" : "Dialog.SelectAudioOutput"), input);
        if (selected is null) return;
        RunGroupedConfigurationChange(input ? "select-audio-input" : "select-audio-output", () =>
        {
            if (input) { action.AudioInputDeviceId = selected.Id; action.AudioInputDeviceName = selected.FriendlyName; }
            else { action.AudioOutputDeviceId = selected.Id; action.AudioOutputDeviceName = selected.FriendlyName; }
            action.TrySetSuggestedName(selected.FriendlyName);
        });
    }

    private void SelectDevice(ActionItemViewModel? action)
    {
        if (action is null || action.Type != ActionTypeIds.DeviceSetState) return;
        var selected = _dialogService.SelectDevice(_localizationService.GetString("Dialog.SelectDevice"));
        if (selected is null) return;
        RunGroupedConfigurationChange("select-device", () =>
        {
            action.DeviceInstanceId = selected.InstanceId;
            action.DeviceFriendlyName = selected.FriendlyName;
            action.DeviceClass = selected.DeviceClass;
            action.TrySetSuggestedName(selected.FriendlyName);
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

        var customTheme = FindCustomTheme(option.Id);
        var appliedThemeId = _themeManager.ApplyTheme(option.Id, customTheme?.Colors);
        _userSettings.SchemaVersion = SettingsSchema.CurrentVersion;
        _userSettings.ThemeId = appliedThemeId;
        UpdateActiveThemeMarker();

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

    private async Task AddThemeAsync()
    {
        var previous = CaptureActiveTheme();
        var draftId = CustomThemeDefinition.CreateId();
        var draft = CustomThemeSettings.CreateDefault();
        var result = EditThemeDraft(new(CustomThemeEditMode.Add, string.Empty, draft,
            GetUnavailableThemeNames(), draftId, colors => _themeManager.ApplyTemporary(draftId, colors)), previous);
        if (result is null) return;
        var now = DateTimeOffset.UtcNow;
        var definition = new CustomThemeDefinition
        {
            Id = draftId, Name = result.Name, Colors = result.Colors.Clone(), CreatedAt = now, UpdatedAt = now
        };
        _userSettings.CustomThemes.Add(definition);
        var option = new ThemeOptionViewModel(definition, _localizationService);
        ThemeOptions.Add(option);
        await SelectApplyAndSaveThemeAsync(option, "CustomTheme.AddedStatus");
    }

    private async Task EditThemeAsync(string? themeId)
    {
        if (string.IsNullOrWhiteSpace(themeId)) return;
        if (FindCustomTheme(themeId) is null)
        {
            await DuplicateThemeAsync(themeId);
            return;
        }
        await EditCustomThemeByIdAsync(themeId, CustomThemeEditMode.EditCustom);
    }

    private async Task DuplicateThemeAsync(string? sourceThemeId)
    {
        if (string.IsNullOrWhiteSpace(sourceThemeId)) return;
        var source = GetThemeSourceById(sourceThemeId);
        if (source is null) return;

        var previous = CaptureActiveTheme();
        var draftId = CustomThemeDefinition.CreateId();
        var draftName = CreateUniqueThemeName(_localizationService.Format("CustomTheme.CopyName", source.Name));
        var mode = source.IsBuiltIn ? CustomThemeEditMode.CopyBuiltIn : CustomThemeEditMode.DuplicateCustom;
        var result = EditThemeDraft(new(mode, draftName, source.Colors.Clone(), GetUnavailableThemeNames(), draftId,
            colors => _themeManager.ApplyTemporary(draftId, colors)), previous);
        if (result is null) return;

        var now = DateTimeOffset.UtcNow;
        var duplicate = new CustomThemeDefinition
        {
            Id = draftId,
            Name = result.Name,
            Colors = result.Colors.Clone(),
            CreatedAt = now,
            UpdatedAt = now,
            IsBuiltIn = false
        };
        _userSettings.CustomThemes.Add(duplicate);
        var duplicateOption = new ThemeOptionViewModel(duplicate, _localizationService);
        ThemeOptions.Add(duplicateOption);
        await SelectApplyAndSaveThemeAsync(duplicateOption, "CustomTheme.DuplicatedStatus");
    }

    private async Task EditCustomThemeByIdAsync(string themeId, CustomThemeEditMode mode)
    {
        var custom = FindCustomTheme(themeId);
        if (custom is null) return;
        var previous = CaptureActiveTheme();
        var result = EditThemeDraft(new(mode, custom.Name, custom.Colors.Clone(),
            GetUnavailableThemeNames(themeId), themeId,
            colors => _themeManager.ApplyTemporary(themeId, colors)), previous);
        if (result is null) return;

        // Resolve again by the same stable ID in case the modal dialog was open for a while.
        custom = FindCustomTheme(themeId);
        if (custom is null) return;
        custom.Name = result.Name;
        custom.Colors = result.Colors.Clone();
        custom.UpdatedAt = DateTimeOffset.UtcNow;
        var option = GetThemeOptionById(themeId);
        if (option is not null) option.DisplayName = custom.Name;
        var editedOption = GetThemeOptionById(themeId);
        if (editedOption is null) return;
        await SelectApplyAndSaveThemeAsync(editedOption, "CustomTheme.UpdatedStatus");
    }

    private async Task RenameThemeAsync(string? themeId)
    {
        if (string.IsNullOrWhiteSpace(themeId) || FindCustomTheme(themeId) is not { } custom) return;
        var name = _customThemeEditorService.Rename(custom.Name, GetUnavailableThemeNames(themeId));
        if (name is null) return;
        custom = FindCustomTheme(themeId);
        if (custom is null) return;
        custom.Name = name.Trim();
        custom.UpdatedAt = DateTimeOffset.UtcNow;
        var option = GetThemeOptionById(themeId);
        if (option is not null) option.DisplayName = custom.Name;
        await SaveThemeCollectionAsync("CustomTheme.RenamedStatus");
    }

    private async Task DeleteThemeAsync(string? themeId)
    {
        if (string.IsNullOrWhiteSpace(themeId) || FindCustomTheme(themeId) is not { } custom) return;
        if (!_dialogService.Confirm(_localizationService.GetString("CustomTheme.Delete"),
                _localizationService.Format("CustomTheme.DeleteConfirm", custom.Name))) return;
        var wasActive = string.Equals(_userSettings.ThemeId, themeId, StringComparison.OrdinalIgnoreCase);
        _userSettings.CustomThemes.Remove(custom);
        var option = GetThemeOptionById(themeId);
        if (option is not null) ThemeOptions.Remove(option);
        if (wasActive)
        {
            var fallback = GetThemeOptionById(ThemeIds.Graphite)!;
            _selectedThemeOption = fallback;
            OnPropertyChanged(nameof(SelectedThemeOption));
            _themeManager.ApplyTheme(fallback.Id);
            _userSettings.ThemeId = fallback.Id;
            UpdateActiveThemeMarker();
        }
        await SaveThemeCollectionAsync("CustomTheme.DeletedStatus");
    }

    private async Task SelectApplyAndSaveThemeAsync(ThemeOptionViewModel option, string statusKey)
    {
        _selectedThemeOption = option;
        OnPropertyChanged(nameof(SelectedThemeOption));
        var custom = FindCustomTheme(option.Id);
        _userSettings.ThemeId = _themeManager.ApplyTheme(option.Id, custom?.Colors);
        UpdateActiveThemeMarker();
        await SaveThemeCollectionAsync(statusKey);
    }

    private async Task SaveThemeCollectionAsync(string successStatusKey)
    {
        try
        {
            _userSettings.SchemaVersion = SettingsSchema.CurrentVersion;
            await _settingsRepository.SaveAsync(_userSettings);
            StatusMessage = _localizationService.GetString(successStatusKey);
        }
        catch (Exception exception)
        {
            StatusMessage = _localizationService.Format("Status.SettingsSaveFailed", exception.Message);
        }
    }

    private CustomThemeDefinition? FindCustomTheme(string id) => _userSettings.CustomThemes.FirstOrDefault(theme =>
        string.Equals(theme.Id, id, StringComparison.OrdinalIgnoreCase));

    private ThemeOptionViewModel? GetThemeOptionById(string id) => ThemeOptions.FirstOrDefault(option =>
        string.Equals(option.Id, id, StringComparison.OrdinalIgnoreCase));

    private ThemeSource? GetThemeSourceById(string id)
    {
        if (FindCustomTheme(id) is { } custom)
            return new(custom.Id, custom.Name, custom.Colors.Clone(), false);
        var builtIn = _themeManager.AvailableThemes.FirstOrDefault(theme =>
            string.Equals(theme.Id, id, StringComparison.OrdinalIgnoreCase));
        if (builtIn is null) return null;
        var displayName = GetThemeOptionById(builtIn.Id)?.DisplayName
                          ?? _localizationService.GetString(builtIn.DisplayNameResourceKey);
        return new(builtIn.Id, displayName, _themeManager.CreateEditableCopy(builtIn.Id), true);
    }

    private IReadOnlyCollection<string> GetUnavailableThemeNames(string? excludedId = null) => ThemeOptions
        .Where(option => !string.Equals(option.Id, excludedId, StringComparison.OrdinalIgnoreCase))
        .Select(option => option.DisplayName).ToArray();

    private string CreateUniqueThemeName(string baseName)
    {
        var unavailable = new HashSet<string>(GetUnavailableThemeNames(), StringComparer.CurrentCultureIgnoreCase);
        var candidate = baseName;
        var suffix = 2;
        while (unavailable.Contains(candidate)) candidate = $"{baseName} ({suffix++})";
        return candidate;
    }

    private void UpdateActiveThemeMarker()
    {
        foreach (var option in ThemeOptions)
            option.IsActive = string.Equals(option.Id, _selectedThemeOption?.Id, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record ThemeSource(string Id, string Name, CustomThemeSettings Colors, bool IsBuiltIn);

    private CustomThemeEditResult? EditThemeDraft(CustomThemeEditRequest request, AppliedThemeSnapshot previous)
    {
        try
        {
            _themeManager.ApplyTemporary(request.ThemeId ?? CustomThemeDefinition.CreateId(), request.Colors);
            var result = _customThemeEditorService.Edit(request);
            if (result is null) RestoreActiveTheme(previous);
            return result;
        }
        catch
        {
            RestoreActiveTheme(previous);
            throw;
        }
    }

    private AppliedThemeSnapshot CaptureActiveTheme()
    {
        var activeId = _userSettings.ThemeId;
        return new(activeId, FindCustomTheme(activeId)?.Colors.Clone());
    }

    private void RestoreActiveTheme(AppliedThemeSnapshot snapshot)
    {
        _themeManager.ApplyTheme(snapshot.ThemeId, snapshot.Colors?.Clone());
        var option = GetThemeOptionById(snapshot.ThemeId);
        if (option is not null)
        {
            _selectedThemeOption = option;
            OnPropertyChanged(nameof(SelectedThemeOption));
        }
        UpdateActiveThemeMarker();
    }

    private sealed record AppliedThemeSnapshot(string ThemeId, CustomThemeSettings? Colors);

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
        RefreshPersistentActivityViews();
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
        OnPropertyChanged(nameof(AllProfiles));
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
            [ActionParameterNames.DesiredState] = ServiceDesiredStateIds.Unchanged,
            [ActionParameterNames.ServiceStartupType] = ServiceStartupTypeIds.Unchanged
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
        ActionTypeIds.ProcessConfigure => new JsonObject
        {
            [ActionParameterNames.ChangeAffinity] = true,
            [ActionParameterNames.ChangePriority] = false,
            [ActionParameterNames.ProcessPriority] = ProcessPriorityIds.Normal
        },
        ActionTypeIds.WaitWindow => new JsonObject
        {
            [ActionParameterNames.WindowMatchMode] = WindowMatchModeIds.Any
        },
        ActionTypeIds.AudioConfigure => new JsonObject
        {
            [ActionParameterNames.SetDefaultMultimedia] = true,
            [ActionParameterNames.SetDefaultCommunications] = false
        },
        ActionTypeIds.DeviceSetState => new JsonObject
        {
            [ActionParameterNames.DesiredState] = DeviceStateIds.Unchanged
        },
        ActionTypeIds.ConditionIf => new JsonObject
        {
            [ActionParameterNames.ConditionType] = ConditionTypeIds.ProcessRunning,
            [ActionParameterNames.ThenActions] = new JsonArray(),
            [ActionParameterNames.ElseActions] = new JsonArray()
        },
        ActionTypeIds.NotificationShow => new JsonObject
        {
            [ActionParameterNames.NotificationLevel] = NotificationLevelIds.Info
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
