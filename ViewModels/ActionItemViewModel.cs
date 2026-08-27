using System.IO;
using System.Text.Json.Nodes;
using System.Collections.ObjectModel;
using SwitchBoard.Localization;
using SwitchBoard.Models;
using SwitchBoard.Models.Actions;
using SwitchBoard.Services.Discovery;
using System.Text.Json;
using SwitchBoard.Services.Execution;
using SwitchBoard.Services.Execution.Handlers;
using SwitchBoard.Services.Actions;
using SwitchBoard.ViewModels.Actions;

namespace SwitchBoard.ViewModels;

public sealed class ActionItemViewModel : ObservableObject
{
    private readonly ILocalizationService _localizationService;
    private readonly string? _displayNameResourceKey;
    private string? _name;
    private bool _isEnabled;
    private string _failurePolicyId;
    private string _restoreBehaviorId;
    private string _target;
    private string _targetType;
    private string _arguments;
    private string _workingDirectory;
    private bool _useCustomWorkingDirectory;
    private bool _startOnlyIfNotAlreadyRunning;
    private string _processName;
    private string _executablePath;
    private string _desiredProcessState;
    private string _processOperation;
    private string _processTargetMode;
    private string _serviceName;
    private string _serviceDisplayName;
    private string _desiredServiceState;
    private string _desiredServiceStartupType;
    private string _powerPlanGuid;
    private string _powerPlanName;
    private string _scriptPath;
    private string _scriptType;
    private bool _waitForScriptExit;
    private bool _runAsAdministrator;
    private string _restoreScriptPath;
    private string _restoreScriptArguments;
    private string _restoreScriptWorkingDirectory;
    private string _restoreScriptType;
    private bool _restoreScriptWaitForExit;
    private bool _restoreScriptRunAsAdministrator;
    private int _restoreScriptTimeoutSeconds;
    private int _timeoutSeconds;
    private int _delaySeconds;
    private bool _isExpanded;
    private bool _isAdvancedOptionsExpanded;
    private string _displayDeviceName;
    private string _displayDeviceId;
    private string _displayMonitorName;
    private int _displayWidth;
    private int _displayHeight;
    private int _displayRefreshRate;
    private bool _skipDisplayConfirmation;
    private DisplayResolutionOptionViewModel? _selectedDisplayResolution;
    private bool _retryOnFailure;
    private int _maximumAttempts;
    private int _retryDelaySeconds;
    private string _instanceBehavior;
    private string _windowBehavior;
    private int _windowWaitSeconds;
    private bool _changeAffinity;
    private bool _changePriority;
    private string _processPriority;
    private string _processMemoryPriority;
    private string _processPerformanceMode;
    private bool _waitForProcessStart;
    private int _processStartWaitSeconds;
    private string _windowMatchMode;
    private string _windowTitle;
    private string _audioOutputDeviceId;
    private string _audioOutputDeviceName;
    private string _audioInputDeviceId;
    private string _audioInputDeviceName;
    private bool _setDefaultMultimedia;
    private bool _setDefaultCommunications;
    private bool _changeVolume;
    private int _volumePercent;
    private bool _changeMute;
    private bool _mute;
    private string _deviceInstanceId;
    private string _deviceFriendlyName;
    private string _deviceClass;
    private string _deviceState;
    private string _targetProfileId;
    private string _targetProfileName;
    private string _conditionType;
    private string _conditionValue;
    private string _notificationMessage;
    private string _notificationLevel;
    private string _commentText;
    private string _currentStatusText = string.Empty;
    private string _currentStatusTooltip = string.Empty;
    private DateTimeOffset? _lastChecked;
    private ActionExecutionState _executionState;
    private readonly int _nestingDepth;
    private readonly bool _hasNestingDepthViolation;

