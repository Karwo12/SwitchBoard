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
using SwitchBoard.ViewModels.Actions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Reflection;
using System.Runtime.InteropServices;
using SwitchBoard.Services.Logging;
using SwitchBoard.Services.Diagnostics;
using SwitchBoard.Services.Updates;
using SwitchBoard.Services.Tray;

namespace SwitchBoard.ViewModels;

public enum MainViewMode
{
    Home,
    Activity,
    Settings
}

public sealed class MainWindowViewModel : ObservableObject, IDisposable
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
    private object? _selectedRootNavigationItem;
    private MainViewMode _activeMainView = MainViewMode.Home;
    private ActionItemViewModel? _selectedAction;
    private ActionTypeOption? _selectedActionType;
    private string _actionPickerSearch = string.Empty;
    private bool _isActionPickerOpen;
    private ActionItemViewModel? _nestedActionTarget;
    private bool _nestedActionThenBranch;
    private ThemeOptionViewModel? _selectedThemeOption;
    private LanguageOptionViewModel? _selectedLanguageOption;
    private string _statusMessage;
    private bool _hasUnsavedChanges;
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
    private SwitchBoardCatalog _savedCatalogBaseline;
    private bool _suppressUndoTracking;
    private bool _isRestoreRunning;
    private bool _isSaving;
    private bool _allowCloseWithoutConfirmation;
    private DateTimeOffset _lastAddActionAt;
    private DateTimeOffset _lastUndoAt;
    private PersistentExecutionSession? _pendingRestoreSession;
    private PersistentExecutionSession? _lastSingleActionTestSession;
    private int _restoreChangeCount;
    private string _restoreNoticeText = string.Empty;
    private readonly IActivityService? _activityService;
    private bool _isActivityExpanded;
    private int _activityAlertCount;
    private int _activityTabIndex;
    private double _activityPanelHeightRatio;
    private readonly StatusMonitoringService? _statusMonitoring;
    private readonly ThemeExchangeService? _themeExchangeService;
    private readonly AppDataPaths _appDataPaths;
    private readonly IStartupRegistrationService? _startupRegistrationService;
    private readonly SwitchBoardBackupService _backupService = new();
    private readonly ProfilePreflightService _preflightService = new();
    private readonly DiagnosticExportService _diagnosticExportService;
    private readonly IUpdateService? _updateService;
    private readonly LogMaintenanceService _logMaintenanceService;
    private readonly ActionPickerCatalog _actionPickerCatalog;
    private readonly ProfileExchangeService _profileExchangeService = new();
    private bool _isStatusRefreshing;
    private bool _statusRefreshQueued;
    private string _statusRefreshText = string.Empty;
    private DispatcherTimer? _statusMonitorTimer;
    private bool _isRestoringSelection;
    private CancellationTokenSource? _settingsSaveDebounce;
    private Task? _settingsSaveTask;
    private readonly object _settingsSaveSync = new();
    private bool _disposed;
    private string _profileSearchText = string.Empty;
    private string _activitySearchText = string.Empty;
    private string _activityStatusFilter = "all";
    private string _activityTimeRange = "all";
    private Guid? _activityProfileFilterId;
    private string _preflightSummary = string.Empty;
    private string _updateStatusText = string.Empty;
    private Uri? _latestReleaseUri;

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
        StatusMonitoringService? statusMonitoring = null,
        ThemeExchangeService? themeExchangeService = null,
        AppDataPaths? appDataPaths = null,
        IStartupRegistrationService? startupRegistrationService = null,
        IUpdateService? updateService = null)
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
        _themeExchangeService = themeExchangeService;
        _appDataPaths = appDataPaths ?? new AppDataPaths();
        _startupRegistrationService = startupRegistrationService;
        _updateService = updateService;
        _diagnosticExportService = new DiagnosticExportService(_appDataPaths);
        _logMaintenanceService = new LogMaintenanceService(_appDataPaths);
        _actionPickerCatalog = new ActionPickerCatalog(localizationService);
        _activityPanelHeightRatio = Math.Clamp(userSettings.ActivityPanelHeightRatio, 0.2, 0.8);
        _isActivityExpanded = userSettings.IsActivityExpanded;
        _activityTabIndex = NormalizeActivityTabIndex(userSettings.LastActivityTabIndex);
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
        RootProfiles = [];
        RootNavigationItems = [];
        FilteredRootNavigationItems = [];
        AvailableActionTypes = _actionPickerCatalog.CreateOptions();
        FilteredActionTypes = new ObservableCollection<ActionTypeOption>(AvailableActionTypes);
        ActionPickerView = CollectionViewSource.GetDefaultView(FilteredActionTypes);
        ActionPickerView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ActionTypeOption.Category)));
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
        CloseBehaviorOptions =
        [
            new SettingsOptionViewModel("close", localizationService.GetString("Settings.CloseSwitchBoard")),
            new SettingsOptionViewModel("tray", localizationService.GetString("Settings.MinimizeToTray"))
        ];
        ProfileColorOptions =
        [
            new ProfileAppearanceOption(null, localizationService.GetString("Settings.AppearanceNone")),
            new ProfileAppearanceOption("#4F8EF7", localizationService.GetString("Settings.ProfileColorBlue"), "#4F8EF7"),
            new ProfileAppearanceOption("#9B6BFF", localizationService.GetString("Settings.ProfileColorPurple"), "#9B6BFF"),
            new ProfileAppearanceOption("#44B78B", localizationService.GetString("Settings.ProfileColorGreen"), "#44B78B"),
            new ProfileAppearanceOption("#E6953B", localizationService.GetString("Settings.ProfileColorOrange"), "#E6953B"),
            new ProfileAppearanceOption("#D95D63", localizationService.GetString("Settings.ProfileColorRed"), "#D95D63")
        ];
        ProfileIconOptions =
        [
            new ProfileAppearanceOption(null, localizationService.GetString("Settings.AppearanceNone")),
            new ProfileAppearanceOption("bolt", localizationService.GetString("Settings.ProfileIconBolt")),
            new ProfileAppearanceOption("briefcase", localizationService.GetString("Settings.ProfileIconBriefcase")),
            new ProfileAppearanceOption("gamepad", localizationService.GetString("Settings.ProfileIconGamepad")),
            new ProfileAppearanceOption("monitor", localizationService.GetString("Settings.ProfileIconMonitor")),
            new ProfileAppearanceOption("moon", localizationService.GetString("Settings.ProfileIconMoon"))
        ];
        ActivityStatusOptions =
        [
            new SettingsOptionViewModel("all", localizationService.GetString("Activity.Filter.AllStatuses")),
            new SettingsOptionViewModel("success", localizationService.GetString("NotificationLevel.Success")),
            new SettingsOptionViewModel("warning", localizationService.GetString("NotificationLevel.Warning")),
            new SettingsOptionViewModel("error", localizationService.GetString("NotificationLevel.Error"))
        ];
        ActivityTimeRangeOptions =
        [
            new SettingsOptionViewModel("all", localizationService.GetString("Activity.Filter.AllTime")),
            new SettingsOptionViewModel("today", localizationService.GetString("Activity.Filter.Today")),
            new SettingsOptionViewModel("7d", localizationService.GetString("Activity.Filter.Last7Days")),
            new SettingsOptionViewModel("30d", localizationService.GetString("Activity.Filter.Last30Days"))
        ];
        ActivityProfileOptions = new ObservableCollection<ProfileFilterOption>();
        InterfaceDensityOptions =
        [
            new SettingsOptionViewModel("standard", localizationService.GetString("Settings.Density.Standard")),
            new SettingsOptionViewModel("compact", localizationService.GetString("Settings.Density.Compact"))
        ];
        ApplyInterfaceDensityResources();

        AddCategoryCommand = new RelayCommand(AddCategory);
        DeleteCategoryCommand = new RelayCommand<CategoryItemViewModel>(DeleteCategory, category => category is not null);
        AddProfileCommand = new RelayCommand(AddProfile);
        DeleteProfileCommand = new RelayCommand<ProfileItemViewModel>(DeleteProfile, profile => profile is not null);
        DuplicateProfileCommand = new RelayCommand<ProfileItemViewModel>(DuplicateProfile, profile => profile is not null && !HasCriticalOperation);
        ExportProfileCommand = new AsyncRelayCommand<ProfileItemViewModel>(ExportProfileAsync, profile => profile is not null);
        SetProfileColorCommand = new RelayCommand<string>(SetSelectedProfileColor, _ => SelectedProfile is not null && !HasCriticalOperation);
        SetProfileIconCommand = new RelayCommand<string>(SetSelectedProfileIcon, _ => SelectedProfile is not null && !HasCriticalOperation);
        ImportProfileCommand = new AsyncRelayCommand(ImportProfileAsync, () => !HasCriticalOperation);
        AddActionCommand = new RelayCommand(AddAction, () => SelectedProfile is not null && SelectedActionType is not null && !HasCriticalOperation);
        ToggleActionPickerCommand = new RelayCommand(ToggleMainActionPicker);
        SelectActionTypeCommand = new RelayCommand<ActionTypeOption>(SelectActionType, option => option is not null);
        OpenThenActionPickerCommand = new RelayCommand<ActionItemViewModel>(action => OpenNestedActionPicker(action, true),
            action => action?.CanAddNestedActions == true);
        OpenElseActionPickerCommand = new RelayCommand<ActionItemViewModel>(action => OpenNestedActionPicker(action, false),
            action => action?.CanAddNestedActions == true);
        DeleteActionCommand = new RelayCommand<ActionItemViewModel>(DeleteAction, action => action is not null);
        DuplicateActionCommand = new RelayCommand<ActionItemViewModel>(DuplicateAction, action => action is not null && SelectedProfile is not null && !HasCriticalOperation);
        TestActionCommand = new AsyncRelayCommand<ActionItemViewModel>(TestActionAsync, CanTestAction);
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
        AddThemeCommand = new AsyncRelayCommand(AddThemeAsync);
        ImportThemeCommand = new AsyncRelayCommand(ImportThemeAsync);
        ExportThemeCommand = new AsyncRelayCommand<string>(ExportThemeAsync, id => FindCustomTheme(id ?? string.Empty) is not null);
        EditThemeCommand = new AsyncRelayCommand<string>(EditThemeAsync, id => !string.IsNullOrWhiteSpace(id));
        DuplicateThemeCommand = new AsyncRelayCommand<string>(DuplicateThemeAsync, id => !string.IsNullOrWhiteSpace(id));
        RenameThemeCommand = new AsyncRelayCommand<string>(RenameThemeAsync, id => FindCustomTheme(id ?? string.Empty) is not null);
        DeleteThemeCommand = new AsyncRelayCommand<string>(DeleteThemeAsync, id => FindCustomTheme(id ?? string.Empty) is not null);
        BrowseProgramCommand = new RelayCommand<ActionItemViewModel>(BrowseProgram, action => action?.Type == ActionTypeIds.ProgramRun);
        SelectArgumentsCommand = new RelayCommand<ActionItemViewModel>(SelectArguments, action => action?.Type == ActionTypeIds.ProgramRun);
        BrowseWorkingDirectoryCommand = new RelayCommand<ActionItemViewModel>(BrowseWorkingDirectory,
            action => action?.Type == ActionTypeIds.ProgramRun && action.UseCustomWorkingDirectory);
        FindProgramCommand = new RelayCommand<ActionItemViewModel>(FindProgram, action => action?.Type == ActionTypeIds.ProgramRun);
         SelectProcessCommand = new RelayCommand<ActionItemViewModel>(SelectProcess, action => action?.Type is
             ActionTypeIds.ProgramRun or ActionTypeIds.ScriptRun or ActionTypeIds.ProcessConfigure or ActionTypeIds.WaitProcessStart or
             ActionTypeIds.WaitProcessExit or ActionTypeIds.WaitWindow);
        SelectServiceCommand = new RelayCommand<ActionItemViewModel>(SelectService, action => action?.Type == ActionTypeIds.ServiceSetState);
        SelectPowerPlanCommand = new RelayCommand<ActionItemViewModel>(SelectPowerPlan, action => action?.Type == ActionTypeIds.PowerSetPlan);
        SelectDisplayCommand = new RelayCommand<ActionItemViewModel>(SelectDisplay, action => action?.Type == ActionTypeIds.DisplayConfigure);
        BrowseScriptCommand = new RelayCommand<ActionItemViewModel>(BrowseScript, action => action?.Type == ActionTypeIds.ScriptRun);
        BrowseRestoreScriptCommand = new RelayCommand<ActionItemViewModel>(BrowseRestoreScript, action => action?.Type == ActionTypeIds.ScriptRun);
        SelectAllCpusCommand = new RelayCommand<ActionItemViewModel>(action => action?.SelectAllCpus(true),
            action => action?.Type is ActionTypeIds.ProgramRun or ActionTypeIds.ProcessConfigure);
        ClearAllCpusCommand = new RelayCommand<ActionItemViewModel>(action => action?.SelectAllCpus(false),
            action => action?.Type is ActionTypeIds.ProgramRun or ActionTypeIds.ProcessConfigure);
        SelectAllExceptCpu0Command = new RelayCommand<ActionItemViewModel>(action => action?.SelectAllExceptCpu0(),
            action => action?.Type is ActionTypeIds.ProgramRun or ActionTypeIds.ProcessConfigure);
        SelectAudioOutputCommand = new RelayCommand<ActionItemViewModel>(action => SelectAudio(action, false),
            action => action?.Type == ActionTypeIds.AudioConfigure);
        SelectAudioInputCommand = new RelayCommand<ActionItemViewModel>(action => SelectAudio(action, true),
            action => action?.Type == ActionTypeIds.AudioConfigure);
        SelectDeviceCommand = new RelayCommand<ActionItemViewModel>(SelectDevice,
            action => action?.Type == ActionTypeIds.DeviceSetState);
        ToggleActionExpandedCommand = new RelayCommand<ActionItemViewModel>(ToggleActionExpanded, action => action is not null);
        NavigateToValidationErrorCommand = new RelayCommand(NavigateToValidationError, () => FindFirstValidationError() is not null);
        ToggleAdvancedOptionsCommand = new RelayCommand<ActionItemViewModel>(action =>
        {
            if (action is not null) action.IsAdvancedOptionsExpanded = !action.IsAdvancedOptionsExpanded;
        }, action => action is not null);
        RunProfileCommand = new AsyncRelayCommand(RunProfileAsync, CanRunProfile);
        RestoreProfileCommand = new AsyncRelayCommand(RestoreProfileAsync, CanRestoreProfile);
        // The X button is always rendered. Resolve the pending session when it is clicked
        // instead of relying on a potentially stale CanExecute state after a prior discard.
        DiscardPendingRestoreCommand = new AsyncRelayCommand(DiscardPendingRestoreAsync);
        UndoSingleActionTestCommand = new AsyncRelayCommand(UndoSingleActionTestAsync, () => CanUndoSingleActionTest);
        RefreshCurrentStatesCommand = new AsyncRelayCommand(RefreshCurrentStatesAsync, CanRefreshCurrentStates);
        CancelProfileCommand = new RelayCommand(CancelProfile, () => IsProfileRunning || IsRestoreRunning);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsProfileRunning && !IsRestoreRunning);
        UndoCommand = new RelayCommand<string>(Undo, _ => _undoService.CanUndo && !HasCriticalOperation);
        RedoCommand = new RelayCommand<string>(Redo, _ => _undoService.CanRedo && !HasCriticalOperation);
        ToggleActivityCommand = new RelayCommand(() => IsActivityExpanded = !IsActivityExpanded);
        ClearActivityCommand = new RelayCommand(ClearActivity);
        SelectActivityTabCommand = new RelayCommand<string>(index =>
        {
            ActivityTabIndex = int.TryParse(index, out var parsed) ? parsed : 2;
        });
        OpenThemeFolderCommand = new RelayCommand(() => OpenDirectory(_appDataPaths.CustomThemeDirectory));
        ExportBackupCommand = new AsyncRelayCommand(ExportBackupAsync, () => !HasCriticalOperation);
        ImportBackupCommand = new AsyncRelayCommand(ImportBackupAsync, () => !HasCriticalOperation);
        OpenDataFolderCommand = new RelayCommand(() => OpenDirectory(_appDataPaths.RootDirectory));
        OpenLogsFolderCommand = new RelayCommand(() => OpenDirectory(_appDataPaths.LogsDirectory));
        ClearLogsCommand = new RelayCommand(ClearLogs);
        CopyDiagnosticsCommand = new RelayCommand(CopyDiagnostics);
        ExportDiagnosticsCommand = new AsyncRelayCommand(ExportDiagnosticsAsync);
        ExportHistoryCommand = new AsyncRelayCommand(ExportHistoryAsync);
        ResetSettingsCommand = new AsyncRelayCommand(ResetSettingsAsync, () => !HasCriticalOperation);
        ResetAllDataCommand = new AsyncRelayCommand(ResetAllDataAsync, () => !HasCriticalOperation);
        CheckUpdatesCommand = new AsyncRelayCommand(CheckUpdatesAsync, () => _updateService is not null);
        OpenLatestReleaseCommand = new RelayCommand(OpenLatestRelease, () => _latestReleaseUri is not null);
        OpenRepositoryCommand = new RelayCommand(OpenRepository);
        ClearActivityFiltersCommand = new RelayCommand(ClearActivityFilters);

        ActivityEntries = new ObservableCollection<ActivityEntry>(activityService?.Entries ?? []);
        ActivityDisplayEntries = new ObservableCollection<ActivityEntryViewModel>();
        RefreshActivityDisplayEntries();
        HistoryEntries = [];
        SystemChangeEntries = [];
        if (activityService is not null)
        {
            activityService.EntryAdded += ActivityServiceOnEntryAdded;
            activityService.PersistentViewsChanged += ActivityServiceOnPersistentViewsChanged;
            RefreshPersistentActivityViews();
        }

        RefreshProfileGroups(catalog.RootNavigationOrder);
        RefreshActivityProfileOptions();
        SubscribeToItems();
        _undoBaseline = BuildCatalogSnapshot();
        _savedCatalogBaseline = BuildCatalogSnapshot();
        _isRestoringSelection = true;
        var restoredProfile = _allProfiles.FirstOrDefault(item => item.Id == userSettings.LastSelectedProfileId);
        SelectedCategory = restoredProfile is null
            ? Categories.FirstOrDefault(item => item.Id == userSettings.LastSelectedCategoryId) ?? Categories.FirstOrDefault()
            : FindCategory(restoredProfile.CategoryId);
        SelectedProfile = restoredProfile ?? Profiles.FirstOrDefault() ?? RootProfiles.FirstOrDefault();
        _isRestoringSelection = false;
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

    /// <summary>
    /// Profiles that deliberately have no category. Guid.Empty is the persisted
    /// root marker, so no catalog schema or additional data file is required.
    /// </summary>
    public ObservableCollection<ProfileItemViewModel> RootProfiles { get; }

    /// <summary>
    /// Mixed top-level sequence rendered by the Profile panel. Its entries are
    /// either a category or a profile whose CategoryId is Guid.Empty.
    /// </summary>
    public ObservableCollection<object> RootNavigationItems { get; }

    /// <summary>Home-only search result list; the catalog ordering remains in RootNavigationItems.</summary>
    public ObservableCollection<object> FilteredRootNavigationItems { get; }

    public string ProfileSearchText
    {
        get => _profileSearchText;
        set
        {
            if (SetProperty(ref _profileSearchText, value ?? string.Empty)) RefreshProfileSearchPresentation();
        }
    }

    public object? SelectedRootNavigationItem
    {
        get => _selectedRootNavigationItem;
        set
        {
            if (!SetProperty(ref _selectedRootNavigationItem, value)) return;
            switch (value)
            {
                case ProfileItemViewModel profile:
                    SelectedProfile = profile;
                    break;
                case CategoryItemViewModel category:
                    SelectedCategory = category;
                    break;
            }
        }
    }

    public Guid RootCategoryId => Guid.Empty;

    public IReadOnlyList<ActionTypeOption> AvailableActionTypes { get; }
    public ObservableCollection<ActionTypeOption> FilteredActionTypes { get; }
    public ICollectionView ActionPickerView { get; }
    public string ActionPickerSearch
    {
        get => _actionPickerSearch;
        set { if (SetProperty(ref _actionPickerSearch, value)) FilterActionTypes(); }
    }
    public bool IsActionPickerOpen
    {
        get => _isActionPickerOpen;
        set { if (SetProperty(ref _isActionPickerOpen, value) && value) ActionPickerSearch = string.Empty; }
    }

    public ObservableCollection<ThemeOptionViewModel> ThemeOptions { get; }

    public ObservableCollection<LanguageOptionViewModel> LanguageOptions { get; }
    public IReadOnlyList<SettingsOptionViewModel> CloseBehaviorOptions { get; }
    public IReadOnlyList<SettingsOptionViewModel> InterfaceDensityOptions { get; }
    public IReadOnlyList<ProfileAppearanceOption> ProfileColorOptions { get; }
    public IReadOnlyList<ProfileAppearanceOption> ProfileIconOptions { get; }
    public IReadOnlyList<SettingsOptionViewModel> ActivityStatusOptions { get; }
    public IReadOnlyList<SettingsOptionViewModel> ActivityTimeRangeOptions { get; }
    public ObservableCollection<ProfileFilterOption> ActivityProfileOptions { get; }
    public ObservableCollection<ActivityEntry> ActivityEntries { get; }
    public ObservableCollection<ActivityEntryViewModel> ActivityDisplayEntries { get; }
    public ObservableCollection<ProfileExecutionViewModel> HistoryEntries { get; }
    public ObservableCollection<SystemChangeItemViewModel> SystemChangeEntries { get; }
    public IReadOnlyList<ProfileItemViewModel> AllProfiles => _allProfiles;

    public string ActivitySearchText
    {
        get => _activitySearchText;
        set { if (SetProperty(ref _activitySearchText, value ?? string.Empty)) RefreshFilteredActivityViews(); }
    }

    public string ActivityStatusFilter
    {
        get => _activityStatusFilter;
        set { if (SetProperty(ref _activityStatusFilter, NormalizeActivityStatus(value))) RefreshFilteredActivityViews(); }
    }

    public string ActivityTimeRange
    {
        get => _activityTimeRange;
        set { if (SetProperty(ref _activityTimeRange, NormalizeActivityTimeRange(value))) RefreshFilteredActivityViews(); }
    }

    public Guid? ActivityProfileFilterId
    {
        get => _activityProfileFilterId;
        set { if (SetProperty(ref _activityProfileFilterId, value)) RefreshFilteredActivityViews(); }
    }

    public string PreflightSummary
    {
        get => _preflightSummary;
        private set => SetProperty(ref _preflightSummary, value);
    }

    public string UpdateStatusText
    {
        get => _updateStatusText;
        private set => SetProperty(ref _updateStatusText, value);
    }

    public bool HasLatestRelease => _latestReleaseUri is not null;
    public MainViewMode ActiveMainView
    {
        get => _activeMainView;
        set
        {
            if (!SetProperty(ref _activeMainView, value)) return;
            if (_userSettings.RememberLastView)
            {
                _userSettings.LastMainView = value.ToString();
                ScheduleSettingsSave();
            }
        }
    }

    public MainViewMode InitialMainView => _userSettings.RememberLastView &&
        Enum.TryParse<MainViewMode>(_userSettings.LastMainView, ignoreCase: true, out var view)
        ? view
        : MainViewMode.Home;

    public bool RememberLastView
    {
        get => _userSettings.RememberLastView;
        set
        {
            if (_userSettings.RememberLastView == value) return;
            _userSettings.RememberLastView = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(InitialMainView));
            ScheduleSettingsSave();
        }
    }

    public bool WarnBeforeClosingWithUnsavedChanges
    {
        get => _userSettings.WarnBeforeClosingWithUnsavedChanges;
        set
        {
            if (_userSettings.WarnBeforeClosingWithUnsavedChanges == value) return;
            _userSettings.WarnBeforeClosingWithUnsavedChanges = value;
            OnPropertyChanged();
            ScheduleSettingsSave();
        }
    }

    public bool IsLaunchAtStartup
    {
        get => _userSettings.LaunchAtStartup;
        set
        {
            if (_userSettings.LaunchAtStartup == value) return;
            if (_startupRegistrationService is not null &&
                !_startupRegistrationService.TrySetEnabled(value, out var error))
            {
                StatusMessage = _localizationService.Format("Status.StartupRegistrationFailed", error ?? string.Empty);
                OnPropertyChanged();
                return;
            }

            _userSettings.LaunchAtStartup = value;
            OnPropertyChanged();
            ScheduleSettingsSave();
        }
    }

    public string CloseBehavior
    {
        get => _userSettings.CloseBehavior;
        set
        {
            var normalized = string.Equals(value, "tray", StringComparison.OrdinalIgnoreCase) ? "tray" : "close";
            if (string.Equals(_userSettings.CloseBehavior, normalized, StringComparison.OrdinalIgnoreCase)) return;
            _userSettings.CloseBehavior = normalized;
            OnPropertyChanged();
            ScheduleSettingsSave();
        }
    }

    public string InterfaceDensity
    {
        get => _userSettings.InterfaceDensity;
        set
        {
            var normalized = string.Equals(value, "compact", StringComparison.OrdinalIgnoreCase) ? "compact" : "standard";
            if (string.Equals(_userSettings.InterfaceDensity, normalized, StringComparison.OrdinalIgnoreCase)) return;
            _userSettings.InterfaceDensity = normalized;
            ApplyInterfaceDensityResources();
            OnPropertyChanged();
            ScheduleSettingsSave();
        }
    }

    public bool ShowCardDetails
    {
        get => _userSettings.ShowCardDetails;
        set
        {
            if (_userSettings.ShowCardDetails == value) return;
            _userSettings.ShowCardDetails = value;
            OnPropertyChanged();
            ScheduleSettingsSave();
        }
    }

    public bool AutomaticBackupEnabled
    {
        get => _userSettings.AutomaticBackupEnabled;
        set
        {
            if (_userSettings.AutomaticBackupEnabled == value) return;
            _userSettings.AutomaticBackupEnabled = value;
            OnPropertyChanged();
            ScheduleSettingsSave();
        }
    }

    public int AutomaticBackupCount
    {
        get => _userSettings.AutomaticBackupCount;
        set
        {
            var normalized = Math.Clamp(value, 1, 50);
            if (_userSettings.AutomaticBackupCount == normalized) return;
            _userSettings.AutomaticBackupCount = normalized;
            OnPropertyChanged();
            ScheduleSettingsSave();
        }
    }

    public string ApplicationVersion => Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
    public string RepositoryUrl => "https://github.com/Karwo12/SwitchBoard";
    public string DataDirectoryPath => _appDataPaths.RootDirectory;
    public string LogsDirectoryPath => _appDataPaths.LogsDirectory;
    public string ActiveThemeDisplayName => ThemeOptions.FirstOrDefault(item => item.IsActive)?.DisplayName ?? _userSettings.ThemeId;
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
            _userSettings.IsActivityExpanded = value;
            ScheduleSettingsSave();
        }
    }

    private void ExpandActivityPanelForTab()
    {
        if (ActivityPanelHeightRatio is < 0.2 or > 0.8)
            ActivityPanelHeightRatio = 0.5;
        IsActivityExpanded = true;
        OnPropertyChanged(nameof(ActivityContentRowHeight));
        OnPropertyChanged(nameof(TopContentRowHeight));
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
            ScheduleSettingsSave();
        }
    }
    public int WindowWidth
    {
        get => _userSettings.WindowWidth;
        set
        {
            var normalized = Math.Clamp(value, 900, 4096);
            if (_userSettings.WindowWidth == normalized) return;
            _userSettings.WindowWidth = normalized;
            OnPropertyChanged();
            ScheduleSettingsSave();
        }
    }
    public int WindowHeight
    {
        get => _userSettings.WindowHeight;
        set
        {
            var normalized = Math.Clamp(value, 500, 4096);
            if (_userSettings.WindowHeight == normalized) return;
            _userSettings.WindowHeight = normalized;
            OnPropertyChanged();
            ScheduleSettingsSave();
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
        set
        {
            var normalized = NormalizeActivityTabIndex(value);
            if (!SetProperty(ref _activityTabIndex, normalized)) return;
            _userSettings.LastActivityTabIndex = normalized;
            ScheduleSettingsSave();
        }
    }
    public int UnresolvedSystemChangeCount => SystemChangeEntries.Count(item => item.IsUnresolved);
    public string SystemChangeTabText => UnresolvedSystemChangeCount > 0
        ? _localizationService.Format("Activity.SystemChangesCount", UnresolvedSystemChangeCount)
        : _localizationService.GetString("Activity.SystemChanges");
    public string SystemChangeNoticeText => UnresolvedSystemChangeCount > 0
        ? _localizationService.Format("Activity.UnrestoredChanges", UnresolvedSystemChangeCount)
        : string.Empty;

    public string RunAvailabilityMessage
    {
        get
        {
            if (SelectedProfile is null) return string.Empty;
            var invalid = SelectedProfile.Actions.Count(action => action.IsEnabled && !action.IsComment && !action.IsValid);
            if (invalid > 0) return _localizationService.Format("Validation.RunBlocked", invalid);
            if (!ProfileReferencesAreValid(SelectedProfile.Id))
                return _localizationService.GetString("Validation.ProfileReferenceCycle");
            if (IsProfileRunning || IsRestoreRunning || IsSaving)
                return _localizationService.GetString("Validation.RunBusy");
            return string.Empty;
        }
    }
    public bool HasRunValidationIssue => !string.IsNullOrWhiteSpace(RunAvailabilityMessage);

    private ProfilePreflightResult? BuildPreflight(ProfileItemViewModel? profile = null)
    {
        var target = profile ?? SelectedProfile;
        if (target is null)
        {
            PreflightSummary = string.Empty;
            return null;
        }
        var result = _preflightService.Analyze(target, ProfileReferencesAreValid(target.Id));
        PreflightSummary = _localizationService.Format("Preflight.Summary", result.ReadyActionCount,
            result.WarningCount, result.ErrorCount);
        return result;
    }

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
            SelectedProfile = Profiles.FirstOrDefault();
            if (!_isRestoringSelection)
            {
                _userSettings.LastSelectedCategoryId = value?.Id;
                _userSettings.LastSelectedProfileId = SelectedProfile?.Id;
                ScheduleSettingsSave();
            }
            AddProfileCommand.NotifyCanExecuteChanged();
            ImportProfileCommand.NotifyCanExecuteChanged();
        }
    }

    public ProfileItemViewModel? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetProperty(ref _selectedProfile, value))
            {
                SynchronizeSelectedCategory(value);
                var rootNavigationSelection = value is not null && value.CategoryId == Guid.Empty ? value : null;
                if (!ReferenceEquals(_selectedRootNavigationItem, rootNavigationSelection))
                {
                    _selectedRootNavigationItem = rootNavigationSelection;
                    OnPropertyChanged(nameof(SelectedRootNavigationItem));
                }
                // Selecting a profile must not implicitly select its first action.
                // Action selection is a user/navigation concern; execution state is
                // reported independently through ActionItemViewModel.ExecutionState.
                SelectedAction = null;
                if (!_isRestoringSelection)
                {
                    _userSettings.LastSelectedProfileId = value?.Id;
                    _userSettings.LastSelectedCategoryId = value is null ? _selectedCategory?.Id : FindCategory(value.CategoryId)?.Id;
                    ScheduleSettingsSave();
                }
                AddActionCommand.NotifyCanExecuteChanged();
                RunProfileCommand.NotifyCanExecuteChanged();
                SetProfileColorCommand.NotifyCanExecuteChanged();
                SetProfileIconCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(RunAvailabilityMessage));
                OnPropertyChanged(nameof(HasRunValidationIssue));
                BuildPreflight(value);
                NavigateToValidationErrorCommand.NotifyCanExecuteChanged();
                _ = RefreshPendingRestoreAsync();
                _ = RefreshCurrentStatesAsync();
                NotifyActionCommandStates();
            }
        }
    }

    public ActionItemViewModel? SelectedAction
    {
        get => _selectedAction;
        set
        {
            if (!SetProperty(ref _selectedAction, value) || value is null) return;
            value.ClearExecutionError();
            SelectedProfile?.ClearExecutionError();
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
                TestActionCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(IsCancellationAvailable));
                OnPropertyChanged(nameof(RunAvailabilityMessage));
            }
        }
    }

    public bool HasExecutionStatus
    {
        get => _hasExecutionStatus;
        private set => SetProperty(ref _hasExecutionStatus, value);
    }

    public bool IsCancellationAvailable => IsProfileRunning || IsRestoreRunning;

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
            TestActionCommand.NotifyCanExecuteChanged();
            CancelProfileCommand.NotifyCanExecuteChanged();
            AddActionCommand.NotifyCanExecuteChanged();
            SaveCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(IsCancellationAvailable));
            OnPropertyChanged(nameof(RunAvailabilityMessage));
            OnPropertyChanged(nameof(HasRunValidationIssue));
        }
    }

    public int RestoreChangeCount { get => _restoreChangeCount; private set => SetProperty(ref _restoreChangeCount, value); }
    public bool HasPendingRestore => RestoreChangeCount > 0;
    public bool CanUndoSingleActionTest => _lastSingleActionTestSession is not null &&
        _lastSingleActionTestSession.Origin == ExecutionOrigin.SingleActionTest &&
        _lastSingleActionTestSession.PendingRestoreCount > 0 && !HasCriticalOperation;
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
            TestActionCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(RunAvailabilityMessage));
        }
    }
    public bool HasCriticalOperation => IsProfileRunning || IsRestoreRunning || IsSaving;

    public RelayCommand AddCategoryCommand { get; }

    public RelayCommand<CategoryItemViewModel> DeleteCategoryCommand { get; }

    public RelayCommand AddProfileCommand { get; }

    public RelayCommand<ProfileItemViewModel> DeleteProfileCommand { get; }
    public RelayCommand<ProfileItemViewModel> DuplicateProfileCommand { get; }
    public AsyncRelayCommand<ProfileItemViewModel> ExportProfileCommand { get; }
    public RelayCommand<string> SetProfileColorCommand { get; }
    public RelayCommand<string> SetProfileIconCommand { get; }
    public AsyncRelayCommand ImportProfileCommand { get; }

    public RelayCommand AddActionCommand { get; }
    public RelayCommand ToggleActionPickerCommand { get; }
    public RelayCommand<ActionTypeOption> SelectActionTypeCommand { get; }
    public RelayCommand<ActionItemViewModel> OpenThenActionPickerCommand { get; }
    public RelayCommand<ActionItemViewModel> OpenElseActionPickerCommand { get; }

    public RelayCommand<ActionItemViewModel> DeleteActionCommand { get; }
    public RelayCommand<ActionItemViewModel> DuplicateActionCommand { get; }
    public AsyncRelayCommand<ActionItemViewModel> TestActionCommand { get; }

    public RelayCommand<ActionItemViewModel> MoveActionUpCommand { get; }

    public RelayCommand<ActionItemViewModel> MoveActionDownCommand { get; }

    public AsyncRelayCommand<ReorderDropRequest> ReorderDropCommand { get; }

    public RelayCommand<CategoryItemViewModel> BeginCategoryRenameCommand { get; }

    public RelayCommand<CategoryItemViewModel> CommitCategoryRenameCommand { get; }

    public RelayCommand<CategoryItemViewModel> CancelCategoryRenameCommand { get; }

    public RelayCommand<ProfileItemViewModel> BeginProfileRenameCommand { get; }

    public RelayCommand<ProfileItemViewModel> CommitProfileRenameCommand { get; }

    public RelayCommand<ProfileItemViewModel> CancelProfileRenameCommand { get; }

    public AsyncRelayCommand AddThemeCommand { get; }
    public AsyncRelayCommand ImportThemeCommand { get; }
    public AsyncRelayCommand<string> ExportThemeCommand { get; }
    public AsyncRelayCommand<string> EditThemeCommand { get; }
    public AsyncRelayCommand<string> DuplicateThemeCommand { get; }
    public AsyncRelayCommand<string> RenameThemeCommand { get; }
    public AsyncRelayCommand<string> DeleteThemeCommand { get; }

    public RelayCommand<ActionItemViewModel> BrowseProgramCommand { get; }
    public RelayCommand<ActionItemViewModel> SelectArgumentsCommand { get; }
    public RelayCommand<ActionItemViewModel> BrowseWorkingDirectoryCommand { get; }

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
    public RelayCommand NavigateToValidationErrorCommand { get; }

    public AsyncRelayCommand RunProfileCommand { get; }
    public AsyncRelayCommand RestoreProfileCommand { get; }
    public AsyncRelayCommand DiscardPendingRestoreCommand { get; }
    public AsyncRelayCommand UndoSingleActionTestCommand { get; }
    public AsyncRelayCommand RefreshCurrentStatesCommand { get; }

    public RelayCommand CancelProfileCommand { get; }

    public AsyncRelayCommand SaveCommand { get; }
    public RelayCommand<string> UndoCommand { get; }
    public RelayCommand<string> RedoCommand { get; }
    public RelayCommand ToggleActivityCommand { get; }
    public RelayCommand ClearActivityCommand { get; }
    public RelayCommand<string> SelectActivityTabCommand { get; }
    public RelayCommand OpenThemeFolderCommand { get; }
    public AsyncRelayCommand ExportBackupCommand { get; }
    public AsyncRelayCommand ImportBackupCommand { get; }
    public RelayCommand OpenDataFolderCommand { get; }
    public RelayCommand OpenLogsFolderCommand { get; }
    public RelayCommand ClearLogsCommand { get; }
    public RelayCommand CopyDiagnosticsCommand { get; }
    public AsyncRelayCommand ExportDiagnosticsCommand { get; }
    public AsyncRelayCommand ExportHistoryCommand { get; }
    public AsyncRelayCommand ResetSettingsCommand { get; }
    public AsyncRelayCommand ResetAllDataCommand { get; }
    public AsyncRelayCommand CheckUpdatesCommand { get; }
    public RelayCommand OpenLatestReleaseCommand { get; }
    public RelayCommand OpenRepositoryCommand { get; }
    public RelayCommand ClearActivityFiltersCommand { get; }

    public ProfileDefinition? ResolveProfileDefinition(Guid id) =>
        _allProfiles.FirstOrDefault(item => item.Id == id)?.ToModel();

    public IReadOnlyList<TrayProfileShortcut> GetTrayProfiles() => _allProfiles
        .OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase)
        .Select(profile => new TrayProfileShortcut(profile.Id, profile.SettingsDisplayName))
        .ToList();

    public async Task RunProfileFromTrayAsync(Guid profileId)
    {
        var profile = _allProfiles.FirstOrDefault(item => item.Id == profileId);
        if (profile is null || HasCriticalOperation) return;
        SelectedProfile = profile;
        await RunProfileAsync();
    }

    public async Task RestoreProfileFromTrayAsync()
    {
        if (!HasPendingRestore || HasCriticalOperation) return;
        if (_pendingRestoreSession?.ProfileId is Guid profileId &&
            _allProfiles.FirstOrDefault(item => item.Id == profileId) is { } profile)
            SelectedProfile = profile;
        await RestoreProfileAsync();
    }

    public string GetLocalizedText(string key) => _localizationService.GetString(key);

    private CategoryItemViewModel? FindCategory(Guid categoryId) =>
        categoryId == Guid.Empty ? null : Categories.FirstOrDefault(item => item.Id == categoryId);

    private void SynchronizeSelectedCategory(ProfileItemViewModel? profile)
    {
        if (profile is null) return;
        var category = FindCategory(profile.CategoryId);
        if (ReferenceEquals(_selectedCategory, category)) return;

        _selectedCategory = category;
        OnPropertyChanged(nameof(SelectedCategory));
        RefreshProfiles();
        AddProfileCommand.NotifyCanExecuteChanged();
        ImportProfileCommand.NotifyCanExecuteChanged();
    }

    public bool NavigateToProfileAction(Guid? profileId, Guid? actionId)
    {
        if (profileId is not Guid id)
        {
            StatusMessage = _localizationService.GetString("Activity.NavigationProfileMissing");
            return false;
        }
        var profile = _allProfiles.FirstOrDefault(item => item.Id == id);
        if (profile is null)
        {
            StatusMessage = _localizationService.GetString("Activity.NavigationProfileMissing");
            return false;
        }
        SelectedProfile = profile;
        if (actionId is not Guid targetActionId) return true;
        var action = FindAction(profile.Actions, targetActionId);
        if (action is null)
        {
            StatusMessage = _localizationService.GetString("Activity.NavigationActionMissing");
            return false;
        }
        SelectedAction = action;
        action.IsExpanded = true;
        return true;
    }

    private static ActionItemViewModel? FindAction(IEnumerable<ActionItemViewModel> actions, Guid id)
    {
        foreach (var action in actions)
        {
            if (action.Id == id) return action;
            if (FindAction(action.ThenActions.Concat(action.ElseActions), id) is { } nested) return nested;
        }
        return null;
    }

    private ActionItemViewModel? FindFirstValidationError() =>
        SelectedProfile?.Actions.FirstOrDefault(action => action.IsEnabled && !action.IsValid);

    private void NavigateToValidationError()
    {
        var action = FindFirstValidationError();
        if (action is null) return;
        SelectedAction = action;
        action.IsExpanded = true;
    }

    private void ActivityServiceOnEntryAdded(object? sender, ActivityEntry entry)
    {
        if (_disposed) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
            _ = dispatcher.BeginInvoke(() =>
            {
                if (!_disposed) ActivityServiceOnEntryAdded(sender, entry);
            });
            return;
        }
        ActivityEntries.Add(entry);
        while (ActivityEntries.Count > 300) ActivityEntries.RemoveAt(0);
        RefreshActivityDisplayEntries();
        if (!IsActivityExpanded && entry.Level is ActivityLevel.Warning or ActivityLevel.Error)
            ActivityAlertCount++;
    }

    private void ClearActivity()
    {
        _activityService?.Clear();
        ActivityEntries.Clear();
        ActivityDisplayEntries.Clear();
        ActivityAlertCount = 0;
    }

    private void ActivityServiceOnPersistentViewsChanged(object? sender, EventArgs args)
    {
        if (_disposed) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
            _ = dispatcher.BeginInvoke(() =>
            {
                if (!_disposed) ActivityServiceOnPersistentViewsChanged(sender, args);
            });
            return;
        }
        RefreshPersistentActivityViews();
    }

    private void RefreshPersistentActivityViews()
    {
        HistoryEntries.Clear();
        foreach (var session in ProfileExecutionHistoryBuilder.Build(_activityService?.Records ?? []))
        {
            var item = new ProfileExecutionViewModel(session, _localizationService);
            if (MatchesActivityFilter(item.Timestamp, item.ProfileId, item.Result.ToString(),
                    new string?[] { item.ProfileName, item.StatusText }
                        .Concat(item.Actions.SelectMany(action =>
                            new string?[] { action.Name, action.Description }
                                .Concat(GetActionProcessSearchTerms(item.ProfileId, action.ActionId))))))
                HistoryEntries.Add(item);
        }
        SystemChangeEntries.Clear();
        foreach (var change in _activityService?.SystemChanges ?? [])
        {
            var item = new SystemChangeItemViewModel(change, _localizationService);
            if (MatchesActivityFilter(item.Timestamp, item.ProfileId, item.Status,
                    new string?[] { item.FriendlyName, item.Details, item.ProcessSearchText }
                        .Concat(GetActionProcessSearchTerms(item.ProfileId, item.ActionId))))
                SystemChangeEntries.Add(item);
        }
        OnPropertyChanged(nameof(UnresolvedSystemChangeCount));
        OnPropertyChanged(nameof(SystemChangeTabText));
        OnPropertyChanged(nameof(SystemChangeNoticeText));
    }

    private void RefreshActivityDisplayEntries()
    {
        ActivityDisplayEntries.Clear();
        foreach (var entry in ActivityEntries)
        {
            var item = CreateActivityDisplayEntry(entry);
            if (MatchesActivityFilter(item.Timestamp, item.ProfileId, item.Level.ToString(),
                    new string?[] { item.SourceText, item.Description }
                        .Concat(GetActionProcessSearchTerms(item.ProfileId, item.ActionId))))
                ActivityDisplayEntries.Add(item);
        }
    }

    private void RefreshFilteredActivityViews()
    {
        RefreshActivityDisplayEntries();
        RefreshPersistentActivityViews();
    }

    private bool MatchesActivityFilter(DateTimeOffset timestamp, Guid? profileId, string rawStatus,
        IEnumerable<string?> searchable)
    {
        if (ActivityProfileFilterId is Guid requested && profileId != requested) return false;
        if (!MatchesActivityTimeRange(timestamp)) return false;
        if (!MatchesActivityStatus(rawStatus)) return false;
        var query = ActivitySearchText.Trim();
        return query.Length == 0 || searchable.Any(value => MatchesActivitySearchValue(value, query));
    }

    private static bool MatchesActivitySearchValue(string? value, string query)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.Contains(query, StringComparison.CurrentCultureIgnoreCase)) return true;

        // Also tolerate a short process-name typo such as "andesk" for "Anydesk".
        var queryPosition = 0;
        foreach (var character in value)
        {
            if (char.ToUpperInvariant(character) != char.ToUpperInvariant(query[queryPosition])) continue;
            queryPosition++;
            if (queryPosition == query.Length) return true;
        }
        return false;
    }

    private IEnumerable<string?> GetActionProcessSearchTerms(Guid? profileId, Guid? actionId)
    {
        if (profileId is not Guid profileKey || actionId is not Guid actionKey) return [];
        var profile = _allProfiles.FirstOrDefault(item => item.Id == profileKey);
        var action = profile is null ? null : FindAction(profile.Actions, actionKey);
        if (action is null) return [];

        return [action.ProcessName, action.ExecutablePath, Path.GetFileName(action.ExecutablePath), action.Target];
    }

    private bool MatchesActivityStatus(string rawStatus)
    {
        if (ActivityStatusFilter == "all") return true;
        var status = rawStatus.ToLowerInvariant();
        return ActivityStatusFilter switch
        {
            "success" => status is "success" or "restored" or "completed",
            "error" => status.Contains("error") || status.Contains("failed"),
            "warning" => status.Contains("warning") || status is "pending" or "discarded" or "left-active" or "external-change",
            _ => true
        };
    }

    private bool MatchesActivityTimeRange(DateTimeOffset timestamp)
    {
        var now = DateTimeOffset.Now;
        return ActivityTimeRange switch
        {
            "today" => timestamp.LocalDateTime.Date == now.LocalDateTime.Date,
            "7d" => timestamp >= now.AddDays(-7),
            "30d" => timestamp >= now.AddDays(-30),
            _ => true
        };
    }

    private static string NormalizeActivityStatus(string? value) => value?.ToLowerInvariant() switch
    {
        "success" => "success", "warning" => "warning", "error" => "error", _ => "all"
    };

    private static string NormalizeActivityTimeRange(string? value) => value?.ToLowerInvariant() switch
    {
        "today" => "today", "7d" => "7d", "30d" => "30d", _ => "all"
    };

    private static int NormalizeActivityTabIndex(int value) => value switch
    {
        1 => 1,
        2 => 2,
        // Index 0 belonged to the removed live-activity subtab. Keep old
        // settings readable and open the useful History view instead.
        _ => 2
    };

    private void ClearActivityFilters()
    {
        _activitySearchText = string.Empty;
        _activityStatusFilter = "all";
        _activityTimeRange = "all";
        _activityProfileFilterId = null;
        OnPropertyChanged(nameof(ActivitySearchText));
        OnPropertyChanged(nameof(ActivityStatusFilter));
        OnPropertyChanged(nameof(ActivityTimeRange));
        OnPropertyChanged(nameof(ActivityProfileFilterId));
        RefreshFilteredActivityViews();
    }

    private ActivityEntryViewModel CreateActivityDisplayEntry(ActivityEntry entry)
    {
        var profile = entry.ProfileId is Guid profileId ? _allProfiles.FirstOrDefault(item => item.Id == profileId) : null;
        var action = profile is not null && entry.ActionId is Guid actionId
            ? FindAction(profile.Actions, actionId)
            : null;
        return new ActivityEntryViewModel(entry, profile?.Name, action?.DisplayName ?? action?.Name,
            _localizationService);
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
        RefreshProfileGroups();
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

        RootNavigationItems.Remove(category);
        var rootSortOrder = RootProfiles.Count;
        foreach (var profile in _allProfiles.Where(profile => profile.CategoryId == category.Id)
                     .OrderBy(profile => profile.SortOrder))
        {
            profile.MoveToCategory(Guid.Empty);
            profile.SortOrder = rootSortOrder++;
        }

        Categories.Remove(category);
        RefreshProfileGroups();
        if (ReferenceEquals(SelectedCategory, category)) SelectedCategory = null;
        if (SelectedProfile is not null && SelectedProfile.CategoryId == Guid.Empty)
            SynchronizeSelectedCategory(SelectedProfile);
        MarkDirty(_localizationService.GetString("Status.CategoryDeleted"));
    }

    private void AddProfile()
    {
        RecordStructuralUndo("add-profile");

        var categoryId = SelectedCategory?.Id ?? Guid.Empty;
        var profilesInCategory = GetProfilesInGroup(categoryId);
        var profile = new ProfileItemViewModel(new ProfileDefinition
        {
            Id = Guid.NewGuid(),
            CategoryId = categoryId,
            Name = CreateUniqueName(
                _localizationService.GetString("Default.NewProfile"),
                profilesInCategory.Select(item => item.Name)),
            SortOrder = profilesInCategory.Count
        }, _localizationService);
        Subscribe(profile);
        _allProfiles.Add(profile);
        OnPropertyChanged(nameof(AllProfiles));
        RefreshProfileGroups();
        SelectedProfile = profile;
        profile.BeginEdit();
        MarkDirty(_localizationService.GetString("Status.ProfileCreated"));
    }

    private async Task ExportProfileAsync(ProfileItemViewModel? profile)
    {
        if (profile is null) return;
        var dialog = new SaveFileDialog
        {
            Filter = "SwitchBoard profile (*.sbprofile)|*.sbprofile|JSON files (*.json)|*.json",
            DefaultExt = ".sbprofile",
            FileName = string.IsNullOrWhiteSpace(profile.Name) ? "Profile" : profile.Name
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await _profileExchangeService.ExportAsync(profile.ToModel(), dialog.FileName);
            StatusMessage = _localizationService.GetString("Status.ProfileExported");
        }
        catch (Exception exception)
        {
            StatusMessage = _localizationService.Format("Status.ProfileExportFailed", exception.Message);
        }
    }

    private async Task ImportProfileAsync()
    {
        if (HasCriticalOperation) return;
        var dialog = new OpenFileDialog
        {
            Filter = "SwitchBoard profile (*.sbprofile)|*.sbprofile|JSON files (*.json)|*.json",
            DefaultExt = ".sbprofile",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            var imported = await _profileExchangeService.ImportAsync(dialog.FileName);
            RecordStructuralUndo("import-profile");
            imported.CategoryId = SelectedCategory?.Id ?? Guid.Empty;
            var siblings = GetProfilesInGroup(imported.CategoryId);
            imported.Name = CreateUniqueName(imported.Name, siblings.Select(profile => profile.Name));
            imported.SortOrder = siblings.Count;
            var profile = new ProfileItemViewModel(imported, _localizationService);
            Subscribe(profile);
            _allProfiles.Add(profile);
            OnPropertyChanged(nameof(AllProfiles));
            RefreshProfileGroups();
            SelectedProfile = profile;
            MarkDirty(_localizationService.GetString("Status.ProfileImported"));
        }
        catch (Exception exception)
        {
            StatusMessage = _localizationService.Format("Status.ProfileImportFailed", exception.Message);
        }
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

        var siblings = GetProfilesInGroup(profile.CategoryId);
        var oldIndex = siblings.IndexOf(profile);
        RootNavigationItems.Remove(profile);
        _allProfiles.Remove(profile);
        OnPropertyChanged(nameof(AllProfiles));
        RefreshProfileGroups();
        var remaining = GetProfilesInGroup(profile.CategoryId);
        SelectedProfile = remaining.Count == 0
            ? null
            : remaining[Math.Min(oldIndex, remaining.Count - 1)];
        MarkDirty(_localizationService.GetString("Status.ProfileDeleted"));
    }

    private void DuplicateProfile(ProfileItemViewModel? source)
    {
        if (source is null) return;
        var siblings = _allProfiles.Where(item => item.CategoryId == source.CategoryId)
            .OrderBy(item => item.SortOrder).ToList();
        var clone = _profileExchangeService.CloneForDuplicate(source.ToModel());
        clone.CategoryId = source.CategoryId;
        clone.Name = CreateUniqueName(source.Name, siblings.Select(item => item.Name));
        clone.SortOrder = source.SortOrder + 1;
        foreach (var sibling in siblings.Where(item => item.SortOrder > source.SortOrder)) sibling.SortOrder++;

        RecordStructuralUndo("duplicate-profile");
        var viewModel = new ProfileItemViewModel(clone, _localizationService);
        Subscribe(viewModel);
        _allProfiles.Add(viewModel);
        if (viewModel.CategoryId == Guid.Empty)
        {
            var rootIndex = RootNavigationItems.IndexOf(source);
            RootNavigationItems.Insert(rootIndex < 0 ? RootNavigationItems.Count : rootIndex + 1, viewModel);
        }
        OnPropertyChanged(nameof(AllProfiles));
        RefreshProfileGroups();
        SelectedProfile = viewModel;
        MarkDirty(_localizationService.GetString("Status.ProfileDuplicated"));
    }

    private void SetSelectedProfileColor(string? color) => SetSelectedProfileAppearance(color, SelectedProfile?.Icon);

    private void SetSelectedProfileIcon(string? icon) => SetSelectedProfileAppearance(SelectedProfile?.Color, icon);

    private void SetSelectedProfileAppearance(string? color, string? icon)
    {
        if (SelectedProfile is null || HasCriticalOperation) return;
        SelectedProfile.Color = color;
        SelectedProfile.Icon = icon;
        StatusMessage = _localizationService.GetString("Status.ProfileAppearanceChanged");
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
            Parameters = _actionPickerCatalog.CreateDefaultParameters(SelectedActionType.TypeId, nested: false)
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

    private void FilterActionTypes()
    {
        var matches = _actionPickerCatalog.Filter(AvailableActionTypes, ActionPickerSearch);
        FilteredActionTypes.Clear();
        foreach (var option in matches) FilteredActionTypes.Add(option);
    }

    private void ToggleMainActionPicker()
    {
        _nestedActionTarget = null;
        IsActionPickerOpen = !IsActionPickerOpen;
    }

    private void OpenNestedActionPicker(ActionItemViewModel? action, bool thenBranch)
    {
        if (action is null || !action.CanAddNestedActions) return;
        _nestedActionTarget = action;
        _nestedActionThenBranch = thenBranch;
        IsActionPickerOpen = true;
    }

    private void SelectActionType(ActionTypeOption? option)
    {
        if (option is null) return;
        SelectedActionType = option;
        IsActionPickerOpen = false;
        if (_nestedActionTarget is { } nestedParent)
        {
            RecordStructuralUndo("add-nested-action");
            var nested = nestedParent.AddNestedAction(option.TypeId,
                _actionPickerCatalog.CreateDefaultParameters(option.TypeId, nested: true), _nestedActionThenBranch);
            _nestedActionTarget = null;
            if (nested is not null)
            {
                Subscribe(nested);
                nested.IsExpanded = true;
                MarkDirty(_localizationService.GetString("Status.ActionAdded"));
            }
            return;
        }
        AddAction();
    }

    private void DuplicateAction(ActionItemViewModel? action)
    {
        if (SelectedProfile is null || action is null || HasCriticalOperation) return;
        RecordStructuralUndo("duplicate-action");
        var model = action.ToModel();
        ResetRuntimeAndAssignIds(model);
        var copy = new ActionItemViewModel(model, _localizationService);
        Subscribe(copy);
        var index = SelectedProfile.Actions.IndexOf(action);
        foreach (var existing in SelectedProfile.Actions) existing.IsExpanded = false;
        SelectedProfile.Actions.Insert(index + 1, copy);
        SelectedAction = copy;
        MarkDirty(_localizationService.GetString("Status.ActionDuplicated"));
    }

    private static void ResetRuntimeAndAssignIds(ActionDefinition action)
    {
        action.Id = Guid.NewGuid();
        foreach (var property in new[] { ActionParameterNames.ThenActions, ActionParameterNames.ElseActions })
        {
            if (action.Parameters[property] is not JsonArray nested) continue;
            foreach (var node in nested)
            {
                try
                {
                    if (node is null) continue;
                    var child = node.Deserialize<ActionDefinition>();
                    if (child is null) continue;
                    ResetRuntimeAndAssignIds(child);
                    node.ReplaceWith(JsonSerializer.SerializeToNode(child));
                }
                catch (JsonException) { }
            }
        }
    }

    private bool CanTestAction(ActionItemViewModel? action) => action is not null && !action.IsComment &&
        SelectedProfile is not null && action.IsValid && !HasCriticalOperation &&
        !_profileRunner.IsRunning && !_profileRestoreRunner.IsRunning;

    private async Task TestActionAsync(ActionItemViewModel? action)
    {
        if (!CanTestAction(action) || SelectedProfile is null) return;
        var model = action!.ToModel();
        var testProfile = new ProfileDefinition { Id = SelectedProfile.Id, Name = SelectedProfile.Name, Actions = [model] };
        _profileExecutionCancellation = new CancellationTokenSource();
        IsProfileRunning = true;
        SetProfileExecutionState(SelectedProfile, ProfileExecutionState.Executing);
        ResetActionExecutionStates(SelectedProfile);
        HasExecutionStatus = true;
        CurrentExecutionActionNumber = 0;
        TotalExecutionActions = 1;
        CurrentExecutionActionName = action.DisplayName;
        ExecutionErrorMessage = string.Empty;
        SetExecutionStatus("Execution.Status.Running");
        StatusMessage = _localizationService.GetString("Execution.TestingAction");
        var progress = new Progress<ProfileExecutionProgress>(ApplyExecutionProgress);
        try
        {
            LastExecutionSession = await _profileRunner.RunAsync(testProfile, progress, _profileExecutionCancellation.Token,
                ExecutionOrigin.SingleActionTest);
            _activityService?.Add(ActivityLevel.Info,
                _localizationService.Format("Activity.ActionTest", action.DisplayName),
                SelectedProfile.Id, action.Id);
            await RefreshPendingRestoreAsync(SelectedProfile.Id);
            UndoSingleActionTestCommand.NotifyCanExecuteChanged();
            await RefreshCurrentStatesAsync();
            ExecutionErrorMessage = LastExecutionSession.Journal.LastOrDefault(entry =>
                entry.Status is ActionJournalStatus.Failed or ActionJournalStatus.Unsupported)?.ErrorMessage ?? string.Empty;
            switch (LastExecutionSession.Status)
            {
                case ExecutionSessionStatus.Cancelled:
                    SetExecutionStatus("Execution.Status.Cancelled");
                    StatusMessage = _localizationService.GetString("Status.ProfileCancelled");
                    break;
                case ExecutionSessionStatus.Failed:
                case ExecutionSessionStatus.CompletedWithErrors:
                    SetProfileExecutionState(SelectedProfile, ProfileExecutionState.Error);
                    SetExecutionStatus("Execution.Status.Failed");
                    StatusMessage = _localizationService.GetString("Status.ActionTestFailed");
                    break;
                default:
                    SetExecutionStatus("Execution.Status.Success");
                    StatusMessage = _localizationService.GetString("Status.ActionTestCompleted");
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            SetExecutionStatus("Execution.Status.Cancelled");
            StatusMessage = _localizationService.GetString("Execution.Cancelled");
        }
        catch (Exception exception)
        {
            SetProfileExecutionState(SelectedProfile, ProfileExecutionState.Error);
            SetExecutionStatus("Execution.Status.Failed");
            ExecutionErrorMessage = exception.Message;
            StatusMessage = exception.Message;
        }
        finally
        {
            _profileExecutionCancellation.Dispose();
            _profileExecutionCancellation = null;
            IsProfileRunning = false;
            ClearActiveActionExecutionStates();
            if (SelectedProfile?.ExecutionState == ProfileExecutionState.Executing)
                SetProfileExecutionState(SelectedProfile, ProfileExecutionState.Normal);
            TestActionCommand.NotifyCanExecuteChanged();
        }
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
        if (request is null || HasCriticalOperation || !string.IsNullOrWhiteSpace(ProfileSearchText)) return;
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
        var oldRootIndex = RootNavigationItems.IndexOf(category);
        if (oldRootIndex < 0) return false;

        // TargetIndex belongs to the shared root list. Remove the dragged category
        // before inserting it so root profiles remain in exactly the requested order.
        var insertionIndex = Math.Clamp(request.TargetIndex, 0, RootNavigationItems.Count);
        if (insertionIndex > oldRootIndex) insertionIndex--;
        insertionIndex = Math.Clamp(insertionIndex, 0, RootNavigationItems.Count - 1);
        if (insertionIndex == oldRootIndex) return false;

        RecordStructuralUndo("drag-category");
        RootNavigationItems.RemoveAt(oldRootIndex);
        RootNavigationItems.Insert(insertionIndex, category);

        // Categories keeps the same relative category order for compatibility with
        // existing bindings and persistence, while the mixed root order remains the
        // single source of truth for the Profile navigation.
        var orderedCategories = RootNavigationItems.OfType<CategoryItemViewModel>().ToList();
        for (var index = 0; index < orderedCategories.Count; index++)
        {
            var currentIndex = Categories.IndexOf(orderedCategories[index]);
            if (currentIndex >= 0 && currentIndex != index)
                Categories.Move(currentIndex, index);
        }

        MarkDirty(_localizationService.GetString("Status.CategoryOrderChanged"));
        return true;
    }

    private bool ReorderProfile(ReorderDropRequest request)
    {
        if (request.Item is not ProfileItemViewModel profile || !_allProfiles.Contains(profile)) return false;
        if (request.TargetParentId == Guid.Empty)
            return ReorderProfileAtRoot(request, profile);

        var sourceCategoryId = profile.CategoryId;
        var targetCategoryId = request.TargetItem switch
        {
            CategoryItemViewModel category => category.Id,
            ProfileItemViewModel targetProfile => targetProfile.CategoryId,
            _ => request.TargetParentId ?? sourceCategoryId
        };
        if (targetCategoryId != Guid.Empty && Categories.All(category => category.Id != targetCategoryId)) return false;

        var sourceProfiles = GetProfilesInGroup(sourceCategoryId);
        var oldIndex = sourceProfiles.IndexOf(profile);
        if (oldIndex < 0) return false;

        var targetProfiles = sourceCategoryId == targetCategoryId
            ? sourceProfiles
            : GetProfilesInGroup(targetCategoryId);
        var insertionIndex = request.TargetItem is CategoryItemViewModel
            ? targetProfiles.Count
            : Math.Clamp(request.TargetIndex, 0, targetProfiles.Count);
        if (ReferenceEquals(sourceProfiles, targetProfiles) && insertionIndex > oldIndex) insertionIndex--;
        insertionIndex = Math.Clamp(insertionIndex, 0, Math.Max(0, targetProfiles.Count -
            (ReferenceEquals(sourceProfiles, targetProfiles) ? 1 : 0)));
        if (sourceCategoryId == targetCategoryId && insertionIndex == oldIndex) return false;

        RecordStructuralUndo(sourceCategoryId == targetCategoryId ? "drag-profile" : "drag-profile-category");
        RootNavigationItems.Remove(profile);
        sourceProfiles.Remove(profile);
        if (!ReferenceEquals(sourceProfiles, targetProfiles)) targetProfiles.Remove(profile);
        insertionIndex = Math.Clamp(insertionIndex, 0, targetProfiles.Count);
        targetProfiles.Insert(insertionIndex, profile);
        profile.MoveToCategory(targetCategoryId);
        for (var index = 0; index < sourceProfiles.Count; index++) sourceProfiles[index].SortOrder = index;
        for (var index = 0; index < targetProfiles.Count; index++) targetProfiles[index].SortOrder = index;

        RefreshProfileGroups();
        SynchronizeSelectedCategory(profile);
        SelectedProfile = profile;
        MarkDirty(_localizationService.GetString(sourceCategoryId == targetCategoryId
            ? "Status.ProfileOrderChanged"
            : "Status.ProfileCategoryChanged"));
        return true;
    }

    private bool ReorderProfileAtRoot(ReorderDropRequest request, ProfileItemViewModel profile)
    {
        var sourceCategoryId = profile.CategoryId;
        var oldRootIndex = RootNavigationItems.IndexOf(profile);
        var insertionIndex = Math.Clamp(request.TargetIndex, 0, RootNavigationItems.Count);
        if (oldRootIndex >= 0 && insertionIndex > oldRootIndex) insertionIndex--;
        insertionIndex = Math.Clamp(insertionIndex, 0,
            Math.Max(0, RootNavigationItems.Count - (oldRootIndex >= 0 ? 1 : 0)));
        if (sourceCategoryId == Guid.Empty && insertionIndex == oldRootIndex) return false;

        RecordStructuralUndo(sourceCategoryId == Guid.Empty ? "drag-profile" : "drag-profile-category");
        if (oldRootIndex >= 0) RootNavigationItems.RemoveAt(oldRootIndex);
        profile.MoveToCategory(Guid.Empty);
        RootNavigationItems.Insert(insertionIndex, profile);
        RefreshProfileGroups();
        SynchronizeSelectedCategory(profile);
        SelectedProfile = profile;
        MarkDirty(_localizationService.GetString(sourceCategoryId == Guid.Empty
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
            var snapshot = BuildCatalogSnapshot();
            if (AutomaticBackupEnabled)
            {
                try
                {
                    await Task.Run(() => _backupService.CreateAutomaticBackupAsync(snapshot,
                        SwitchBoardBackupService.CloneSettings(_userSettings), _appDataPaths,
                        AutomaticBackupCount));
                }
                catch (Exception exception)
                {
                    _activityService?.Add(ActivityLevel.Warning, $"Automatic backup failed: {exception.Message}");
                }
            }
            await _catalogService.SaveAsync(snapshot);
            _savedCatalogBaseline = BuildCatalogSnapshot();
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
        ScheduleSettingsSave();
    }

    private void ScheduleSettingsSave()
    {
        lock (_settingsSaveSync)
        {
            _settingsSaveDebounce?.Cancel();
            var cancellation = new CancellationTokenSource();
            _settingsSaveDebounce = cancellation;
            _settingsSaveTask = SaveSettingsAfterDelayAsync(cancellation);
        }
    }

    private async Task SaveSettingsAfterDelayAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(400, cancellation.Token);
            await _settingsRepository.SaveAsync(_userSettings, cancellation.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            StatusMessage = _localizationService.Format("Status.SaveFailed", exception.Message);
        }
        finally
        {
            lock (_settingsSaveSync)
            {
                if (ReferenceEquals(_settingsSaveDebounce, cancellation))
                {
                    _settingsSaveDebounce = null;
                    _settingsSaveTask = null;
                }
            }
            cancellation.Dispose();
        }
    }

    public async Task FlushPendingSettingsSaveAsync()
    {
        Task? pending;
        lock (_settingsSaveSync)
        {
            _settingsSaveDebounce?.Cancel();
            pending = _settingsSaveTask;
        }
        if (pending is not null)
        {
            try { await pending; } catch (OperationCanceledException) { }
        }
        await _settingsRepository.SaveAsync(_userSettings);
    }

    private void ApplyInterfaceDensityResources()
    {
        if (Application.Current is not { } application) return;
        var compact = string.Equals(_userSettings.InterfaceDensity, "compact", StringComparison.OrdinalIgnoreCase);
        application.Resources["SettingsRowPadding"] = compact ? new Thickness(9, 6, 9, 6) : new Thickness(10, 7, 10, 7);
        application.Resources["ActionCardPadding"] = compact ? new Thickness(12) : new Thickness(14);
        application.Resources["ActionCardItemMargin"] = compact ? new Thickness(0, 0, 0, 6) : new Thickness(0, 0, 0, 8);
        application.Resources["NestedActionMargin"] = compact ? new Thickness(0, 2, 0, 2) : new Thickness(0, 3, 0, 3);
        application.Resources["NestedActionPadding"] = compact ? new Thickness(9, 9, 9, 9) : new Thickness(10, 10, 10, 10);
    }

    private void OpenDirectory(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception exception)
        {
            StatusMessage = _localizationService.Format("Status.OpenFolderFailed", exception.Message);
        }
    }

    private async Task ExportBackupAsync()
    {
        var dialog = new SaveFileDialog
        {
            Filter = _localizationService.GetString("Settings.BackupFilter"),
            DefaultExt = ".sbbackup",
            AddExtension = true,
            FileName = "SwitchBoard-backup.sbbackup"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await _backupService.ExportAsync(BuildCatalogSnapshot(), _userSettings, dialog.FileName, _appDataPaths);
            StatusMessage = _localizationService.GetString("Status.BackupExported");
        }
        catch (Exception exception)
        {
            StatusMessage = _localizationService.Format("Status.BackupFailed", exception.Message);
        }
    }

    private async Task ImportBackupAsync()
    {
        if (HasCriticalOperation) return;
        var dialog = new OpenFileDialog
        {
            Filter = _localizationService.GetString("Settings.BackupFilter"),
            DefaultExt = ".sbbackup",
            CheckFileExists = true
        };
        if (dialog.ShowDialog() != true) return;

        SwitchBoardBackupPackage imported;
        try
        {
            imported = await _backupService.ImportPackageAsync(dialog.FileName);
        }
        catch (Exception exception)
        {
            StatusMessage = _localizationService.Format("Status.BackupFailed", exception.Message);
            return;
        }

        if (!_dialogService.Confirm(_localizationService.GetString("Settings.ImportBackupConfirmTitle"),
                _localizationService.GetString("Settings.ImportBackupConfirmMessage"))) return;

        var previousCatalog = BuildCatalogSnapshot();
        var previousSettings = SwitchBoardBackupService.CloneSettings(_userSettings);
        ThemeAssetStaging? stagedAssets = null;
        try
        {
            await FlushPendingSettingsSaveAsync();
            await _backupService.CreateSafetyBackupAsync(previousCatalog, previousSettings, _appDataPaths, "restore");
            stagedAssets = await _backupService.StageThemeAssetsAsync(imported, _appDataPaths);
            await _catalogService.SaveAsync(imported.Document.Catalog);
            await _settingsRepository.SaveAsync(imported.Document.Settings);
            stagedAssets.Commit();
            ApplyRuntimeSettings(imported.Document.Settings);
            ApplyCatalogSnapshot(imported.Document.Catalog, "Status.BackupImported");
            stagedAssets.Complete();
        }
        catch (Exception exception)
        {
            try
            {
                await _catalogService.SaveAsync(previousCatalog);
                await _settingsRepository.SaveAsync(previousSettings);
                stagedAssets?.Rollback();
                ApplyRuntimeSettings(previousSettings);
                ApplyCatalogSnapshot(previousCatalog, "Status.BackupRollback");
            }
            catch (Exception rollbackException)
            {
                StatusMessage = _localizationService.Format("Status.BackupRollbackFailed", rollbackException.Message);
                return;
            }

            StatusMessage = _localizationService.Format("Status.BackupFailed", exception.Message);
            return;
        }
        finally { stagedAssets?.Dispose(); }

        StatusMessage = _localizationService.GetString("Status.BackupImported");
    }

    private void ApplyRuntimeSettings(UserSettings source)
    {
        if (_startupRegistrationService is not null &&
            !_startupRegistrationService.TrySetEnabled(source.LaunchAtStartup, out var startupError))
        {
            throw new InvalidOperationException(startupError ??
                "Windows did not allow updating the SwitchBoard startup registration.");
        }

        _userSettings.SchemaVersion = SettingsSchema.CurrentVersion;
        _userSettings.ThemeId = source.ThemeId;
        _userSettings.LanguageId = source.LanguageId;
        _userSettings.ActivityPanelHeightRatio = source.ActivityPanelHeightRatio;
        _userSettings.ShowCurrentActionState = source.ShowCurrentActionState;
        _userSettings.LaunchAtStartup = source.LaunchAtStartup;
        _userSettings.CloseBehavior = string.Equals(source.CloseBehavior, "tray", StringComparison.OrdinalIgnoreCase)
            ? "tray" : "close";
        _userSettings.AutomaticBackupEnabled = source.AutomaticBackupEnabled;
        _userSettings.AutomaticBackupCount = Math.Clamp(source.AutomaticBackupCount, 1, 50);
        _userSettings.RememberLastView = source.RememberLastView;
        _userSettings.LastMainView = source.LastMainView;
        _userSettings.WarnBeforeClosingWithUnsavedChanges = source.WarnBeforeClosingWithUnsavedChanges;
        _userSettings.InterfaceDensity = source.InterfaceDensity;
        _userSettings.ShowCardDetails = source.ShowCardDetails;
        _userSettings.WindowWidth = source.WindowWidth;
        _userSettings.WindowHeight = source.WindowHeight;
        _userSettings.WindowX = source.WindowX;
        _userSettings.WindowY = source.WindowY;
        _userSettings.WindowState = source.WindowState;
        _userSettings.LastSelectedCategoryId = source.LastSelectedCategoryId;
        _userSettings.LastSelectedProfileId = source.LastSelectedProfileId;
        _userSettings.LastActivityTabIndex = source.LastActivityTabIndex;
        _userSettings.IsActivityExpanded = source.IsActivityExpanded;
        _userSettings.CustomThemes = source.CustomThemes.Select(theme => theme.Clone()).ToList();

        var languageId = _localizationService.ApplyLanguage(_userSettings.LanguageId);
        _userSettings.LanguageId = languageId;
        foreach (var actionType in AvailableActionTypes) actionType.RefreshLocalization();
        ActionPickerView.Refresh();
        foreach (var action in _allProfiles.SelectMany(profile => profile.Actions)) action.RefreshDisplayName();
        foreach (var languageOption in LanguageOptions) languageOption.RefreshDisplayName();
        CloseBehaviorOptions[0].RefreshDisplayName(_localizationService.GetString("Settings.CloseSwitchBoard"));
        CloseBehaviorOptions[1].RefreshDisplayName(_localizationService.GetString("Settings.MinimizeToTray"));
        InterfaceDensityOptions[0].RefreshDisplayName(_localizationService.GetString("Settings.Density.Standard"));
        InterfaceDensityOptions[1].RefreshDisplayName(_localizationService.GetString("Settings.Density.Compact"));
        _selectedLanguageOption = LanguageOptions.FirstOrDefault(option =>
            string.Equals(option.Id, languageId, StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(SelectedLanguageOption));

        ThemeOptions.Clear();
        foreach (var theme in _themeManager.AvailableThemes)
            ThemeOptions.Add(new ThemeOptionViewModel(theme, _localizationService));
        foreach (var theme in _userSettings.CustomThemes)
            ThemeOptions.Add(new ThemeOptionViewModel(theme, _localizationService));
        var selectedTheme = ThemeOptions.FirstOrDefault(option =>
            string.Equals(option.Id, _userSettings.ThemeId, StringComparison.OrdinalIgnoreCase)) ??
            ThemeOptions.First(option => string.Equals(option.Id, ThemeIds.Graphite, StringComparison.OrdinalIgnoreCase));
        _selectedThemeOption = selectedTheme;
        _userSettings.ThemeId = _themeManager.ApplyTheme(selectedTheme.Id, FindCustomTheme(selectedTheme.Id)?.Colors);
        OnPropertyChanged(nameof(SelectedThemeOption));
        OnPropertyChanged(nameof(ActiveThemeDisplayName));
        UpdateActiveThemeMarker();

        _activityPanelHeightRatio = Math.Clamp(_userSettings.ActivityPanelHeightRatio, 0.2, 0.8);
        _isActivityExpanded = _userSettings.IsActivityExpanded;
        _activityTabIndex = NormalizeActivityTabIndex(_userSettings.LastActivityTabIndex);
        OnPropertyChanged(nameof(ActivityPanelHeightRatio));
        OnPropertyChanged(nameof(IsActivityExpanded));
        OnPropertyChanged(nameof(ActivityTabIndex));
        OnPropertyChanged(nameof(WindowWidth));
        OnPropertyChanged(nameof(WindowHeight));
        OnPropertyChanged(nameof(RememberLastView));
        OnPropertyChanged(nameof(InitialMainView));
        OnPropertyChanged(nameof(WarnBeforeClosingWithUnsavedChanges));
        OnPropertyChanged(nameof(IsLaunchAtStartup));
        OnPropertyChanged(nameof(CloseBehavior));
        OnPropertyChanged(nameof(InterfaceDensity));
        OnPropertyChanged(nameof(ShowCardDetails));
        OnPropertyChanged(nameof(AutomaticBackupEnabled));
        OnPropertyChanged(nameof(AutomaticBackupCount));
        ApplyInterfaceDensityResources();
        RefreshExecutionDisplay();
        RefreshPersistentActivityViews();
    }

    private void ClearLogs()
    {
        if (!_dialogService.Confirm(_localizationService.GetString("Settings.ClearLogsConfirmTitle"),
                _localizationService.GetString("Settings.ClearLogsConfirmMessage"))) return;
        try
        {
            _logMaintenanceService.Clear();
            StatusMessage = _localizationService.GetString("Status.LogsCleared");
        }
        catch (Exception exception)
        {
            StatusMessage = _localizationService.Format("Status.ClearLogsFailed", exception.Message);
        }
    }

    private void CopyDiagnostics()
    {
        try
        {
            Clipboard.SetText(BuildDiagnosticsText());
            StatusMessage = _localizationService.GetString("Status.DiagnosticsCopied");
        }
        catch (Exception exception)
        {
            StatusMessage = _localizationService.Format("Status.DiagnosticsCopyFailed", exception.Message);
        }
    }

    private string BuildDiagnosticsText() => string.Join(Environment.NewLine,
        $"SwitchBoard: {ApplicationVersion}",
        $"Windows: {Environment.OSVersion}",
        $"Process architecture: {RuntimeInformation.ProcessArchitecture}",
        $"Data folder: {DataDirectoryPath}",
        $"Profiles: {_allProfiles.Count}",
        $"Theme: {ActiveThemeDisplayName}");

    private async Task ExportDiagnosticsAsync()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "ZIP archive (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            FileName = "SwitchBoard-diagnostics.zip"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await _diagnosticExportService.ExportDiagnosticsAsync(dialog.FileName, BuildDiagnosticsText(),
                _activityService?.Records ?? []);
            StatusMessage = _localizationService.GetString("Status.DiagnosticsExported");
        }
        catch (Exception exception)
        {
            StatusMessage = _localizationService.Format("Status.DiagnosticsExportFailed", exception.Message);
        }
    }

    private async Task ExportHistoryAsync()
    {
        var dialog = new SaveFileDialog
        {
            Filter = "ZIP archive (*.zip)|*.zip",
            DefaultExt = ".zip",
            AddExtension = true,
            FileName = "SwitchBoard-history.zip"
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            await _diagnosticExportService.ExportHistoryAsync(dialog.FileName, _activityService?.Records ?? []);
            StatusMessage = _localizationService.GetString("Status.HistoryExported");
        }
        catch (Exception exception)
        {
            StatusMessage = _localizationService.Format("Status.DiagnosticsExportFailed", exception.Message);
        }
    }

    public async Task ResetSettingsAsync()
    {
        if (!_dialogService.Confirm(_localizationService.GetString("Dialog.ResetSettingsTitle"),
                _localizationService.GetString("Dialog.ResetSettingsMessage"))) return;
        var previousSettings = SwitchBoardBackupService.CloneSettings(_userSettings);
        ThemeAssetStaging? stagedAssets = null;
        try
        {
            await FlushPendingSettingsSaveAsync();
            await _backupService.CreateSafetyBackupAsync(BuildCatalogSnapshot(), previousSettings,
                _appDataPaths, "reset-settings");
            var defaults = CreateDefaultSettings();
            stagedAssets = await _backupService.StageThemeAssetsAsync(
                new SwitchBoardBackupPackage(new SwitchBoardBackupDocument { Settings = defaults },
                    new Dictionary<string, byte[]>()), _appDataPaths);
            await _settingsRepository.SaveAsync(defaults);
            stagedAssets.Commit();
            ApplyRuntimeSettings(defaults);
            stagedAssets.Complete();
            StatusMessage = _localizationService.GetString("Status.SettingsReset");
        }
        catch (Exception exception)
        {
            try
            {
                await _settingsRepository.SaveAsync(previousSettings);
                stagedAssets?.Rollback();
                ApplyRuntimeSettings(previousSettings);
            }
            catch { }
            StatusMessage = _localizationService.Format("Status.ResetFailed", exception.Message);
        }
        finally { stagedAssets?.Dispose(); }
    }

    public async Task ResetAllDataAsync()
    {
        if (!_dialogService.Confirm(_localizationService.GetString("Dialog.ResetAllTitle"),
                _localizationService.GetString("Dialog.ResetAllMessage"))) return;

        var previousCatalog = BuildCatalogSnapshot();
        var previousSettings = SwitchBoardBackupService.CloneSettings(_userSettings);
        string safetyBackup;
        try
        {
            await FlushPendingSettingsSaveAsync();
            safetyBackup = await _backupService.CreateSafetyBackupAsync(previousCatalog, previousSettings,
                _appDataPaths, "reset-all");
        }
        catch (Exception exception)
        {
            StatusMessage = _localizationService.Format("Status.ResetBackupFailed", exception.Message);
            return;
        }

        ThemeAssetStaging? stagedAssets = null;
        try
        {
            var defaults = CreateDefaultSettings();
            var emptyCatalog = SwitchBoardCatalog.Empty();
            stagedAssets = await _backupService.StageThemeAssetsAsync(
                new SwitchBoardBackupPackage(new SwitchBoardBackupDocument { Catalog = emptyCatalog, Settings = defaults },
                    new Dictionary<string, byte[]>()),
                _appDataPaths);
            await _catalogService.SaveAsync(emptyCatalog);
            await _settingsRepository.SaveAsync(defaults);
            stagedAssets.Commit();
            ApplyRuntimeSettings(defaults);
            ApplyCatalogSnapshot(emptyCatalog, "Status.AllDataReset");
            stagedAssets.Complete();
            StatusMessage = _localizationService.Format("Status.AllDataReset", safetyBackup);
        }
        catch (Exception exception)
        {
            try
            {
                await _catalogService.SaveAsync(previousCatalog);
                await _settingsRepository.SaveAsync(previousSettings);
                stagedAssets?.Rollback();
                ApplyRuntimeSettings(previousSettings);
                ApplyCatalogSnapshot(previousCatalog, "Status.BackupRollback");
            }
            catch { }
            StatusMessage = _localizationService.Format("Status.ResetFailed", exception.Message);
        }
        finally { stagedAssets?.Dispose(); }
    }

    private UserSettings CreateDefaultSettings() => new()
    {
        LanguageId = _localizationService.DetectSystemLanguage()
    };

    private async Task CheckUpdatesAsync()
    {
        if (_updateService is null) return;
        UpdateStatusText = _localizationService.GetString("Status.CheckingUpdates");
        try
        {
            var result = await _updateService.CheckAsync(GetCurrentVersion());
            _latestReleaseUri = result.ReleaseUrl;
            OnPropertyChanged(nameof(HasLatestRelease));
            OpenLatestReleaseCommand.NotifyCanExecuteChanged();
            UpdateStatusText = result.Status switch
            {
                UpdateCheckStatus.UpdateAvailable => _localizationService.Format("Status.UpdateAvailable",
                    result.CurrentVersion, result.LatestVersion),
                UpdateCheckStatus.UpToDate => _localizationService.Format("Status.UpToDate", result.CurrentVersion),
                _ => _localizationService.Format("Status.UpdateCheckFailed", result.Message ?? string.Empty)
            };
        }
        catch (Exception exception)
        {
            UpdateStatusText = _localizationService.Format("Status.UpdateCheckFailed", exception.Message);
        }
    }

    private Version GetCurrentVersion()
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);
        return new Version(version.Major, Math.Max(0, version.Minor), Math.Max(0, version.Build));
    }

    private void OpenLatestRelease()
    {
        if (_latestReleaseUri is null) return;
        try { Process.Start(new ProcessStartInfo(_latestReleaseUri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception exception) { UpdateStatusText = _localizationService.Format("Status.UpdateCheckFailed", exception.Message); }
    }

    private void OpenRepository()
    {
        try { Process.Start(new ProcessStartInfo(RepositoryUrl) { UseShellExecute = true }); }
        catch (Exception exception) { UpdateStatusText = _localizationService.Format("Status.UpdateCheckFailed", exception.Message); }
    }

    public double? WindowX => _userSettings.WindowX;
    public double? WindowY => _userSettings.WindowY;
    public string SavedWindowState => _userSettings.WindowState;

    public void CaptureWindowGeometry(Window window)
    {
        if (window.WindowState == WindowState.Maximized && window.RestoreBounds.Width > 0)
        {
            var bounds = window.RestoreBounds;
            _userSettings.WindowWidth = Math.Clamp((int)Math.Round(bounds.Width), 900, 4096);
            _userSettings.WindowHeight = Math.Clamp((int)Math.Round(bounds.Height), 500, 4096);
            _userSettings.WindowX = bounds.Left;
            _userSettings.WindowY = bounds.Top;
            _userSettings.WindowState = "Maximized";
        }
        else if (window.WindowState == WindowState.Normal)
        {
            _userSettings.WindowWidth = Math.Clamp((int)Math.Round(window.Width), 900, 4096);
            _userSettings.WindowHeight = Math.Clamp((int)Math.Round(window.Height), 500, 4096);
            _userSettings.WindowX = window.Left;
            _userSettings.WindowY = window.Top;
            _userSettings.WindowState = "Normal";
        }
        ScheduleSettingsSave();
    }

    public async Task<bool> SaveForShutdownAsync()
    {
        await SaveAsync();
        return !HasUnsavedChanges;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _statusMonitorTimer?.Stop();
        _statusMonitorTimer = null;
        _profileExecutionCancellation?.Cancel();
        lock (_settingsSaveSync) _settingsSaveDebounce?.Cancel();
        if (_activityService is not null)
        {
            _activityService.EntryAdded -= ActivityServiceOnEntryAdded;
            _activityService.PersistentViewsChanged -= ActivityServiceOnPersistentViewsChanged;
        }

        foreach (var category in Categories) category.PropertyChanged -= ItemOnPropertyChanged;
        foreach (var profile in _allProfiles)
        {
            profile.PropertyChanged -= ItemOnPropertyChanged;
            foreach (var action in EnumerateActions(profile.Actions))
                action.PropertyChanged -= ItemOnPropertyChanged;
        }
    }

    public void ResetActivityPanelRatio() => UpdateActivityPanelRatio(0.5);

    private bool CanRefreshCurrentStates() => _statusMonitoring is not null && SelectedProfile is not null && !IsStatusRefreshing;

    private async Task RefreshCurrentStatesAsync()
    {
        if (_statusMonitoring is null) return;
        _statusRefreshQueued = true;
        if (IsStatusRefreshing) return;
        IsStatusRefreshing = true;
        try
        {
            while (_statusRefreshQueued)
            {
                _statusRefreshQueued = false;
                var profile = SelectedProfile;
                if (profile is not null)
                    await _statusMonitoring.RefreshSelectedProfileAsync(profile.Actions);
            }
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

    private bool ProfileReferencesAreValid(Guid rootProfileId) =>
        ProfileReferenceValidator.AreValid(_allProfiles.Select(item => item.ToModel()), rootProfileId);


    private async Task RunProfileAsync()
    {
        var profileViewModel = SelectedProfile;
        if (profileViewModel is null || IsProfileRunning)
        {
            return;
        }

        try
        {
            var preflight = BuildPreflight(profileViewModel);
            if (preflight is null || preflight.HasErrors)
            {
                StatusMessage = _localizationService.GetString("Status.PreflightBlocked");
                return;
            }
            if (preflight.RequiresAdministrator && !_dialogService.Confirm(
                    _localizationService.GetString("Dialog.AdminRequiredTitle"),
                    _localizationService.Format("Dialog.AdminRequiredMessage",
                        string.Join(Environment.NewLine, preflight.AdministratorActions))))
            {
                return;
            }

            for (var actionIndex = 0; actionIndex < profileViewModel.Actions.Count; actionIndex++)
                profileViewModel.Actions[actionIndex].SortOrder = actionIndex;

            var profile = profileViewModel.ToModel();
            _allowCloseWithoutConfirmation = false;
            _profileExecutionCancellation = new CancellationTokenSource();
            IsProfileRunning = true;
            SetProfileExecutionState(profileViewModel, ProfileExecutionState.Executing);
            ResetActionExecutionStates(profileViewModel);
            HasExecutionStatus = true;
            CurrentExecutionActionNumber = 0;
            TotalExecutionActions = profile.Actions.Count(action => action.IsEnabled &&
                !string.Equals(action.Type, ActionTypeIds.Comment, StringComparison.OrdinalIgnoreCase));
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
                        SetProfileExecutionState(profileViewModel, ProfileExecutionState.Error);
                        SetExecutionStatus("Execution.Status.CompletedWithErrors");
                        ExecutionErrorMessage = session.Journal.LastOrDefault(entry =>
                            entry.Status is ActionJournalStatus.Failed or ActionJournalStatus.Unsupported)?.ErrorMessage ?? string.Empty;
                        StatusMessage = _localizationService.GetString("Status.ProfileCompletedWithErrors");
                        break;
                    default:
                        SetProfileExecutionState(profileViewModel, ProfileExecutionState.Error);
                        SetExecutionStatus("Execution.Status.Failed");
                        ExecutionErrorMessage = session.Journal.LastOrDefault(entry =>
                            entry.Status is ActionJournalStatus.Failed or ActionJournalStatus.Unsupported)?.ErrorMessage ?? string.Empty;
                        StatusMessage = _localizationService.GetString("Status.ProfileFailed");
                        break;
                }
            }
            catch (Exception exception)
            {
                ReportProfileFailure(profileViewModel, exception);
            }
            finally
            {
                _profileExecutionCancellation.Dispose();
                _profileExecutionCancellation = null;
                IsProfileRunning = false;
                ClearActiveActionExecutionStates();
                if (profileViewModel.ExecutionState == ProfileExecutionState.Executing)
                    SetProfileExecutionState(profileViewModel, ProfileExecutionState.Normal);
            }
        }
        catch (Exception exception)
        {
            // Preflight/model conversion also runs from the UI command. Keep a
            // malformed action from escaping AsyncRelayCommand.Execute (async void)
            // and taking down the WPF process before execution even begins.
            ReportProfileFailure(profileViewModel, exception);
        }
    }

    private void ReportProfileFailure(ProfileItemViewModel profile, Exception exception)
    {
        SetProfileExecutionState(profile, ProfileExecutionState.Error);
        SetExecutionStatus("Execution.Status.Failed");
        ExecutionErrorMessage = exception.Message;
        StatusMessage = _localizationService.GetString("Status.ProfileFailed");
    }

    public void HandleUnhandledUiException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (_disposed) return;

        if (IsProfileRunning && SelectedProfile is { } profile)
        {
            ReportProfileFailure(profile, exception);
            _profileExecutionCancellation?.Cancel();
            return;
        }

        StatusMessage = exception.Message;
    }

    private bool CanRestoreProfile() => SelectedProfile is not null &&
        (RestoreChangeCount > 0 || _pendingRestoreSession?.PendingRestoreCount > 0) &&
        !IsProfileRunning && !IsRestoreRunning && !IsSaving && !_profileRestoreRunner.IsRunning;

    private bool CanDiscardPendingRestore() => SelectedProfile is not null &&
        (RestoreChangeCount > 0 || _pendingRestoreSession?.PendingRestoreCount > 0) &&
        !IsProfileRunning && !IsRestoreRunning && !IsSaving;

    private async Task DiscardPendingRestoreAsync()
    {
        if (IsProfileRunning || IsRestoreRunning || IsSaving) return;
        if (_pendingRestoreSession is null || _pendingRestoreSession.PendingRestoreCount <= 0)
            await RefreshPendingRestoreAsync();
        var session = _pendingRestoreSession;
        if (session is null || session.PendingRestoreCount <= 0 || IsProfileRunning || IsRestoreRunning || IsSaving) return;
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
        _lastSingleActionTestSession = null;
        SetRestoreCount(0);
        RestoreNoticeText = _localizationService.GetString("Restore.Discarded");
    }

    private async Task RefreshPendingRestoreAsync(Guid? profileId = null)
    {
        var id = profileId ?? SelectedProfile?.Id;
        if (id is null)
        {
            _pendingRestoreSession = null;
            _lastSingleActionTestSession = null;
            SetRestoreCount(0);
            RestoreNoticeText = string.Empty;
            UndoSingleActionTestCommand.NotifyCanExecuteChanged();
            return;
        }
        try
        {
            var loaded = await _sessionRepository.GetLatestPendingAsync(id.Value);
            if (SelectedProfile?.Id != id.Value) return;
            _pendingRestoreSession = loaded;
            _lastSingleActionTestSession = loaded?.Origin == ExecutionOrigin.SingleActionTest &&
                loaded.PendingRestoreCount > 0 ? loaded : null;
            SetRestoreCount(_pendingRestoreSession?.PendingRestoreCount ?? 0);
            UndoSingleActionTestCommand.NotifyCanExecuteChanged();
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
        if (_pendingRestoreSession is null || _pendingRestoreSession.PendingRestoreCount <= 0)
            await RefreshPendingRestoreAsync();
        if (_pendingRestoreSession is null || _pendingRestoreSession.PendingRestoreCount <= 0 || !CanRestoreProfile()) return;
        SetRestoreCount(_pendingRestoreSession.PendingRestoreCount);
        IsRestoreRunning = true;
        SetProfileExecutionState(SelectedProfile, ProfileExecutionState.Restoring);
        ResetActionExecutionStates(SelectedProfile);
        HasExecutionStatus = true;
        CurrentExecutionActionNumber = 0;
        TotalExecutionActions = RestoreChangeCount;
        CurrentExecutionActionName = SelectedProfile?.Name ?? string.Empty;
        ExecutionErrorMessage = string.Empty;
        RestoreNoticeText = _localizationService.GetString("Restore.Running");
        _profileExecutionCancellation = new CancellationTokenSource();
        try
        {
            var progress = new Progress<ProfileRestoreProgress>(ApplyRestoreProgress);
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
            ClearActiveActionExecutionStates();
            if (SelectedProfile is not null)
                SetProfileExecutionState(SelectedProfile, ProfileExecutionState.Normal);
        }
    }

    private async Task UndoSingleActionTestAsync()
    {
        if (!CanUndoSingleActionTest) return;
        await RestoreProfileAsync();
        _lastSingleActionTestSession = null;
        UndoSingleActionTestCommand.NotifyCanExecuteChanged();
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

    private static void SetProfileExecutionState(ProfileItemViewModel? profile, ProfileExecutionState state) =>
        profile?.SetExecutionState(state);

    private static void ResetActionExecutionStates(ProfileItemViewModel? profile)
    {
        if (profile is null) return;
        foreach (var action in EnumerateActions(profile.Actions))
            action.ResetExecutionState();
    }

    private ActionItemViewModel? FindRuntimeAction(Guid actionId) =>
        _allProfiles.SelectMany(profile => EnumerateActions(profile.Actions))
            .FirstOrDefault(action => action.Id == actionId);

    private void ApplyActionExecutionState(Guid actionId, ActionExecutionState state) =>
        FindRuntimeAction(actionId)?.SetExecutionState(state);

    private void ClearActiveActionExecutionStates()
    {
        foreach (var action in _allProfiles.SelectMany(profile => EnumerateActions(profile.Actions)))
        {
            if (action.IsExecutionRunning || action.IsRestoring)
                action.ResetExecutionState();
        }
    }

    private static ActionExecutionState MapExecutionState(ActionJournalStatus status) => status switch
    {
        ActionJournalStatus.Running => ActionExecutionState.Running,
        ActionJournalStatus.Success or ActionJournalStatus.Skipped => ActionExecutionState.Completed,
        ActionJournalStatus.Failed or ActionJournalStatus.Unsupported => ActionExecutionState.Error,
        _ => ActionExecutionState.Pending
    };

    private static ActionExecutionState MapRestoreState(PersistentActionRestoreStatus status) => status switch
    {
        PersistentActionRestoreStatus.Restoring => ActionExecutionState.Restoring,
        PersistentActionRestoreStatus.Restored => ActionExecutionState.Completed,
        PersistentActionRestoreStatus.Failed => ActionExecutionState.Error,
        _ => ActionExecutionState.Pending
    };

    private void ApplyExecutionProgress(ProfileExecutionProgress progress)
    {
        CurrentExecutionActionNumber = progress.CurrentActionNumber;
        TotalExecutionActions = progress.TotalActiveActions;
        _currentExecutionActionId = progress.ActionId;
        ApplyActionExecutionState(progress.ActionId, MapExecutionState(progress.Status));
        CurrentExecutionActionName = FindRuntimeAction(progress.ActionId)?.DisplayName
            ?? progress.Action.Name
            ?? progress.Action.Type;
        SetExecutionStatus(GetExecutionStatusResourceKey(progress.Status));
        ExecutionErrorMessage = progress.Status is ActionJournalStatus.Failed or ActionJournalStatus.Unsupported
            ? progress.ErrorMessage ?? string.Empty
            : string.Empty;
    }

    private void ApplyRestoreProgress(ProfileRestoreProgress progress)
    {
        CurrentExecutionActionNumber = progress.CurrentAction;
        TotalExecutionActions = progress.TotalActions;
        _currentExecutionActionId = progress.Action.ActionId;
        ApplyActionExecutionState(progress.Action.ActionId, MapRestoreState(progress.Status));
        CurrentExecutionActionName = FindRuntimeAction(progress.Action.ActionId)?.DisplayName
            ?? progress.Action.ActionName
            ?? progress.Action.ActionType;
        if (progress.Status == PersistentActionRestoreStatus.Restoring)
        {
            SetExecutionStatus("Execution.Status.Restoring");
            ExecutionStatusText = _localizationService.Format("Restore.Progress",
                progress.CurrentAction, progress.TotalActions, CurrentExecutionActionName);
        }
        else
        {
            SetExecutionStatus(progress.Status == PersistentActionRestoreStatus.Restored
                ? "Execution.Status.Success"
                : progress.Status == PersistentActionRestoreStatus.Failed
                    ? "Execution.Status.Failed"
                    : "Execution.Status.Pending");
        }
        ExecutionErrorMessage = progress.Status == PersistentActionRestoreStatus.Failed
            ? progress.Message ?? string.Empty
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

    private void BrowseWorkingDirectory(ActionItemViewModel? action)
    {
        if (action is null || action.Type != ActionTypeIds.ProgramRun || !action.UseCustomWorkingDirectory) return;
        var selected = _dialogService.SelectFolder(
            _localizationService.GetString("Dialog.SelectWorkingDirectoryTitle"), action.WorkingDirectory);
        if (selected is null) return;
        RunGroupedConfigurationChange("select-working-directory", () => action.WorkingDirectory = selected);
    }

    private void SelectArguments(ActionItemViewModel? action)
    {
        if (action is null || action.Type != ActionTypeIds.ProgramRun) return;
        var selected = _dialogService.SelectArgumentsForTarget(
            _localizationService.GetString("Dialog.SelectArgumentsTitle"), action.Arguments, action.Target);
        if (selected is not null)
            RunGroupedConfigurationChange("select-program-arguments", () => action.Arguments = selected);
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
         if (action is null || action.Type is not (ActionTypeIds.ProgramRun or ActionTypeIds.ScriptRun or
             ActionTypeIds.ProcessConfigure or ActionTypeIds.WaitProcessStart or ActionTypeIds.WaitProcessExit or ActionTypeIds.WaitWindow))
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
        action.TargetType = TargetTypeIds.Executable;
        action.Target = target;
        action.TrySetSuggestedName(suggestedName);
        action.WorkingDirectory = workingDirectory;
        action.UseCustomWorkingDirectory = !string.IsNullOrWhiteSpace(workingDirectory);
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
            var action = FindRuntimeAction(actionId);
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
        var result = await EditThemeDraftAsync(new(CustomThemeEditMode.Add, string.Empty, draft,
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

    private async Task ExportThemeAsync(string? themeId)
    {
        var theme = FindCustomTheme(themeId ?? string.Empty);
        if (theme is null || _themeExchangeService is null) return;
        var dialog = new SaveFileDialog { Filter = _localizationService.GetString("CustomTheme.PackageFilter"), FileName = theme.Name + ".sbtheme", AddExtension = true, DefaultExt = ".sbtheme" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            _themeExchangeService.Export(theme, dialog.FileName);
            StatusMessage = _localizationService.GetString("CustomTheme.ExportSuccess");
        }
        catch (Exception exception)
        {
            StatusMessage = _localizationService.Format("CustomTheme.ExchangeError", exception.Message);
            MessageBox.Show(StatusMessage, _localizationService.GetString("CustomTheme.ExchangeTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        await Task.CompletedTask;
    }

    private async Task ImportThemeAsync()
    {
        if (_themeExchangeService is null) return;
        var dialog = new OpenFileDialog { Filter = _localizationService.GetString("CustomTheme.PackageFilter"), DefaultExt = ".sbtheme", CheckFileExists = true };
        if (dialog.ShowDialog() != true) return;
        CustomThemeDefinition? imported = null;
        try
        {
            imported = _themeExchangeService.Import(dialog.FileName, _userSettings.CustomThemes);
            _userSettings.CustomThemes.Add(imported);
            ThemeOptions.Add(new ThemeOptionViewModel(imported, _localizationService));
            _userSettings.SchemaVersion = SettingsSchema.CurrentVersion;
            await _settingsRepository.SaveAsync(_userSettings);
            StatusMessage = _localizationService.Format("CustomTheme.ImportSuccess", imported.Name);
        }
        catch (ThemeExchangeService.UnsupportedThemeAssetException)
        {
            StatusMessage = _localizationService.GetString("CustomTheme.UnsupportedBackgroundMedia");
            MessageBox.Show(StatusMessage, _localizationService.GetString("CustomTheme.ExchangeTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception exception)
        {
            if (imported is not null)
            {
                _userSettings.CustomThemes.Remove(imported);
                var importedOption = GetThemeOptionById(imported.Id);
                if (importedOption is not null) ThemeOptions.Remove(importedOption);
                _themeExchangeService.DeleteOwnedAssets(imported.Id);
            }
            StatusMessage = _localizationService.Format("CustomTheme.ExchangeError", exception.Message);
            MessageBox.Show(StatusMessage, _localizationService.GetString("CustomTheme.ExchangeTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
        var result = await EditThemeDraftAsync(new(mode, draftName, source.Colors.Clone(), GetUnavailableThemeNames(), draftId,
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
        var result = await EditThemeDraftAsync(new(mode, custom.Name, custom.Colors.Clone(),
            GetUnavailableThemeNames(themeId), themeId,
            colors => _themeManager.ApplyTemporary(themeId, colors)), previous);
        if (result is null) return;

        // Resolve again by the same stable ID in case the model changed while the editor was open.
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
        _themeExchangeService?.DeleteOwnedAssets(custom.Id);
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

    private async Task<CustomThemeEditResult?> EditThemeDraftAsync(
        CustomThemeEditRequest request, AppliedThemeSnapshot previous)
    {
        try
        {
            _themeManager.ApplyTemporary(request.ThemeId ?? CustomThemeDefinition.CreateId(), request.Colors);
            var result = await _customThemeEditorService.EditAsync(request);
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
            actionType.RefreshLocalization();
        }
        ActionPickerView.Refresh();

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
        CloseBehaviorOptions[0].RefreshDisplayName(_localizationService.GetString("Settings.CloseSwitchBoard"));
        CloseBehaviorOptions[1].RefreshDisplayName(_localizationService.GetString("Settings.MinimizeToTray"));
        InterfaceDensityOptions[0].RefreshDisplayName(_localizationService.GetString("Settings.Density.Standard"));
        InterfaceDensityOptions[1].RefreshDisplayName(_localizationService.GetString("Settings.Density.Compact"));

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
        foreach (var profile in GetProfilesInGroup(SelectedCategory?.Id ?? Guid.Empty))
            Profiles.Add(profile);
    }

    private void RefreshProfileSelectionDisplayNames()
    {
        foreach (var group in _allProfiles.GroupBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase))
        {
            var profiles = group.ToList();
            if (profiles.Count == 1)
            {
                profiles[0].UpdateSettingsDisplayName(profiles[0].Name);
                continue;
            }

            var groupLabels = profiles.ToDictionary(profile => profile.Id, profile =>
                FindCategory(profile.CategoryId)?.Name ?? profile.Id.ToString("N")[..8]);
            var duplicateLabels = groupLabels.Values
                .GroupBy(label => label, StringComparer.OrdinalIgnoreCase)
                .Where(labelGroup => labelGroup.Count() > 1)
                .Select(labelGroup => labelGroup.Key)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var profile in profiles)
            {
                var label = $"{profile.Name} · {groupLabels[profile.Id]}";
                if (duplicateLabels.Contains(groupLabels[profile.Id]))
                    label += $" · {profile.Id.ToString("N")[..8]}";
                profile.UpdateSettingsDisplayName(label);
            }
        }
    }

    private List<ProfileItemViewModel> GetProfilesInGroup(Guid categoryId) =>
        categoryId == Guid.Empty
            ? RootProfiles.ToList()
            : _allProfiles.Where(profile => profile.CategoryId == categoryId)
                .OrderBy(profile => profile.SortOrder)
                .ToList();

    private void RefreshProfileGroups(IReadOnlyList<RootNavigationItemDefinition>? persistedRootOrder = null)
    {
        foreach (var category in Categories)
        {
            category.Profiles.Clear();
            category.VisibleProfiles.Clear();
        }

        foreach (var profile in _allProfiles.OrderBy(profile => profile.SortOrder))
        {
            var category = FindCategory(profile.CategoryId);
            if (category is not null) category.Profiles.Add(profile);
        }

        SynchronizeRootNavigationItems(persistedRootOrder);
        RootProfiles.Clear();
        foreach (var profile in RootNavigationItems.OfType<ProfileItemViewModel>())
            RootProfiles.Add(profile);

        RefreshProfileSelectionDisplayNames();
        RefreshProfiles();
        RefreshProfileSearchPresentation();
        RefreshActivityProfileOptions();
    }

    private void RefreshProfileSearchPresentation()
    {
        var query = ProfileSearchText.Trim();
        var hasQuery = query.Length > 0;
        bool Matches(ProfileItemViewModel profile) => !hasQuery ||
            profile.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase);

        FilteredRootNavigationItems.Clear();
        foreach (var category in Categories)
        {
            category.VisibleProfiles.Clear();
            foreach (var profile in category.Profiles.Where(Matches)) category.VisibleProfiles.Add(profile);
        }
        foreach (var item in RootNavigationItems)
        {
            switch (item)
            {
                case CategoryItemViewModel category when category.VisibleProfiles.Count > 0:
                    if (hasQuery) category.IsExpanded = true;
                    FilteredRootNavigationItems.Add(category);
                    break;
                case ProfileItemViewModel profile when Matches(profile):
                    FilteredRootNavigationItems.Add(profile);
                    break;
            }
        }
        OnPropertyChanged(nameof(FilteredRootNavigationItems));
    }

    private void RefreshActivityProfileOptions()
    {
        var selected = ActivityProfileFilterId;
        ActivityProfileOptions.Clear();
        ActivityProfileOptions.Add(new ProfileFilterOption(null, _localizationService.GetString("Activity.Filter.AllProfiles")));
        foreach (var profile in _allProfiles.OrderBy(profile => profile.Name, StringComparer.CurrentCultureIgnoreCase))
            ActivityProfileOptions.Add(new ProfileFilterOption(profile.Id, profile.SettingsDisplayName));
        if (selected is not null && ActivityProfileOptions.All(option => option.Id != selected))
            ActivityProfileFilterId = null;
    }

    private void SynchronizeRootNavigationItems(IReadOnlyList<RootNavigationItemDefinition>? persistedRootOrder)
    {
        var categoriesById = Categories.ToDictionary(category => category.Id);
        var rootProfilesById = _allProfiles.Where(profile => profile.CategoryId == Guid.Empty)
            .ToDictionary(profile => profile.Id);
        var ordered = new List<object>();
        var seenCategories = new HashSet<Guid>();
        var seenProfiles = new HashSet<Guid>();

        IEnumerable<object> candidates;
        if (persistedRootOrder is null)
        {
            candidates = RootNavigationItems;
        }
        else
        {
            candidates = persistedRootOrder.Select(entry =>
            {
                if (entry.Kind == RootNavigationItemKind.Category && categoriesById.TryGetValue(entry.Id, out var category))
                    return (object?)category;
                if (entry.Kind == RootNavigationItemKind.Profile && rootProfilesById.TryGetValue(entry.Id, out var profile))
                    return profile;
                return null;
            }).OfType<object>();
        }

        foreach (var candidate in candidates)
        {
            switch (candidate)
            {
                case CategoryItemViewModel category when categoriesById.ContainsKey(category.Id) && seenCategories.Add(category.Id):
                    ordered.Add(category);
                    break;
                case ProfileItemViewModel profile when profile.CategoryId == Guid.Empty &&
                                                   rootProfilesById.ContainsKey(profile.Id) && seenProfiles.Add(profile.Id):
                    ordered.Add(profile);
                    break;
            }
        }

        foreach (var category in Categories.OrderBy(category => category.SortOrder))
            if (seenCategories.Add(category.Id)) ordered.Add(category);
        foreach (var profile in _allProfiles.Where(profile => profile.CategoryId == Guid.Empty)
                     .OrderBy(profile => profile.SortOrder))
            if (seenProfiles.Add(profile.Id)) ordered.Add(profile);

        RootNavigationItems.Clear();
        foreach (var item in ordered) RootNavigationItems.Add(item);
    }

    private void NormalizeSortOrders()
    {
        var rootCategories = RootNavigationItems.OfType<CategoryItemViewModel>().ToList();
        foreach (var category in Categories)
            if (!rootCategories.Contains(category)) rootCategories.Add(category);
        for (var categoryIndex = 0; categoryIndex < rootCategories.Count; categoryIndex++)
        {
            rootCategories[categoryIndex].SortOrder = categoryIndex;
        }

        foreach (var category in Categories)
        {
            NormalizeProfileGroup(category.Id);
        }

        NormalizeProfileGroup(Guid.Empty);
    }

    private void NormalizeProfileGroup(Guid categoryId)
    {
        var profiles = GetProfilesInGroup(categoryId);
        for (var profileIndex = 0; profileIndex < profiles.Count; profileIndex++)
        {
            var profile = profiles[profileIndex];
            profile.SortOrder = profileIndex;
            for (var actionIndex = 0; actionIndex < profile.Actions.Count; actionIndex++)
                profile.Actions[actionIndex].SortOrder = actionIndex;
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
            foreach (var action in EnumerateActions(profile.Actions)) Subscribe(action);
        }
    }

    private static IEnumerable<ActionItemViewModel> EnumerateActions(IEnumerable<ActionItemViewModel> actions)
    {
        foreach (var action in actions)
        {
            yield return action;
            foreach (var nested in EnumerateActions(action.ThenActions.Concat(action.ElseActions)))
                yield return nested;
        }
    }

    private void Subscribe(ObservableObject item) => item.PropertyChanged += ItemOnPropertyChanged;

    private void ItemOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProfileItemViewModel.SettingsDisplayName)) return;
        if ((sender is CategoryItemViewModel && e.PropertyName == nameof(CategoryItemViewModel.Name)) ||
            (sender is ProfileItemViewModel &&
             (e.PropertyName is nameof(ProfileItemViewModel.Name) or nameof(ProfileItemViewModel.CategoryId))))
        {
            RefreshProfileSelectionDisplayNames();
            RefreshProfileSearchPresentation();
            RefreshActivityProfileOptions();
        }

        var isActionExecutionState = e.PropertyName is
            nameof(ActionItemViewModel.ExecutionState) or
            nameof(ActionItemViewModel.IsExecutionRunning) or
            nameof(ActionItemViewModel.IsRestoring) or
            nameof(ActionItemViewModel.HasExecutionError) or
            nameof(ActionItemViewModel.ExecutionStateText);
        if (sender is ActionItemViewModel changedAction &&
            !isActionExecutionState &&
            e.PropertyName is not (nameof(ActionItemViewModel.CurrentStatusText) or
                nameof(ActionItemViewModel.CurrentStatusTooltip) or nameof(ActionItemViewModel.LastChecked)))
        {
            changedAction.ClearExecutionError();
            _allProfiles.FirstOrDefault(profile => profile.Actions.Any(action =>
                EnumerateActions([action]).Any(candidate => candidate.Id == changedAction.Id)))?.ClearExecutionError();
        }
        if (e.PropertyName is nameof(CategoryItemViewModel.IsEditing) or
            nameof(CategoryItemViewModel.EditName) or
            nameof(CategoryItemViewModel.IsExpanded) or
            nameof(ActionItemViewModel.IsExpanded) or
            nameof(ActionItemViewModel.IsAdvancedOptionsExpanded) or
            nameof(ActionItemViewModel.DisplayName) or
            nameof(ActionItemViewModel.Summary) or
            nameof(ActionItemViewModel.IsValid) or
             nameof(ActionItemViewModel.ValidationMessage) or
             nameof(ActionItemViewModel.SupportsRestore) or
             nameof(ActionItemViewModel.IsRestoreScriptEnabled) or
             nameof(ActionItemViewModel.CurrentStatusText) or
             nameof(ActionItemViewModel.CurrentStatusTooltip) or
             nameof(ActionItemViewModel.LastChecked) or
             nameof(ActionItemViewModel.ExecutionState) or
             nameof(ActionItemViewModel.IsExecutionRunning) or
             nameof(ActionItemViewModel.IsRestoring) or
             nameof(ActionItemViewModel.HasExecutionError) or
             nameof(ActionItemViewModel.ExecutionStateText) or
             nameof(ActionItemViewModel.ShouldMonitorCurrentStatus) or
             nameof(ActionItemViewModel.ShouldShowCurrentStatus))
        {
            if (e.PropertyName is nameof(ActionItemViewModel.IsValid) or nameof(ActionItemViewModel.ValidationMessage))
            {
                OnPropertyChanged(nameof(RunAvailabilityMessage));
                OnPropertyChanged(nameof(HasRunValidationIssue));
                BuildPreflight();
                RunProfileCommand.NotifyCanExecuteChanged();
                NavigateToValidationErrorCommand.NotifyCanExecuteChanged();
            }
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
            RedoCommand.NotifyCanExecuteChanged();
        }
        HasUnsavedChanges = HasCatalogChanges();
        RunProfileCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(RunAvailabilityMessage));
        BuildPreflight();
        NavigateToValidationErrorCommand.NotifyCanExecuteChanged();
    }

    private void MarkDirty(string message)
    {
        _suppressUndoTracking = false;
        _undoBaseline = BuildCatalogSnapshot();
        HasUnsavedChanges = HasCatalogChanges();
        StatusMessage = message;
    }

    private void RecordStructuralUndo(string key)
    {
        _undoService.Record(_undoBaseline, $"{key}:{Guid.NewGuid():N}");
        _suppressUndoTracking = true;
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
    }

    private void RunGroupedConfigurationChange(string key, Action change)
    {
        RecordStructuralUndo(key);
        try { change(); }
        finally
        {
            _suppressUndoTracking = false;
            _undoBaseline = BuildCatalogSnapshot();
            HasUnsavedChanges = HasCatalogChanges();
        }
    }

    private void Undo(string? source)
    {
        if (source == "button")
        {
            if (DateTimeOffset.UtcNow - _lastUndoAt < TimeSpan.FromMilliseconds(300)) return;
            _lastUndoAt = DateTimeOffset.UtcNow;
        }
        if (!_undoService.TryUndo(BuildCatalogSnapshot(), out var catalog) || catalog is null) return;
        var categoryId = SelectedCategory?.Id;
        var profileId = SelectedProfile?.Id;
        _suppressUndoTracking = true;
        foreach (var category in Categories) category.PropertyChanged -= ItemOnPropertyChanged;
        foreach (var profile in _allProfiles)
        {
            profile.PropertyChanged -= ItemOnPropertyChanged;
            foreach (var action in EnumerateActions(profile.Actions)) action.PropertyChanged -= ItemOnPropertyChanged;
        }
        Categories.Clear();
        _allProfiles.Clear();
        foreach (var category in catalog.Categories.OrderBy(item => item.SortOrder))
            Categories.Add(new CategoryItemViewModel(category));
        foreach (var profile in catalog.Profiles.OrderBy(item => item.SortOrder))
            _allProfiles.Add(new ProfileItemViewModel(profile, _localizationService));
        OnPropertyChanged(nameof(AllProfiles));
        RefreshProfileGroups(catalog.RootNavigationOrder);
        SubscribeToItems();
        var restoredProfile = _allProfiles.FirstOrDefault(item => item.Id == profileId);
        SelectedCategory = restoredProfile is null
            ? Categories.FirstOrDefault(item => item.Id == categoryId) ?? Categories.FirstOrDefault()
            : FindCategory(restoredProfile.CategoryId);
        SelectedProfile = restoredProfile ?? Profiles.FirstOrDefault() ?? RootProfiles.FirstOrDefault();
        _suppressUndoTracking = false;
        _undoBaseline = BuildCatalogSnapshot();
        HasUnsavedChanges = HasCatalogChanges();
        StatusMessage = _localizationService.GetString("Common.Undo");
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
        NotifyActionCommandStates();
    }

    private void Redo(string? source)
    {
        if (!_undoService.TryRedo(BuildCatalogSnapshot(), out var catalog) || catalog is null) return;
        ApplyCatalogSnapshot(catalog, "Common.Redo");
    }

    private void ApplyCatalogSnapshot(SwitchBoardCatalog catalog, string statusKey)
    {
        var categoryId = SelectedCategory?.Id;
        var profileId = SelectedProfile?.Id;
        _suppressUndoTracking = true;
        foreach (var category in Categories) category.PropertyChanged -= ItemOnPropertyChanged;
        foreach (var profile in _allProfiles)
        {
            profile.PropertyChanged -= ItemOnPropertyChanged;
            foreach (var action in EnumerateActions(profile.Actions)) action.PropertyChanged -= ItemOnPropertyChanged;
        }
        Categories.Clear();
        _allProfiles.Clear();
        foreach (var category in catalog.Categories.OrderBy(item => item.SortOrder))
            Categories.Add(new CategoryItemViewModel(category));
        foreach (var profile in catalog.Profiles.OrderBy(item => item.SortOrder))
            _allProfiles.Add(new ProfileItemViewModel(profile, _localizationService));
        OnPropertyChanged(nameof(AllProfiles));
        RefreshProfileGroups(catalog.RootNavigationOrder);
        SubscribeToItems();
        var restoredProfile = _allProfiles.FirstOrDefault(item => item.Id == profileId);
        SelectedCategory = restoredProfile is null
            ? Categories.FirstOrDefault(item => item.Id == categoryId) ?? Categories.FirstOrDefault()
            : FindCategory(restoredProfile.CategoryId);
        SelectedProfile = restoredProfile ?? Profiles.FirstOrDefault() ?? RootProfiles.FirstOrDefault();
        _suppressUndoTracking = false;
        _undoBaseline = BuildCatalogSnapshot();
        HasUnsavedChanges = HasCatalogChanges();
        StatusMessage = _localizationService.GetString(statusKey);
        UndoCommand.NotifyCanExecuteChanged();
        RedoCommand.NotifyCanExecuteChanged();
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
        var orderedCategories = RootNavigationItems.OfType<CategoryItemViewModel>().ToList();
        foreach (var category in Categories)
            if (!orderedCategories.Contains(category)) orderedCategories.Add(category);
        var categories = orderedCategories.Select((item, index) =>
        {
            var model = item.ToModel();
            model.SortOrder = index;
            return model;
        }).ToList();
        var profiles = new List<ProfileDefinition>();
        foreach (var category in categories)
        {
            AddProfiles(category.Id);
        }

        AddProfiles(Guid.Empty);
        var rootNavigationOrder = RootNavigationItems.Select(item => item switch
        {
            CategoryItemViewModel category => new RootNavigationItemDefinition
            {
                Kind = RootNavigationItemKind.Category,
                Id = category.Id
            },
            ProfileItemViewModel profile when profile.CategoryId == Guid.Empty => new RootNavigationItemDefinition
            {
                Kind = RootNavigationItemKind.Profile,
                Id = profile.Id
            },
            _ => null
        }).OfType<RootNavigationItemDefinition>().ToList();
        return new SwitchBoardCatalog
        {
            SchemaVersion = CatalogSchema.CurrentVersion,
            Categories = categories,
            Profiles = profiles,
            RootNavigationOrder = rootNavigationOrder
        };

        void AddProfiles(Guid categoryId)
        {
            var categoryProfiles = GetProfilesInGroup(categoryId);
            for (var index = 0; index < categoryProfiles.Count; index++)
            {
                var model = categoryProfiles[index].ToModel();
                model.SortOrder = index;
                for (var actionIndex = 0; actionIndex < model.Actions.Count; actionIndex++)
                    model.Actions[actionIndex].SortOrder = actionIndex;
                profiles.Add(model);
            }
        }
    }

    private void SetClean(string message)
    {
        HasUnsavedChanges = false;
        StatusMessage = message;
    }

    private bool HasCatalogChanges() =>
        !string.Equals(
            JsonSerializer.Serialize(BuildCatalogSnapshot()),
            JsonSerializer.Serialize(_savedCatalogBaseline),
            StringComparison.Ordinal);

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