    public ActionItemViewModel(ActionDefinition action, ILocalizationService localizationService, int nestingDepth = 0)
    {
        _localizationService = localizationService;
        _nestingDepth = nestingDepth;
        var legacyProcessState = action.Type == ActionTypeIds.ProcessSetState;
        var normalizedType = legacyProcessState ? ActionTypeIds.ProcessConfigure : action.Type;
        _displayNameResourceKey = ActionDescriptorRegistry.Get(normalizedType)?.DisplayNameResourceKey;
        Id = action.Id;
        Type = normalizedType;
        ActionSchemaVersion = action.ActionSchemaVersion;
        SortOrder = action.SortOrder;
        _name = action.Name;
        _isEnabled = action.IsEnabled;
        _failurePolicyId = action.FailurePolicy == ActionFailurePolicy.Stop ? "stop" : "continue";
        _restoreBehaviorId = action.RestoreBehavior switch
        {
            ActionRestoreBehavior.RestorePreviousState => "previous",
            ActionRestoreBehavior.CloseIfStartedBySwitchBoard => "closeStarted",
            ActionRestoreBehavior.RestartIfWasRunning => "restart",
            ActionRestoreBehavior.RunRestoreScript => "restoreScript",
            _ => "none"
        };
        Parameters = action.Parameters.DeepClone().AsObject();
        if (legacyProcessState)
        {
            var legacyState = DefaultIfEmpty(ReadString(ActionParameterNames.DesiredState), ProcessDesiredStateIds.Stopped);
            Parameters[ActionParameterNames.ProcessOperation] =
                string.Equals(legacyState, ProcessDesiredStateIds.Stopped, StringComparison.OrdinalIgnoreCase)
                    ? ProcessOperationIds.Stop
                    : ProcessOperationIds.Configure;
        }
        _target = ReadString(ActionParameterNames.Target);
        _targetType = DefaultIfEmpty(ReadString(ActionParameterNames.TargetType),
            IsProtocolTarget(_target) ? TargetTypeIds.Uri : TargetTypeIds.Executable);
        _arguments = ReadString(ActionParameterNames.Arguments);
        _workingDirectory = ReadString(ActionParameterNames.WorkingDirectory);
        // Older actions had no explicit switch; a stored directory means that it was intentional.
        _useCustomWorkingDirectory = ReadBoolean(ActionParameterNames.UseCustomWorkingDirectory,
            !string.IsNullOrWhiteSpace(_workingDirectory));
        _startOnlyIfNotAlreadyRunning = ReadBoolean(ActionParameterNames.StartOnlyIfNotAlreadyRunning, true);
        _processName = ReadString(ActionParameterNames.ProcessName);
        _executablePath = ReadString(ActionParameterNames.ExecutablePath);
        _desiredProcessState = DefaultIfEmpty(ReadString(ActionParameterNames.DesiredState), ProcessDesiredStateIds.Stopped);
        _processOperation = DefaultIfEmpty(ReadString(ActionParameterNames.ProcessOperation), ProcessOperationIds.Configure);
        _processTargetMode = DefaultIfEmpty(ReadString(ActionParameterNames.ProcessTargetMode), ProcessTargetModeIds.Automatic);
        _serviceName = ReadString(ActionParameterNames.ServiceName);
        _serviceDisplayName = ReadString(ActionParameterNames.ServiceDisplayName);
        _desiredServiceState = DefaultIfEmpty(ReadString(ActionParameterNames.DesiredState), ServiceDesiredStateIds.Unchanged);
        _desiredServiceStartupType = DefaultIfEmpty(ReadString(ActionParameterNames.ServiceStartupType),
            ServiceStartupTypeIds.Unchanged);
        _powerPlanGuid = ReadString(ActionParameterNames.PowerPlanGuid);
        _powerPlanName = ReadString(ActionParameterNames.PowerPlanName);
        _scriptPath = ReadString(ActionParameterNames.ScriptPath);
        _scriptType = DefaultIfEmpty(ReadString(ActionParameterNames.ScriptType), ScriptTypeIds.AutoDetect);
        _waitForScriptExit = ReadBoolean(ActionParameterNames.WaitForExit, true);
        _runAsAdministrator = ReadBoolean(ActionParameterNames.RunAsAdministrator, false);
        _restoreScriptPath = ReadString(ActionParameterNames.RestoreScriptPath);
        _restoreScriptArguments = ReadString(ActionParameterNames.RestoreScriptArguments);
        _restoreScriptWorkingDirectory = ReadString(ActionParameterNames.RestoreScriptWorkingDirectory);
        _restoreScriptType = DefaultIfEmpty(ReadString(ActionParameterNames.RestoreScriptType), ScriptTypeIds.AutoDetect);
        _restoreScriptWaitForExit = ReadBoolean(ActionParameterNames.RestoreScriptWaitForExit, true);
        _restoreScriptRunAsAdministrator = ReadBoolean(ActionParameterNames.RestoreScriptRunAsAdministrator, false);
        _restoreScriptTimeoutSeconds = Math.Clamp(ReadInt32(ActionParameterNames.RestoreScriptTimeoutSeconds, 0), 0, 86400);
        _timeoutSeconds = action.Timeout is { } timeout ? Math.Max(0, (int)Math.Round(timeout.TotalSeconds)) : 0;
        _delaySeconds = Math.Clamp(ReadInt32(ActionParameterNames.DelaySeconds, 0), 0, 3600);
        _displayDeviceName = ReadString(ActionParameterNames.DisplayDeviceName);
        _displayDeviceId = ReadString(ActionParameterNames.DisplayDeviceId);
        _displayMonitorName = ReadString(ActionParameterNames.DisplayName);
        _displayWidth = ReadInt32(ActionParameterNames.DisplayWidth, 0);
        _displayHeight = ReadInt32(ActionParameterNames.DisplayHeight, 0);
        _displayRefreshRate = ReadInt32(ActionParameterNames.DisplayRefreshRate, 0);
        _skipDisplayConfirmation = ReadBoolean(ActionParameterNames.SkipDisplayConfirmation, false);
        _retryOnFailure = action.RetryOnFailure;
        _maximumAttempts = Math.Clamp(action.MaximumAttempts, 1, 10);
        _retryDelaySeconds = Math.Clamp((int)Math.Round(action.RetryDelay.TotalSeconds), 0, 3600);
        _instanceBehavior = DefaultIfEmpty(ReadString(ActionParameterNames.InstanceBehavior),
            _startOnlyIfNotAlreadyRunning ? InstanceBehaviorIds.DoNotStartAgain : InstanceBehaviorIds.StartAnother);
        _windowBehavior = DefaultIfEmpty(ReadString(ActionParameterNames.WindowBehavior), WindowBehaviorIds.None);
        _windowWaitSeconds = Math.Clamp(ReadInt32(ActionParameterNames.WindowWaitSeconds, 10), 1, 300);
        _changeAffinity = ReadBoolean(ActionParameterNames.ChangeAffinity, false);
        _changePriority = ReadBoolean(ActionParameterNames.ChangePriority, false);
        var storedProcessPriority = DefaultIfEmpty(ReadString(ActionParameterNames.ProcessPriority), ProcessPriorityIds.Normal);
        _processPriority = _changePriority && !string.Equals(storedProcessPriority, ProcessPriorityIds.NoChange,
            StringComparison.OrdinalIgnoreCase) ? storedProcessPriority : ProcessPriorityIds.NoChange;
        _processMemoryPriority = DefaultIfEmpty(ReadString(ActionParameterNames.ProcessMemoryPriority),
            ProcessMemoryPriorityIds.NoChange);
        _processPerformanceMode = DefaultIfEmpty(ReadString(ActionParameterNames.ProcessPerformanceMode),
            ProcessPerformanceModeIds.NoChange);
        _waitForProcessStart = ReadBoolean(ActionParameterNames.WaitForProcessStart,
            Type == ActionTypeIds.ScriptRun ? false : true);
        _processStartWaitSeconds = Math.Clamp(ReadInt32(ActionParameterNames.ProcessStartWaitSeconds, 10), 1, 120);
        _windowMatchMode = DefaultIfEmpty(ReadString(ActionParameterNames.WindowMatchMode), WindowMatchModeIds.Any);
        _windowTitle = ReadString(ActionParameterNames.WindowTitle);
        _audioOutputDeviceId = ReadString(ActionParameterNames.AudioOutputDeviceId);
        _audioOutputDeviceName = ReadString("audioOutputDeviceName");
        _audioInputDeviceId = ReadString(ActionParameterNames.AudioInputDeviceId);
        _audioInputDeviceName = ReadString("audioInputDeviceName");
        _setDefaultMultimedia = ReadBoolean(ActionParameterNames.SetDefaultMultimedia, true);
        _setDefaultCommunications = ReadBoolean(ActionParameterNames.SetDefaultCommunications, false);
        _changeVolume = Parameters.ContainsKey(ActionParameterNames.VolumePercent);
        _volumePercent = Math.Clamp(ReadInt32(ActionParameterNames.VolumePercent, 100), 0, 100);
        _changeMute = Parameters.ContainsKey(ActionParameterNames.Mute);
        _mute = ReadBoolean(ActionParameterNames.Mute, false);
        _deviceInstanceId = ReadString(ActionParameterNames.DeviceInstanceId);
        _deviceFriendlyName = ReadString(ActionParameterNames.DeviceFriendlyName);
        _deviceClass = ReadString(ActionParameterNames.DeviceClass);
        _deviceState = DefaultIfEmpty(ReadString(ActionParameterNames.DesiredState), DeviceStateIds.Unchanged);
        _targetProfileId = ReadString(ActionParameterNames.ProfileId);
        _targetProfileName = ReadString("profileName");
        _conditionType = DefaultIfEmpty(ReadString(ActionParameterNames.ConditionType), ConditionTypeIds.ProcessRunning);
        _conditionValue = ReadString(ActionParameterNames.ConditionValue);
        _notificationMessage = ReadString(ActionParameterNames.NotificationMessage);
        _notificationLevel = DefaultIfEmpty(ReadString(ActionParameterNames.NotificationLevel), NotificationLevelIds.Info);
        _commentText = ReadString(ActionParameterNames.CommentText);

        LogicalCpus = [];
        var selectedCpus = ReadIntArray(ActionParameterNames.CpuIndices).ToHashSet();
        for (var index = 0; index < Math.Min(Environment.ProcessorCount, IntPtr.Size * 8); index++)
        {
            var option = new LogicalCpuOptionViewModel(index, selectedCpus.Count == 0 || selectedCpus.Contains(index));
            option.PropertyChanged += (_, _) =>
            {
                NotifyValidation();
                OnPropertyChanged("CpuSelection");
            };
            LogicalCpus.Add(option);
        }
        ThenActions = [];
        ElseActions = [];
        _hasNestingDepthViolation = nestingDepth >= ProfileRunner.MaximumNestingDepth &&
            (Parameters[ActionParameterNames.ThenActions] is JsonArray { Count: > 0 } ||
             Parameters[ActionParameterNames.ElseActions] is JsonArray { Count: > 0 });
        if (nestingDepth < ProfileRunner.MaximumNestingDepth)
        {
            LoadNestedActions(ActionParameterNames.ThenActions, ThenActions, nestingDepth + 1);
            LoadNestedActions(ActionParameterNames.ElseActions, ElseActions, nestingDepth + 1);
        }

        AvailableDisplayResolutions = [];
        AvailableDisplayRefreshRates = [];
        if (_displayWidth > 0 && _displayHeight > 0)
        {
            var persistedMode = new DisplayModeCandidate(
                _displayWidth,
                _displayHeight,
                Math.Max(1, _displayRefreshRate),
                32);
            _selectedDisplayResolution = new DisplayResolutionOptionViewModel(
                _displayWidth,
                _displayHeight,
                [persistedMode]);
            AvailableDisplayResolutions.Add(_selectedDisplayResolution);
            if (_displayRefreshRate > 0) AvailableDisplayRefreshRates.Add(_displayRefreshRate);
        }

        AvailableProcessStates =
        [
            new(ProcessDesiredStateIds.Stopped, "ProcessState.Stopped", localizationService),
            new(ProcessDesiredStateIds.Unchanged, "ProcessState.Unchanged", localizationService)
        ];
        AvailableProcessOperations =
        [
            new(ProcessOperationIds.Configure, "ProcessOperation.Configure", localizationService),
            new(ProcessOperationIds.Stop, "ProcessOperation.Stop", localizationService)
        ];
        AvailableProcessTargetModes =
        [
            new(ProcessTargetModeIds.Automatic, "ProcessTargetMode.Automatic", localizationService),
            new(ProcessTargetModeIds.Manual, "ProcessTargetMode.Manual", localizationService)
        ];
        AvailableServiceStates =
        [
            new(ServiceDesiredStateIds.Unchanged, "ServiceState.Unchanged", localizationService),
            new(ServiceDesiredStateIds.Running, "ServiceState.Running", localizationService),
            new(ServiceDesiredStateIds.Stopped, "ServiceState.Stopped", localizationService)
        ];
        AvailableServiceStartupTypes =
        [
            new(ServiceStartupTypeIds.Unchanged, "ServiceStartupType.Unchanged", localizationService),
            new(ServiceStartupTypeIds.Automatic, "ServiceStartupType.Automatic", localizationService),
            new(ServiceStartupTypeIds.AutomaticDelayed, "ServiceStartupType.AutomaticDelayed", localizationService),
            new(ServiceStartupTypeIds.Manual, "ServiceStartupType.Manual", localizationService),
            new(ServiceStartupTypeIds.Disabled, "ServiceStartupType.Disabled", localizationService)
        ];
        AvailableScriptTypes =
        [
            new(ScriptTypeIds.AutoDetect, "ScriptType.AutoDetect", localizationService),
            new(ScriptTypeIds.PowerShell, "ScriptType.PowerShell", localizationService),
            new(ScriptTypeIds.BatchCmd, "ScriptType.BatchCmd", localizationService)
        ];
        AvailableFailurePolicies =
        [
            new("continue", "FailurePolicy.Continue", localizationService),
            new("stop", "FailurePolicy.Stop", localizationService)
        ];
        AvailableRestoreBehaviors = ActionRestoreBehaviorProvider.Get(Type, ProcessOperation, localizationService);
        if (!AvailableRestoreBehaviors.Any(option => option.Value == _restoreBehaviorId))
            _restoreBehaviorId = "none";
        AvailableProcessPriorities = BuildOptions(localizationService,
            (ProcessPriorityIds.NoChange, "Priority.NoChange"),
            (ProcessPriorityIds.Idle, "Priority.Idle"), (ProcessPriorityIds.BelowNormal, "Priority.BelowNormal"),
            (ProcessPriorityIds.Normal, "Priority.Normal"), (ProcessPriorityIds.AboveNormal, "Priority.AboveNormal"),
            (ProcessPriorityIds.High, "Priority.High"));
        AvailableProcessMemoryPriorities = BuildOptions(localizationService,
            (ProcessMemoryPriorityIds.NoChange, "MemoryPriority.NoChange"),
            (ProcessMemoryPriorityIds.VeryLow, "MemoryPriority.VeryLow"),
            (ProcessMemoryPriorityIds.Low, "MemoryPriority.Low"),
            (ProcessMemoryPriorityIds.Medium, "MemoryPriority.Medium"),
            (ProcessMemoryPriorityIds.BelowNormal, "MemoryPriority.BelowNormal"),
            (ProcessMemoryPriorityIds.Normal, "MemoryPriority.Normal"));
        AvailableProcessPerformanceModes = BuildOptions(localizationService,
            (ProcessPerformanceModeIds.NoChange, "PerformanceMode.NoChange"),
            (ProcessPerformanceModeIds.WindowsDefault, "PerformanceMode.WindowsDefault"),
            (ProcessPerformanceModeIds.HighPerformance, "PerformanceMode.HighPerformance"),
            (ProcessPerformanceModeIds.Efficiency, "PerformanceMode.Efficiency"));
        AvailableWindowMatchModes = BuildOptions(localizationService,
            (WindowMatchModeIds.Any, "WindowMatch.Any"), (WindowMatchModeIds.Contains, "WindowMatch.Contains"),
            (WindowMatchModeIds.Exact, "WindowMatch.Exact"));
        AvailableWindowBehaviors = BuildOptions(localizationService,
            (WindowBehaviorIds.None, "WindowBehavior.None"), (WindowBehaviorIds.Minimize, "WindowBehavior.Minimize"),
            (WindowBehaviorIds.Maximize, "WindowBehavior.Maximize"), (WindowBehaviorIds.Restore, "WindowBehavior.Restore"),
            (WindowBehaviorIds.Hide, "WindowBehavior.Hide"));
        AvailableInstanceBehaviors = BuildOptions(localizationService,
            (InstanceBehaviorIds.DoNotStartAgain, "InstanceBehavior.DoNotStart"),
            (InstanceBehaviorIds.StartAnother, "InstanceBehavior.StartAnother"),
            (InstanceBehaviorIds.RestartExisting, "InstanceBehavior.Restart"));
        AvailableDeviceStates = BuildOptions(localizationService,
            (DeviceStateIds.Enabled, "DeviceState.Enabled"), (DeviceStateIds.Disabled, "DeviceState.Disabled"),
            (DeviceStateIds.Unchanged, "DeviceState.Unchanged"));
        AvailableConditions = BuildOptions(localizationService,
            (ConditionTypeIds.ProcessRunning, "Condition.ProcessRunning"),
            (ConditionTypeIds.ProcessNotRunning, "Condition.ProcessNotRunning"),
            (ConditionTypeIds.ServiceRunning, "Condition.ServiceRunning"),
            (ConditionTypeIds.ServiceStopped, "Condition.ServiceStopped"),
            (ConditionTypeIds.FileExists, "Condition.FileExists"),
            (ConditionTypeIds.FileNotExists, "Condition.FileNotExists"));
        AvailableNotificationLevels = BuildOptions(localizationService,
            (NotificationLevelIds.Info, "NotificationLevel.Info"),
            (NotificationLevelIds.Success, "NotificationLevel.Success"),
            (NotificationLevelIds.Warning, "NotificationLevel.Warning"),
            (NotificationLevelIds.Error, "NotificationLevel.Error"));
        AvailableTargetTypes = BuildOptions(localizationService,
            (TargetTypeIds.Executable, "TargetType.Executable"),
            (TargetTypeIds.Uri, "TargetType.Uri"));
        AddThenNotificationCommand = new RelayCommand(() => AddNestedAction(ActionTypeIds.NotificationShow,
            DefaultNestedParameters(ActionTypeIds.NotificationShow), true));
        AddThenProgramCommand = new RelayCommand(() => AddNestedAction(ActionTypeIds.ProgramRun,
            DefaultNestedParameters(ActionTypeIds.ProgramRun), true));
        AddElseNotificationCommand = new RelayCommand(() => AddNestedAction(ActionTypeIds.NotificationShow,
            DefaultNestedParameters(ActionTypeIds.NotificationShow), false));
        AddElseProgramCommand = new RelayCommand(() => AddNestedAction(ActionTypeIds.ProgramRun,
            DefaultNestedParameters(ActionTypeIds.ProgramRun), false));
        DeleteNestedActionCommand = new RelayCommand<ActionItemViewModel>(DeleteNestedAction, item => item is not null);
        MoveNestedActionUpCommand = new RelayCommand<ActionItemViewModel>(item => MoveNestedAction(item, -1),
            CanMoveNestedActionUp);
        MoveNestedActionDownCommand = new RelayCommand<ActionItemViewModel>(item => MoveNestedAction(item, 1),
            CanMoveNestedActionDown);
    }

    public Guid Id { get; }
    public string Type { get; }
    public int ActionSchemaVersion { get; }
    public int SortOrder { get; set; }
    public JsonObject Parameters { get; }
    public IReadOnlyList<LocalizedValueOptionViewModel> AvailableProcessStates { get; }
    public IReadOnlyList<LocalizedValueOptionViewModel> AvailableProcessOperations { get; }
    public IReadOnlyList<LocalizedValueOptionViewModel> AvailableProcessTargetModes { get; }
    public IReadOnlyList<LocalizedValueOptionViewModel> AvailableServiceStates { get; }
    public IReadOnlyList<LocalizedValueOptionViewModel> AvailableServiceStartupTypes { get; }
    public IReadOnlyList<LocalizedValueOptionViewModel> AvailableScriptTypes { get; }
    public IReadOnlyList<LocalizedValueOptionViewModel> AvailableFailurePolicies { get; }
    public IReadOnlyList<LocalizedValueOptionViewModel> AvailableRestoreBehaviors { get; private set; }
    public IReadOnlyList<LocalizedValueOptionViewModel> AvailableProcessPriorities { get; }
    public IReadOnlyList<LocalizedValueOptionViewModel> AvailableProcessMemoryPriorities { get; }
    public IReadOnlyList<LocalizedValueOptionViewModel> AvailableProcessPerformanceModes { get; }
    public IReadOnlyList<LocalizedValueOptionViewModel> AvailableWindowMatchModes { get; }
    public IReadOnlyList<LocalizedValueOptionViewModel> AvailableWindowBehaviors { get; }
    public IReadOnlyList<LocalizedValueOptionViewModel> AvailableInstanceBehaviors { get; }
    public IReadOnlyList<LocalizedValueOptionViewModel> AvailableDeviceStates { get; }
    public IReadOnlyList<LocalizedValueOptionViewModel> AvailableConditions { get; }
    public IReadOnlyList<LocalizedValueOptionViewModel> AvailableNotificationLevels { get; }
    public IReadOnlyList<LocalizedValueOptionViewModel> AvailableTargetTypes { get; }
    public ObservableCollection<LogicalCpuOptionViewModel> LogicalCpus { get; }
    public ObservableCollection<ActionItemViewModel> ThenActions { get; }
    public ObservableCollection<ActionItemViewModel> ElseActions { get; }
    internal ILocalizationService LocalizationService => _localizationService;
    internal bool HasNestingDepthViolation => _hasNestingDepthViolation;
    internal int DisplayWidth => _displayWidth;
    internal int DisplayHeight => _displayHeight;
    public bool CanAddNestedActions => _nestingDepth + 1 < ProfileRunner.MaximumNestingDepth &&
        Type == ActionTypeIds.ConditionIf;
    public RelayCommand AddThenNotificationCommand { get; }
    public RelayCommand AddThenProgramCommand { get; }
    public RelayCommand AddElseNotificationCommand { get; }
    public RelayCommand AddElseProgramCommand { get; }
    public RelayCommand<ActionItemViewModel> DeleteNestedActionCommand { get; }
    public RelayCommand<ActionItemViewModel> MoveNestedActionUpCommand { get; }
    public RelayCommand<ActionItemViewModel> MoveNestedActionDownCommand { get; }

    public string? Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                OnPropertyChanged(nameof(DisplayName));
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    public bool IsComment => Type == ActionTypeIds.Comment;
    public string CommentText
    {
        get => _commentText;
        set
        {
            if (!SetProperty(ref _commentText, value)) return;
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(Summary));
        }
    }

    public string DisplayName => IsComment && !string.IsNullOrWhiteSpace(CommentText)
        ? CommentText.Trim()
        : !string.IsNullOrWhiteSpace(Name)
        ? Name.Trim()
        : _displayNameResourceKey is not null
            ? _localizationService.GetString(_displayNameResourceKey)
            : Type;

    public string Summary => ActionSummaryService.GetSummary(this);

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value)) return;
            if (!value) IsAdvancedOptionsExpanded = false;
        }
    }
    public bool IsAdvancedOptionsExpanded { get => _isAdvancedOptionsExpanded; set => SetProperty(ref _isAdvancedOptionsExpanded, value); }
    public bool IsEnabled
    {
        get => _isEnabled;
        set { if (SetProperty(ref _isEnabled, value)) NotifyValidation(); }
    }
    public string FailurePolicyId { get => _failurePolicyId; set => SetProperty(ref _failurePolicyId, value); }
    public string RestoreBehaviorId
    {
        get => _restoreBehaviorId;
        set
        {
            if (!SetProperty(ref _restoreBehaviorId, value)) return;
            OnPropertyChanged(nameof(IsRestoreScriptEnabled));
            OnPropertyChanged(nameof(IsRestartWindowBehaviorEnabled));
            OnPropertyChanged(nameof(Summary));
            NotifyValidation();
        }
    }
    public bool SupportsRestore => Type switch
    {
        ActionTypeIds.Comment => false,
        ActionTypeIds.ProgramRun => !string.IsNullOrWhiteSpace(Target),
        ActionTypeIds.ProcessConfigure => true,
        ActionTypeIds.ServiceSetState or ActionTypeIds.PowerSetPlan or ActionTypeIds.DisplayConfigure or ActionTypeIds.ScriptRun or
            ActionTypeIds.AudioConfigure or ActionTypeIds.DeviceSetState => true,
        _ => false
    };
    public bool IsRestoreScriptEnabled => Type == ActionTypeIds.ScriptRun && RestoreBehaviorId == "restoreScript";
    public string Arguments { get => _arguments; set => SetProperty(ref _arguments, value); }
    public string TargetType
    {
        get => _targetType;
        set
        {
            var normalized = string.Equals(value, TargetTypeIds.Uri, StringComparison.OrdinalIgnoreCase)
                ? TargetTypeIds.Uri : TargetTypeIds.Executable;
            if (!SetProperty(ref _targetType, normalized)) return;
            OnPropertyChanged(nameof(IsUriTarget));
            OnPropertyChanged(nameof(IsExecutableTarget));
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(SupportsRestore));
            NotifyValidation();
        }
    }
    public bool IsUriTarget => string.Equals(TargetType, TargetTypeIds.Uri, StringComparison.OrdinalIgnoreCase);
    public bool IsExecutableTarget => !IsUriTarget;
    public string WorkingDirectory { get => _workingDirectory; set { if (SetProperty(ref _workingDirectory, value)) OnPropertyChanged(nameof(Summary)); } }
    public bool UseCustomWorkingDirectory
    {
        get => _useCustomWorkingDirectory;
        set
        {
            if (SetProperty(ref _useCustomWorkingDirectory, value)) OnPropertyChanged(nameof(Summary));
        }
    }
    public bool StartOnlyIfNotAlreadyRunning { get => _startOnlyIfNotAlreadyRunning; set => SetProperty(ref _startOnlyIfNotAlreadyRunning, value); }
    public bool WaitForScriptExit { get => _waitForScriptExit; set => SetProperty(ref _waitForScriptExit, value); }
    public bool RunAsAdministrator { get => _runAsAdministrator; set => SetProperty(ref _runAsAdministrator, value); }
    public string RestoreScriptPath { get => _restoreScriptPath; set => SetValidationProperty(ref _restoreScriptPath, value); }
    public string RestoreScriptArguments { get => _restoreScriptArguments; set => SetProperty(ref _restoreScriptArguments, value); }
    public string RestoreScriptWorkingDirectory { get => _restoreScriptWorkingDirectory; set => SetProperty(ref _restoreScriptWorkingDirectory, value); }
    public string RestoreScriptType { get => _restoreScriptType; set => SetProperty(ref _restoreScriptType, value); }
    public bool RestoreScriptWaitForExit { get => _restoreScriptWaitForExit; set => SetProperty(ref _restoreScriptWaitForExit, value); }
    public bool RestoreScriptRunAsAdministrator { get => _restoreScriptRunAsAdministrator; set => SetProperty(ref _restoreScriptRunAsAdministrator, value); }
    public int RestoreScriptTimeoutSeconds { get => _restoreScriptTimeoutSeconds; set => SetProperty(ref _restoreScriptTimeoutSeconds, Math.Clamp(value, 0, 86400)); }
    public int TimeoutSeconds { get => _timeoutSeconds; set => SetProperty(ref _timeoutSeconds, Math.Clamp(value, 0, 86400)); }
    public bool RetryOnFailure { get => _retryOnFailure; set { if (SetProperty(ref _retryOnFailure, value)) OnPropertyChanged(nameof(Summary)); } }
    public int MaximumAttempts { get => _maximumAttempts; set => SetProperty(ref _maximumAttempts, Math.Clamp(value, 1, 10)); }
    public int RetryDelaySeconds { get => _retryDelaySeconds; set => SetProperty(ref _retryDelaySeconds, Math.Clamp(value, 0, 3600)); }
    public string InstanceBehavior { get => _instanceBehavior; set { if (SetProperty(ref _instanceBehavior, value)) OnPropertyChanged(nameof(Summary)); } }
    public string WindowBehavior
    {
        get => _windowBehavior;
        set
        {
            if (!SetProperty(ref _windowBehavior, value)) return;
            OnPropertyChanged(nameof(IsWindowWaitEnabled));
            OnPropertyChanged(nameof(Summary));
        }
    }
    public bool IsRestartWindowBehaviorEnabled => Type == ActionTypeIds.ProcessConfigure &&
        IsProcessStopMode && string.Equals(RestoreBehaviorId, "restart", StringComparison.OrdinalIgnoreCase);
    public bool IsWindowWaitEnabled => !string.Equals(WindowBehavior, WindowBehaviorIds.None, StringComparison.OrdinalIgnoreCase);
    public int WindowWaitSeconds { get => _windowWaitSeconds; set => SetProperty(ref _windowWaitSeconds, Math.Clamp(value, 1, 300)); }
    public bool ChangeAffinity { get => _changeAffinity; set { if (SetProperty(ref _changeAffinity, value)) { OnPropertyChanged(nameof(HasPostLaunchProcessSettings)); NotifyValidation(); } } }
    // Kept as a compatibility property for old profiles and integrations. The dropdown is authoritative for new edits.
    public bool ChangePriority { get => _changePriority; set { if (SetProperty(ref _changePriority, value)) { OnPropertyChanged(nameof(HasPostLaunchProcessSettings)); NotifyValidation(); } } }
    public bool ShouldChangeProcessPriority => !string.Equals(ProcessPriority, ProcessPriorityIds.NoChange, StringComparison.OrdinalIgnoreCase);
    public bool ShouldChangeMemoryPriority => ProcessSettingsService.IsConcreteMemoryPriority(ProcessMemoryPriority);
    public bool ShouldChangePerformanceMode => ProcessSettingsService.IsConcretePerformanceMode(ProcessPerformanceMode);
    public bool HasPostLaunchProcessSettings => Type == ActionTypeIds.ProgramRun &&
        (ChangeAffinity || ShouldChangeProcessPriority || ShouldChangeMemoryPriority || ShouldChangePerformanceMode);
    public string ProcessPriority
    {
        get => _processPriority;
        set
        {
            if (!SetProperty(ref _processPriority, value)) return;
            var selectedChange = ShouldChangeProcessPriority;
            if (_changePriority != selectedChange)
            {
                _changePriority = selectedChange;
                OnPropertyChanged(nameof(ChangePriority));
            }
            OnPropertyChanged(nameof(HasPostLaunchProcessSettings));
            OnPropertyChanged(nameof(Summary));
            NotifyValidation();
        }
    }
    public string ProcessMemoryPriority
    {
        get => _processMemoryPriority;
        set
        {
            if (!SetProperty(ref _processMemoryPriority, value)) return;
            OnPropertyChanged(nameof(HasPostLaunchProcessSettings));
            OnPropertyChanged(nameof(Summary));
            NotifyValidation();
        }
    }
    public string ProcessPerformanceMode
    {
        get => _processPerformanceMode;
        set
        {
            if (!SetProperty(ref _processPerformanceMode, value)) return;
            OnPropertyChanged(nameof(HasPostLaunchProcessSettings));
            OnPropertyChanged(nameof(Summary));
            NotifyValidation();
        }
    }
    public bool WaitForProcessStart
    {
        get => _waitForProcessStart;
        set => SetProperty(ref _waitForProcessStart, value);
    }
    public int ProcessStartWaitSeconds
    {
        get => _processStartWaitSeconds;
        set => SetProperty(ref _processStartWaitSeconds, Math.Clamp(value, 1, 120));
    }
    public string ProcessOperation
    {
        get => _processOperation;
        set
        {
            if (!SetProperty(ref _processOperation, value)) return;
            OnPropertyChanged(nameof(IsProcessStopMode));
            OnPropertyChanged(nameof(IsRestartWindowBehaviorEnabled));
            OnPropertyChanged(nameof(IsProcessConfigureOperation));
            AvailableRestoreBehaviors = ActionRestoreBehaviorProvider.Get(Type, value, _localizationService);
            if (!IsRestoreBehaviorAvailable(RestoreBehaviorId)) RestoreBehaviorId = "none";
            OnPropertyChanged(nameof(AvailableRestoreBehaviors));
            OnPropertyChanged(nameof(Summary));
            NotifyValidation();
        }
    }
    public bool IsProcessStopMode => string.Equals(ProcessOperation, ProcessOperationIds.Stop, StringComparison.OrdinalIgnoreCase);
    public bool IsProcessConfigureOperation => Type == ActionTypeIds.ProcessConfigure && !IsProcessStopMode;
    public string ProcessTargetMode
    {
        get => _processTargetMode;
        set
        {
            if (!SetProperty(ref _processTargetMode, value)) return;
            OnPropertyChanged(nameof(IsManualProcessTarget));
            OnPropertyChanged(nameof(Summary));
            NotifyValidation();
        }
    }
    public bool IsManualProcessTarget => string.Equals(ProcessTargetMode, ProcessTargetModeIds.Manual, StringComparison.OrdinalIgnoreCase);
    public string WindowMatchMode { get => _windowMatchMode; set { if (SetProperty(ref _windowMatchMode, value)) NotifyValidation(); } }
    public string WindowTitle { get => _windowTitle; set => SetValidationProperty(ref _windowTitle, value); }
    public string AudioOutputDeviceId { get => _audioOutputDeviceId; set => SetValidationProperty(ref _audioOutputDeviceId, value); }
    public string AudioOutputDeviceName { get => _audioOutputDeviceName; set => SetWithSummary(ref _audioOutputDeviceName, value); }
    public string AudioInputDeviceId { get => _audioInputDeviceId; set => SetValidationProperty(ref _audioInputDeviceId, value); }
    public string AudioInputDeviceName { get => _audioInputDeviceName; set => SetWithSummary(ref _audioInputDeviceName, value); }
    public bool SetDefaultMultimedia { get => _setDefaultMultimedia; set => SetProperty(ref _setDefaultMultimedia, value); }
    public bool SetDefaultCommunications { get => _setDefaultCommunications; set => SetProperty(ref _setDefaultCommunications, value); }
    public bool ChangeVolume { get => _changeVolume; set { if (SetProperty(ref _changeVolume, value)) NotifyValidation(); } }
    public int VolumePercent { get => _volumePercent; set => SetProperty(ref _volumePercent, Math.Clamp(value, 0, 100)); }
    public bool ChangeMute { get => _changeMute; set { if (SetProperty(ref _changeMute, value)) NotifyValidation(); } }
    public bool Mute { get => _mute; set => SetProperty(ref _mute, value); }
    public string DeviceInstanceId { get => _deviceInstanceId; set => SetValidationProperty(ref _deviceInstanceId, value); }
    public string DeviceFriendlyName { get => _deviceFriendlyName; set => SetWithSummary(ref _deviceFriendlyName, value); }
    public string DeviceClass { get => _deviceClass; set => SetProperty(ref _deviceClass, value); }
    public string DeviceState { get => _deviceState; set => SetValidationProperty(ref _deviceState, value); }
    public string TargetProfileId
    {
        get => _targetProfileId;
        set
        {
            if (!SetProperty(ref _targetProfileId, value)) return;
            OnPropertyChanged(nameof(TargetProfileGuid));
            NotifyValidation();
        }
    }
    public Guid? TargetProfileGuid
    {
        get => Guid.TryParse(TargetProfileId, out var id) ? id : null;
        set => TargetProfileId = value?.ToString("D") ?? string.Empty;
    }
    public string TargetProfileName { get => _targetProfileName; set => SetWithSummary(ref _targetProfileName, value); }
    public string ConditionType { get => _conditionType; set => SetValidationProperty(ref _conditionType, value); }
    public string ConditionValue { get => _conditionValue; set => SetValidationProperty(ref _conditionValue, value); }
    public string NotificationMessage { get => _notificationMessage; set => SetValidationProperty(ref _notificationMessage, value); }
    public string NotificationLevel { get => _notificationLevel; set => SetProperty(ref _notificationLevel, value); }

    public void SelectAllCpus(bool selected)
    {
        foreach (var cpu in LogicalCpus) cpu.IsSelected = selected;
        NotifyValidation();
    }

    public void SelectAllExceptCpu0()
    {
        foreach (var cpu in LogicalCpus) cpu.IsSelected = cpu.Index != 0;
        NotifyValidation();
    }

    public ActionItemViewModel? AddNestedAction(string type, JsonObject? parameters, bool thenBranch)
    {
        if (!CanAddNestedActions) return null;
        var target = thenBranch ? ThenActions : ElseActions;
        var item = new ActionItemViewModel(new ActionDefinition
        {
            Type = type,
            SortOrder = target.Count,
            Parameters = parameters?.DeepClone().AsObject() ?? DefaultNestedParameters(type)
        }, _localizationService, _nestingDepth + 1);
        SubscribeNested(item);
        target.Add(item);
        NotifyValidation();
        OnPropertyChanged("NestedConfiguration");
        return item;
    }

    private static JsonObject DefaultNestedParameters(string type) =>
        ActionDescriptorRegistry.Get(type)?.CreateDefaultParameters(nested: true) ?? [];

    private void DeleteNestedAction(ActionItemViewModel? item)
    {
        if (item is null) return;
        ThenActions.Remove(item);
        ElseActions.Remove(item);
        NotifyValidation();
        OnPropertyChanged("NestedConfiguration");
        MoveNestedActionUpCommand.NotifyCanExecuteChanged();
        MoveNestedActionDownCommand.NotifyCanExecuteChanged();
    }

    private bool CanMoveNestedActionUp(ActionItemViewModel? item) =>
        TryFindNestedAction(item, out _, out var index) && index > 0;

    private bool CanMoveNestedActionDown(ActionItemViewModel? item) =>
        TryFindNestedAction(item, out var branch, out var index) && index < branch.Count - 1;

    private void MoveNestedAction(ActionItemViewModel? item, int direction)
    {
        if (!TryFindNestedAction(item, out var branch, out var index)) return;
        var newIndex = index + direction;
        if (newIndex < 0 || newIndex >= branch.Count) return;
        branch.Move(index, newIndex);
        NotifyValidation();
        OnPropertyChanged("NestedConfiguration");
        MoveNestedActionUpCommand.NotifyCanExecuteChanged();
        MoveNestedActionDownCommand.NotifyCanExecuteChanged();
    }

    private bool TryFindNestedAction(ActionItemViewModel? item,
        out ObservableCollection<ActionItemViewModel> branch, out int index)
    {
        if (item is not null && (index = ThenActions.IndexOf(item)) >= 0)
        {
            branch = ThenActions;
            return true;
        }
        if (item is not null && (index = ElseActions.IndexOf(item)) >= 0)
        {
            branch = ElseActions;
            return true;
        }
        branch = ThenActions;
        index = -1;
        return false;
    }

    private void SubscribeNested(ActionItemViewModel item) => item.PropertyChanged += (_, _) =>
    {
        NotifyValidation();
        OnPropertyChanged("NestedConfiguration");
    };

    public string Target { get => _target; set => SetWithSummary(ref _target, value); }
    public string ProcessName
    {
        get => _processName;
        set
        {
            if (!SetProperty(ref _processName, value)) return;
            ClearCurrentStatus();
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(SupportsRestore));
            NotifyValidation();
        }
    }
    public string ExecutablePath { get => _executablePath; set => SetWithSummary(ref _executablePath, value); }
    // Retained for deserializing old process.setState profiles; the editor uses ProcessOperation.
    public string DesiredProcessState { get => _desiredProcessState; set => SetWithSummary(ref _desiredProcessState, value); }
    public string ServiceName { get => _serviceName; set => SetWithSummary(ref _serviceName, value); }
    public string ServiceDisplayName { get => _serviceDisplayName; set => SetWithSummary(ref _serviceDisplayName, value); }
    public string DesiredServiceState { get => _desiredServiceState; set => SetWithSummary(ref _desiredServiceState, value); }
    public string DesiredServiceStartupType { get => _desiredServiceStartupType; set => SetWithSummary(ref _desiredServiceStartupType, value); }
    public string PowerPlanGuid { get => _powerPlanGuid; set => SetWithSummary(ref _powerPlanGuid, value); }
    public string PowerPlanName { get => _powerPlanName; set => SetWithSummary(ref _powerPlanName, value); }
    public string ScriptPath { get => _scriptPath; set => SetWithSummary(ref _scriptPath, value); }
    public string ScriptType { get => _scriptType; set => SetProperty(ref _scriptType, value); }
    public string DisplayDeviceName { get => _displayDeviceName; set => SetValidationProperty(ref _displayDeviceName, value); }
    public string DisplayDeviceId { get => _displayDeviceId; set => SetProperty(ref _displayDeviceId, value); }
    public string DisplayMonitorName { get => _displayMonitorName; set => SetWithSummary(ref _displayMonitorName, value); }
    public ObservableCollection<DisplayResolutionOptionViewModel> AvailableDisplayResolutions { get; }
    public ObservableCollection<int> AvailableDisplayRefreshRates { get; }

    public DisplayResolutionOptionViewModel? SelectedDisplayResolution
    {
        get => _selectedDisplayResolution;
        set
        {
            if (!SetProperty(ref _selectedDisplayResolution, value) || value is null) return;
            _displayWidth = value.Width;
            _displayHeight = value.Height;
            AvailableDisplayRefreshRates.Clear();
            foreach (var rate in value.Modes.Select(mode => mode.RefreshRate).Distinct().Order())
            {
                AvailableDisplayRefreshRates.Add(rate);
            }

            if (!AvailableDisplayRefreshRates.Contains(_displayRefreshRate))
            {
                DisplayRefreshRate = AvailableDisplayRefreshRates.FirstOrDefault();
            }
            OnPropertyChanged(nameof(Summary));
            NotifyValidation();
        }
    }

    public int DisplayRefreshRate
    {
        get => _displayRefreshRate;
        set
        {
            if (AvailableDisplayRefreshRates.Count > 0 && !AvailableDisplayRefreshRates.Contains(value)) return;
            if (SetProperty(ref _displayRefreshRate, value))
            {
                OnPropertyChanged(nameof(SelectedDisplayRefreshRate));
                OnPropertyChanged(nameof(Summary));
                NotifyValidation();
            }
        }
    }
    public int SelectedDisplayRefreshRate
    {
        get => DisplayRefreshRate;
        set => DisplayRefreshRate = value;
    }
    public bool SkipDisplayConfirmation
    {
        get => _skipDisplayConfirmation;
        set { if (SetProperty(ref _skipDisplayConfirmation, value)) OnPropertyChanged(nameof(Summary)); }
    }

    public bool IsValid => !IsEnabled || ValidationLevel != ValidationSeverity.Error;
    public bool ShouldMonitorCurrentStatus => !IsComment && (Type != ActionTypeIds.ProcessConfigure ||
        (IsEnabled && !string.IsNullOrWhiteSpace(ProcessName) && ValidationLevel == ValidationSeverity.Valid));
    public bool ShouldShowCurrentStatus => IsEnabled && !string.IsNullOrWhiteSpace(CurrentStatusText) &&
        ShouldMonitorCurrentStatus;
    public string CurrentStatusText
    {
        get => _currentStatusText;
        private set => SetProperty(ref _currentStatusText, value);
    }
    public string CurrentStatusTooltip
    {
        get => _currentStatusTooltip;
        private set => SetProperty(ref _currentStatusTooltip, value);
    }
    public DateTimeOffset? LastChecked => _lastChecked;

    public void SetCurrentStatus(string? status, string? technicalDetails, DateTimeOffset checkedAt)
    {
        if (!ShouldMonitorCurrentStatus)
        {
            ClearCurrentStatus();
            return;
        }

        CurrentStatusText = string.IsNullOrWhiteSpace(status)
            ? _localizationService.GetString("ActionStatus.Unavailable")
            : status;
        _lastChecked = checkedAt;
        OnPropertyChanged(nameof(LastChecked));
        CurrentStatusTooltip = string.IsNullOrWhiteSpace(technicalDetails)
            ? _localizationService.Format("ActionStatus.LastChecked", checkedAt.ToLocalTime().ToString("HH:mm:ss"))
            : technicalDetails + Environment.NewLine +
              _localizationService.Format("ActionStatus.LastChecked", checkedAt.ToLocalTime().ToString("HH:mm:ss"));
        OnPropertyChanged(nameof(ShouldShowCurrentStatus));
    }

    public void ClearCurrentStatus()
    {
        CurrentStatusText = string.Empty;
        CurrentStatusTooltip = string.Empty;
        _lastChecked = null;
        OnPropertyChanged(nameof(LastChecked));
        OnPropertyChanged(nameof(ShouldShowCurrentStatus));
    }
    public string ValidationMessage => ActionValidationService.GetMessage(this);

    public ValidationSeverity ValidationLevel => ActionValidationService.GetSeverity(this);

    public int DelaySeconds
    {
        get => _delaySeconds;
        set
        {
            if (SetProperty(ref _delaySeconds, Math.Clamp(value, 0, 3600)))
            {
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    public void RefreshDisplayName()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(ExecutionStateText));
        NotifyValidation();
        foreach (var option in AvailableProcessStates.Concat(AvailableServiceStates).Concat(AvailableServiceStartupTypes)
                     .Concat(AvailableScriptTypes).Concat(AvailableFailurePolicies).Concat(AvailableRestoreBehaviors)
                     .Concat(AvailableProcessPriorities).Concat(AvailableProcessMemoryPriorities)
                     .Concat(AvailableProcessPerformanceModes)
                     .Concat(AvailableWindowMatchModes).Concat(AvailableWindowBehaviors)
                     .Concat(AvailableInstanceBehaviors).Concat(AvailableDeviceStates).Concat(AvailableConditions)
                     .Concat(AvailableNotificationLevels).Concat(AvailableProcessOperations)
                     .Concat(AvailableProcessTargetModes).Concat(AvailableTargetTypes))
        {
            option.RefreshDisplayName();
        }
    }

    internal string GetLocalizedText(string key) => _localizationService.GetString(key);

    public void TrySetSuggestedName(string? suggestedName)
    {
        if (string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(suggestedName))
        {
            Name = suggestedName.Trim();
        }
    }

    public void ApplyDisplayCandidate(DisplayCandidate candidate, bool notifyChanges = true)
    {
        var previousMonitorName = DisplayMonitorName;
        var desiredWidth = _displayWidth > 0 ? _displayWidth : candidate.CurrentWidth;
        var desiredHeight = _displayHeight > 0 ? _displayHeight : candidate.CurrentHeight;
        var desiredRate = _displayRefreshRate > 0 ? _displayRefreshRate : candidate.CurrentRefreshRate;
        AvailableDisplayResolutions.Clear();
        foreach (var group in candidate.Modes.GroupBy(mode => (mode.Width, mode.Height)))
        {
            AvailableDisplayResolutions.Add(new DisplayResolutionOptionViewModel(
                group.Key.Width,
                group.Key.Height,
                group.OrderBy(mode => mode.RefreshRate).ToList()));
        }

        var resolution = AvailableDisplayResolutions.FirstOrDefault(option =>
                             option.Width == desiredWidth && option.Height == desiredHeight)
                         ?? AvailableDisplayResolutions.FirstOrDefault(option =>
                             option.Width == candidate.CurrentWidth && option.Height == candidate.CurrentHeight)
                         ?? AvailableDisplayResolutions.FirstOrDefault();
        AvailableDisplayRefreshRates.Clear();
        if (resolution is not null)
        {
            foreach (var rate in resolution.Modes.Select(mode => mode.RefreshRate).Distinct().Order())
            {
                AvailableDisplayRefreshRates.Add(rate);
            }
        }

        var selectedRate = AvailableDisplayRefreshRates.Contains(desiredRate)
            ? desiredRate
            : AvailableDisplayRefreshRates.Contains(candidate.CurrentRefreshRate)
                ? candidate.CurrentRefreshRate
                : AvailableDisplayRefreshRates.FirstOrDefault();
        if (notifyChanges)
        {
            DisplayDeviceName = candidate.DeviceName;
            DisplayDeviceId = candidate.DeviceId;
            DisplayMonitorName = candidate.DisplayName;
            UpdateGeneratedDisplayActionName(candidate.DisplayName, previousMonitorName, notifyChanges: true);
            SelectedDisplayResolution = resolution;
            DisplayRefreshRate = selectedRate;
        }
        else
        {
            _displayDeviceName = candidate.DeviceName;
            _displayDeviceId = candidate.DeviceId;
            _displayMonitorName = candidate.DisplayName;
            _selectedDisplayResolution = resolution;
            _displayWidth = resolution?.Width ?? 0;
            _displayHeight = resolution?.Height ?? 0;
            _displayRefreshRate = selectedRate;
            UpdateGeneratedDisplayActionName(candidate.DisplayName, previousMonitorName, notifyChanges: false);
        }
    }

    private void UpdateGeneratedDisplayActionName(string displayName, string previousMonitorName, bool notifyChanges)
    {
        var isGeneratedName = string.IsNullOrWhiteSpace(Name) ||
                              (!string.IsNullOrWhiteSpace(previousMonitorName) &&
                               string.Equals(Name, previousMonitorName, StringComparison.OrdinalIgnoreCase));
        if (!isGeneratedName || string.IsNullOrWhiteSpace(displayName)) return;
        if (notifyChanges)
        {
            Name = displayName;
            return;
        }

        _name = displayName.Trim();
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Summary));
    }

    public ActionExecutionState ExecutionState
    {
        get => _executionState;
        private set
        {
            if (!SetProperty(ref _executionState, value)) return;
            OnPropertyChanged(nameof(IsExecutionRunning));
            OnPropertyChanged(nameof(IsRestoring));
            OnPropertyChanged(nameof(HasExecutionError));
            OnPropertyChanged(nameof(ExecutionStateText));
        }
    }

    public bool IsExecutionRunning => ExecutionState == ActionExecutionState.Running;
    public bool IsRestoring => ExecutionState == ActionExecutionState.Restoring;
    public bool HasExecutionError => ExecutionState == ActionExecutionState.Error;

    public string ExecutionStateText => ExecutionState switch
    {
        ActionExecutionState.Running => _localizationService.GetString("Execution.Status.Running"),
        ActionExecutionState.Restoring => _localizationService.GetString("Execution.Status.Restoring"),
        ActionExecutionState.Completed => _localizationService.GetString("Execution.Status.Success"),
        ActionExecutionState.Error => _localizationService.GetString("Execution.Status.Failed"),
        _ => _localizationService.GetString("Execution.Status.Pending")
    };

    public void SetExecutionState(ActionExecutionState state) => ExecutionState = state;

    public void ResetExecutionState() => ExecutionState = ActionExecutionState.Pending;

    public void ClearExecutionError()
    {
        if (HasExecutionError) ExecutionState = ActionExecutionState.Pending;
    }

    public ActionDefinition ToModel()
    {
        var parameters = Parameters.DeepClone().AsObject();
        switch (Type)
        {
            case ActionTypeIds.ProgramRun:
                SetString(parameters, ActionParameterNames.Target, Target);
                parameters[ActionParameterNames.TargetType] = TargetType;
                SetCommonLaunchParameters(parameters);
                parameters[ActionParameterNames.StartOnlyIfNotAlreadyRunning] = StartOnlyIfNotAlreadyRunning;
                parameters[ActionParameterNames.ChangeAffinity] = ChangeAffinity;
                parameters[ActionParameterNames.ChangePriority] = ShouldChangeProcessPriority;
                parameters[ActionParameterNames.CpuIndices] = new JsonArray(LogicalCpus.Where(cpu => cpu.IsSelected)
                    .Select(cpu => (JsonNode?)JsonValue.Create(cpu.Index)).ToArray());
                parameters[ActionParameterNames.ProcessPriority] = ProcessPriority;
                parameters[ActionParameterNames.ProcessMemoryPriority] = ProcessMemoryPriority;
                parameters[ActionParameterNames.ProcessPerformanceMode] = ProcessPerformanceMode;
                parameters[ActionParameterNames.WaitForProcessStart] = WaitForProcessStart;
                parameters[ActionParameterNames.ProcessStartWaitSeconds] = ProcessStartWaitSeconds;
                parameters[ActionParameterNames.ProcessTargetMode] = ProcessTargetMode;
                SetString(parameters, ActionParameterNames.ProcessName, ProcessName);
                SetString(parameters, ActionParameterNames.ExecutablePath, ExecutablePath);
                break;
            case ActionTypeIds.ProcessConfigure:
                SetString(parameters, ActionParameterNames.ProcessName, ProcessName);
                SetString(parameters, ActionParameterNames.ExecutablePath, ExecutablePath);
                parameters[ActionParameterNames.ProcessOperation] = ProcessOperation;
                parameters[ActionParameterNames.ChangeAffinity] = ChangeAffinity;
                parameters[ActionParameterNames.ChangePriority] = ShouldChangeProcessPriority;
                parameters[ActionParameterNames.CpuIndices] = new JsonArray(LogicalCpus.Where(cpu => cpu.IsSelected)
                    .Select(cpu => (JsonNode?)JsonValue.Create(cpu.Index)).ToArray());
                parameters[ActionParameterNames.ProcessPriority] = ProcessPriority;
                parameters[ActionParameterNames.ProcessMemoryPriority] = ProcessMemoryPriority;
                parameters[ActionParameterNames.ProcessPerformanceMode] = ProcessPerformanceMode;
                break;
            case ActionTypeIds.ServiceSetState:
                SetString(parameters, ActionParameterNames.ServiceName, ServiceName);
                SetString(parameters, ActionParameterNames.ServiceDisplayName, ServiceDisplayName);
                parameters[ActionParameterNames.DesiredState] = DesiredServiceState;
                parameters[ActionParameterNames.ServiceStartupType] = DesiredServiceStartupType;
                break;
            case ActionTypeIds.PowerSetPlan:
                SetString(parameters, ActionParameterNames.PowerPlanGuid, PowerPlanGuid);
                SetString(parameters, ActionParameterNames.PowerPlanName, PowerPlanName);
                break;
            case ActionTypeIds.ScriptRun:
                SetString(parameters, ActionParameterNames.ScriptPath, ScriptPath);
                SetCommonLaunchParameters(parameters);
                parameters[ActionParameterNames.ScriptType] = ScriptType;
                parameters[ActionParameterNames.WaitForExit] = WaitForScriptExit;
                parameters[ActionParameterNames.RunAsAdministrator] = RunAsAdministrator;
                SetString(parameters, ActionParameterNames.ProcessName, ProcessName);
                SetString(parameters, ActionParameterNames.ExecutablePath, ExecutablePath);
                parameters[ActionParameterNames.WaitForProcessStart] = WaitForProcessStart;
                parameters[ActionParameterNames.ProcessStartWaitSeconds] = ProcessStartWaitSeconds;
                SetString(parameters, ActionParameterNames.RestoreScriptPath, RestoreScriptPath);
                SetString(parameters, ActionParameterNames.RestoreScriptArguments, RestoreScriptArguments);
                SetString(parameters, ActionParameterNames.RestoreScriptWorkingDirectory, RestoreScriptWorkingDirectory);
                parameters[ActionParameterNames.RestoreScriptType] = RestoreScriptType;
                parameters[ActionParameterNames.RestoreScriptWaitForExit] = RestoreScriptWaitForExit;
                parameters[ActionParameterNames.RestoreScriptRunAsAdministrator] = RestoreScriptRunAsAdministrator;
                parameters[ActionParameterNames.RestoreScriptTimeoutSeconds] = RestoreScriptTimeoutSeconds;
                break;
            case ActionTypeIds.DisplayConfigure:
                SetString(parameters, ActionParameterNames.DisplayDeviceName, DisplayDeviceName);
                SetString(parameters, ActionParameterNames.DisplayDeviceId, DisplayDeviceId);
                SetString(parameters, ActionParameterNames.DisplayName, DisplayMonitorName);
                parameters[ActionParameterNames.DisplayWidth] = _displayWidth;
                parameters[ActionParameterNames.DisplayHeight] = _displayHeight;
                parameters[ActionParameterNames.DisplayRefreshRate] = DisplayRefreshRate;
                parameters[ActionParameterNames.SkipDisplayConfirmation] = SkipDisplayConfirmation;
                break;
            case ActionTypeIds.Delay:
                parameters[ActionParameterNames.DelaySeconds] = DelaySeconds;
                break;
            case ActionTypeIds.WaitProcessStart:
            case ActionTypeIds.WaitProcessExit:
                SetString(parameters, ActionParameterNames.ProcessName, ProcessName);
                SetString(parameters, ActionParameterNames.ExecutablePath, ExecutablePath);
                break;
            case ActionTypeIds.WaitWindow:
                SetString(parameters, ActionParameterNames.ProcessName, ProcessName);
                SetString(parameters, ActionParameterNames.ExecutablePath, ExecutablePath);
                parameters[ActionParameterNames.WindowMatchMode] = WindowMatchMode;
                SetString(parameters, ActionParameterNames.WindowTitle, WindowTitle);
                break;
            case ActionTypeIds.AudioConfigure:
                SetString(parameters, ActionParameterNames.AudioOutputDeviceId, AudioOutputDeviceId);
                SetString(parameters, "audioOutputDeviceName", AudioOutputDeviceName);
                SetString(parameters, ActionParameterNames.AudioInputDeviceId, AudioInputDeviceId);
                SetString(parameters, "audioInputDeviceName", AudioInputDeviceName);
                parameters[ActionParameterNames.SetDefaultMultimedia] = SetDefaultMultimedia;
                parameters[ActionParameterNames.SetDefaultCommunications] = SetDefaultCommunications;
                if (ChangeVolume) parameters[ActionParameterNames.VolumePercent] = VolumePercent;
                else parameters.Remove(ActionParameterNames.VolumePercent);
                if (ChangeMute) parameters[ActionParameterNames.Mute] = Mute;
                else parameters.Remove(ActionParameterNames.Mute);
                break;
            case ActionTypeIds.DeviceSetState:
                SetString(parameters, ActionParameterNames.DeviceInstanceId, DeviceInstanceId);
                SetString(parameters, ActionParameterNames.DeviceFriendlyName, DeviceFriendlyName);
                SetString(parameters, ActionParameterNames.DeviceClass, DeviceClass);
                parameters[ActionParameterNames.DesiredState] = DeviceState;
                break;
            case ActionTypeIds.ProfileRun:
                SetString(parameters, ActionParameterNames.ProfileId, TargetProfileId);
                SetString(parameters, "profileName", TargetProfileName);
                break;
            case ActionTypeIds.ConditionIf:
                parameters[ActionParameterNames.ConditionType] = ConditionType;
                SetString(parameters, ActionParameterNames.ConditionValue, ConditionValue);
                parameters[ActionParameterNames.ThenActions] = SerializeNested(ThenActions);
                parameters[ActionParameterNames.ElseActions] = SerializeNested(ElseActions);
                break;
            case ActionTypeIds.NotificationShow:
                SetString(parameters, ActionParameterNames.NotificationMessage, NotificationMessage);
                parameters[ActionParameterNames.NotificationLevel] = NotificationLevel;
                break;
            case ActionTypeIds.Comment:
                parameters.Clear();
                parameters[ActionParameterNames.CommentText] = CommentText;
                break;
        }

        if (Type == ActionTypeIds.ProgramRun)
        {
            parameters[ActionParameterNames.InstanceBehavior] = InstanceBehavior;
            parameters[ActionParameterNames.WindowBehavior] = WindowBehavior;
            parameters[ActionParameterNames.WindowWaitSeconds] = WindowWaitSeconds;
            parameters[ActionParameterNames.RunAsAdministrator] = RunAsAdministrator;
            parameters[ActionParameterNames.UseCustomWorkingDirectory] = UseCustomWorkingDirectory;
        }
        if (Type == ActionTypeIds.ProcessConfigure)
        {
            parameters[ActionParameterNames.WindowBehavior] = WindowBehavior;
            parameters[ActionParameterNames.WindowWaitSeconds] = WindowWaitSeconds;
        }
        if (Type == ActionTypeIds.ProcessConfigure)
        {
            parameters[ActionParameterNames.WindowBehavior] = WindowBehavior;
        }

        var isComment = Type == ActionTypeIds.Comment;
        return new ActionDefinition
        {
            Id = Id,
            Type = Type,
            ActionSchemaVersion = ActionSchemaVersion,
            SortOrder = SortOrder,
            Name = isComment ? null : string.IsNullOrWhiteSpace(Name) ? null : Name.Trim(),
            IsEnabled = IsEnabled,
            FailurePolicy = !isComment && string.Equals(FailurePolicyId, "stop", StringComparison.OrdinalIgnoreCase)
                ? ActionFailurePolicy.Stop
                : ActionFailurePolicy.Continue,
            RestoreBehavior = isComment ? ActionRestoreBehavior.DoNotRestore : RestoreBehaviorId switch
            {
                "previous" => ActionRestoreBehavior.RestorePreviousState,
                "closeStarted" => ActionRestoreBehavior.CloseIfStartedBySwitchBoard,
                "restart" => ActionRestoreBehavior.RestartIfWasRunning,
                "restoreScript" => ActionRestoreBehavior.RunRestoreScript,
                _ => ActionRestoreBehavior.DoNotRestore
            },
            Timeout = isComment || TimeoutSeconds <= 0 ? null : TimeSpan.FromSeconds(TimeoutSeconds),
            RetryOnFailure = !isComment && RetryOnFailure,
            MaximumAttempts = !isComment && RetryOnFailure ? MaximumAttempts : 1,
            RetryDelay = isComment ? TimeSpan.Zero : TimeSpan.FromSeconds(RetryDelaySeconds),
            Parameters = parameters
        };
    }

    private void SetCommonLaunchParameters(JsonObject parameters)
    {
        SetString(parameters, ActionParameterNames.Arguments, Arguments);
        SetString(parameters, ActionParameterNames.WorkingDirectory, WorkingDirectory);
    }

    private void SetWithSummary(ref string field, string value, [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (SetProperty(ref field, value, propertyName))
        {
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(SupportsRestore));
            NotifyValidation();
        }
    }

    private void SetValidationProperty(ref string field, string value,
        [System.Runtime.CompilerServices.CallerMemberName] string? propertyName = null)
    {
        if (SetProperty(ref field, value, propertyName)) NotifyValidation();
    }

    private void NotifyValidation()
    {
        OnPropertyChanged(nameof(IsValid));
        OnPropertyChanged(nameof(ValidationMessage));
        OnPropertyChanged(nameof(ValidationLevel));
        OnPropertyChanged(nameof(ShouldMonitorCurrentStatus));
        OnPropertyChanged(nameof(ShouldShowCurrentStatus));
        if (!ShouldMonitorCurrentStatus && !string.IsNullOrWhiteSpace(CurrentStatusText))
            ClearCurrentStatus();
    }

    private string ReadString(string propertyName)
    {
        try { return Parameters[propertyName]?.GetValue<string>() ?? string.Empty; }
        catch (InvalidOperationException) { return string.Empty; }
    }

    private bool ReadBoolean(string propertyName, bool defaultValue)
    {
        try { return Parameters[propertyName]?.GetValue<bool>() ?? defaultValue; }
        catch (InvalidOperationException) { return defaultValue; }
    }

    private int ReadInt32(string propertyName, int defaultValue)
    {
        try { return Parameters[propertyName]?.GetValue<int>() ?? defaultValue; }
        catch (InvalidOperationException) { return defaultValue; }
    }

    private IReadOnlyList<int> ReadIntArray(string propertyName)
    {
        if (Parameters[propertyName] is not JsonArray array) return [];
        var result = new List<int>();
        foreach (var node in array)
        {
            try { if (node is not null) result.Add(node.GetValue<int>()); }
            catch (InvalidOperationException) { }
        }
        return result;
    }

    private void LoadNestedActions(string propertyName, ObservableCollection<ActionItemViewModel> target, int depth)
    {
        if (Parameters[propertyName] is not JsonArray array) return;
        foreach (var node in array)
        {
            try
            {
                if (ActionDefinitionJson.Deserialize(node) is { } action)
                {
                    var item = new ActionItemViewModel(action, _localizationService, depth);
                    SubscribeNested(item);
                    target.Add(item);
                }
            }
            catch (JsonException) { }
        }
    }

    private static JsonArray SerializeNested(IEnumerable<ActionItemViewModel> actions) =>
        new(actions.Select((action, index) =>
        {
            var model = action.ToModel();
            model.SortOrder = index;
            return ActionDefinitionJson.Serialize(model);
        }).ToArray());

    private static void SetString(JsonObject parameters, string propertyName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) parameters.Remove(propertyName);
        else parameters[propertyName] = value.Trim();
    }

    private static string DefaultIfEmpty(string value, string defaultValue) =>
        string.IsNullOrWhiteSpace(value) ? defaultValue : value;

    private static bool IsFullExecutablePath(string? value) => !string.IsNullOrWhiteSpace(value) &&
        Path.IsPathRooted(value) && string.Equals(Path.GetExtension(value), ".exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsProtocolTarget(string? value) => !string.IsNullOrWhiteSpace(value) &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && !uri.IsFile && !string.IsNullOrWhiteSpace(uri.Scheme);

    private bool IsRestoreBehaviorAvailable(string value) =>
        AvailableRestoreBehaviors.Any(option => string.Equals(option.Value, value, StringComparison.OrdinalIgnoreCase));



    private static IReadOnlyList<LocalizedValueOptionViewModel> BuildOptions(ILocalizationService localization,
        params (string Value, string ResourceKey)[] values) =>
        values.Select(value => new LocalizedValueOptionViewModel(value.Value, value.ResourceKey, localization)).ToList();
}
