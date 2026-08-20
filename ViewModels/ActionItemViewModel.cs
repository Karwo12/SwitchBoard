using System.IO;
using System.Text.Json.Nodes;
using System.Collections.ObjectModel;
using SwitchBoard.Localization;
using SwitchBoard.Models;
using SwitchBoard.Models.Actions;
using SwitchBoard.Services.Discovery;
using System.Text.Json;
using SwitchBoard.Services.Execution;

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
    private string _arguments;
    private string _workingDirectory;
    private bool _useCustomWorkingDirectory;
    private bool _startOnlyIfNotAlreadyRunning;
    private string _processName;
    private string _executablePath;
    private int? _runtimeProcessIdHint;
    private string _desiredProcessState;
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
    private string _currentStatusText = string.Empty;
    private string _currentStatusTooltip = string.Empty;
    private DateTimeOffset? _lastChecked;

    public ActionItemViewModel(ActionDefinition action, ILocalizationService localizationService, int nestingDepth = 0)
    {
        _localizationService = localizationService;
        _displayNameResourceKey = GetDisplayNameResourceKey(action.Type);
        Id = action.Id;
        Type = action.Type;
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
        _target = ReadString(ActionParameterNames.Target);
        _arguments = ReadString(ActionParameterNames.Arguments);
        _workingDirectory = ReadString(ActionParameterNames.WorkingDirectory);
        // Older actions had no explicit switch; a stored directory means that it was intentional.
        _useCustomWorkingDirectory = ReadBoolean(ActionParameterNames.UseCustomWorkingDirectory,
            !string.IsNullOrWhiteSpace(_workingDirectory));
        _startOnlyIfNotAlreadyRunning = ReadBoolean(ActionParameterNames.StartOnlyIfNotAlreadyRunning, true);
        _processName = ReadString(ActionParameterNames.ProcessName);
        _executablePath = ReadString(ActionParameterNames.ExecutablePath);
        _runtimeProcessIdHint = action.RuntimeProcessIdHint;
        _desiredProcessState = DefaultIfEmpty(ReadString(ActionParameterNames.DesiredState), ProcessDesiredStateIds.Stopped);
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
        _retryOnFailure = action.RetryOnFailure;
        _maximumAttempts = Math.Clamp(action.MaximumAttempts, 1, 10);
        _retryDelaySeconds = Math.Clamp((int)Math.Round(action.RetryDelay.TotalSeconds), 0, 3600);
        _instanceBehavior = DefaultIfEmpty(ReadString(ActionParameterNames.InstanceBehavior),
            _startOnlyIfNotAlreadyRunning ? InstanceBehaviorIds.DoNotStartAgain : InstanceBehaviorIds.StartAnother);
        _windowBehavior = DefaultIfEmpty(ReadString(ActionParameterNames.WindowBehavior), WindowBehaviorIds.None);
        _windowWaitSeconds = Math.Clamp(ReadInt32(ActionParameterNames.WindowWaitSeconds, 10), 1, 300);
        _changeAffinity = ReadBoolean(ActionParameterNames.ChangeAffinity, false);
        _changePriority = ReadBoolean(ActionParameterNames.ChangePriority, false);
        _processPriority = DefaultIfEmpty(ReadString(ActionParameterNames.ProcessPriority), ProcessPriorityIds.Normal);
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
        AvailableRestoreBehaviors = BuildRestoreBehaviors(action.Type, localizationService);
        AvailableProcessPriorities = BuildOptions(localizationService,
            (ProcessPriorityIds.Idle, "Priority.Idle"), (ProcessPriorityIds.BelowNormal, "Priority.BelowNormal"),
            (ProcessPriorityIds.Normal, "Priority.Normal"), (ProcessPriorityIds.AboveNormal, "Priority.AboveNormal"),
            (ProcessPriorityIds.High, "Priority.High"));
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
        AddThenNotificationCommand = new RelayCommand(() => AddNested(ThenActions, ActionTypeIds.NotificationShow));
        AddThenProgramCommand = new RelayCommand(() => AddNested(ThenActions, ActionTypeIds.ProgramRun));
        AddElseNotificationCommand = new RelayCommand(() => AddNested(ElseActions, ActionTypeIds.NotificationShow));
        AddElseProgramCommand = new RelayCommand(() => AddNested(ElseActions, ActionTypeIds.ProgramRun));
        DeleteNestedActionCommand = new RelayCommand<ActionItemViewModel>(DeleteNestedAction, item => item is not null);
    }

    public Guid Id { get; }
    public string Type { get; }
    public int ActionSchemaVersion { get; }
    public int SortOrder { get; set; }
    public JsonObject Parameters { get; }
    public IReadOnlyList<LocalizedValueOptionViewModel> AvailableProcessStates { get; }
    public IReadOnlyList<LocalizedValueOptionViewModel> AvailableServiceStates { get; }
    public IReadOnlyList<LocalizedValueOptionViewModel> AvailableServiceStartupTypes { get; }
    public IReadOnlyList<LocalizedValueOptionViewModel> AvailableScriptTypes { get; }
    public IReadOnlyList<LocalizedValueOptionViewModel> AvailableFailurePolicies { get; }
    public IReadOnlyList<LocalizedValueOptionViewModel> AvailableRestoreBehaviors { get; }
    public IReadOnlyList<LocalizedValueOptionViewModel> AvailableProcessPriorities { get; }
    public IReadOnlyList<LocalizedValueOptionViewModel> AvailableWindowMatchModes { get; }
    public IReadOnlyList<LocalizedValueOptionViewModel> AvailableWindowBehaviors { get; }
    public IReadOnlyList<LocalizedValueOptionViewModel> AvailableInstanceBehaviors { get; }
    public IReadOnlyList<LocalizedValueOptionViewModel> AvailableDeviceStates { get; }
    public IReadOnlyList<LocalizedValueOptionViewModel> AvailableConditions { get; }
    public IReadOnlyList<LocalizedValueOptionViewModel> AvailableNotificationLevels { get; }
    public ObservableCollection<LogicalCpuOptionViewModel> LogicalCpus { get; }
    public ObservableCollection<ActionItemViewModel> ThenActions { get; }
    public ObservableCollection<ActionItemViewModel> ElseActions { get; }
    public RelayCommand AddThenNotificationCommand { get; }
    public RelayCommand AddThenProgramCommand { get; }
    public RelayCommand AddElseNotificationCommand { get; }
    public RelayCommand AddElseProgramCommand { get; }
    public RelayCommand<ActionItemViewModel> DeleteNestedActionCommand { get; }

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

    public string DisplayName => !string.IsNullOrWhiteSpace(Name)
        ? Name.Trim()
        : _displayNameResourceKey is not null
            ? _localizationService.GetString(_displayNameResourceKey)
            : Type;

    public string Summary => Type switch
    {
        ActionTypeIds.ProgramRun => GetProgramSummary(),
        ActionTypeIds.ProcessSetState => _localizationService.Format(
            string.Equals(DesiredProcessState, ProcessDesiredStateIds.Unchanged, StringComparison.OrdinalIgnoreCase)
                ? "ActionSummary.ProcessUnchanged"
                : "ActionSummary.StopProcess",
            GetProcessSummaryTarget()),
        ActionTypeIds.ServiceSetState => _localizationService.Format("ActionSummary.Service", GetServiceSummaryTarget()),
        ActionTypeIds.PowerSetPlan => _localizationService.Format("ActionSummary.PowerPlan", GetPowerPlanSummaryTarget()),
        ActionTypeIds.ScriptRun => _localizationService.Format("ActionSummary.Script", GetFileSummary(ScriptPath)),
        ActionTypeIds.DisplayConfigure => _localizationService.Format("ActionSummary.Display", GetDisplaySummaryTarget()),
        ActionTypeIds.Delay => _localizationService.Format("ActionSummary.Delay", DelaySeconds),
        ActionTypeIds.ProcessConfigure or ActionTypeIds.WaitProcessStart or ActionTypeIds.WaitProcessExit or ActionTypeIds.WaitWindow
            => GetProcessSummaryTarget(),
        ActionTypeIds.AudioConfigure => string.IsNullOrWhiteSpace(AudioOutputDeviceName) ? AudioInputDeviceName : AudioOutputDeviceName,
        ActionTypeIds.DeviceSetState => string.IsNullOrWhiteSpace(DeviceFriendlyName) ? DeviceInstanceId : DeviceFriendlyName,
        ActionTypeIds.ProfileRun => string.IsNullOrWhiteSpace(TargetProfileName) ? TargetProfileId : TargetProfileName,
        ActionTypeIds.ConditionIf => ConditionValue,
        ActionTypeIds.NotificationShow => NotificationMessage,
        _ => DisplayName
    };

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
            OnPropertyChanged(nameof(Summary));
            NotifyValidation();
        }
    }
    public bool SupportsRestore => Type switch
    {
        ActionTypeIds.ProgramRun => IsFullExecutablePath(Target),
        ActionTypeIds.ProcessSetState => IsFullExecutablePath(ExecutablePath),
        ActionTypeIds.ServiceSetState or ActionTypeIds.PowerSetPlan or ActionTypeIds.DisplayConfigure or ActionTypeIds.ScriptRun or
            ActionTypeIds.ProcessConfigure or ActionTypeIds.AudioConfigure or ActionTypeIds.DeviceSetState => true,
        _ => false
    };
    public bool IsRestoreScriptEnabled => Type == ActionTypeIds.ScriptRun && RestoreBehaviorId == "restoreScript";
    public string Arguments { get => _arguments; set => SetProperty(ref _arguments, value); }
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
    public bool IsWindowWaitEnabled => !string.Equals(WindowBehavior, WindowBehaviorIds.None, StringComparison.OrdinalIgnoreCase);
    public int WindowWaitSeconds { get => _windowWaitSeconds; set => SetProperty(ref _windowWaitSeconds, Math.Clamp(value, 1, 300)); }
    public bool ChangeAffinity { get => _changeAffinity; set { if (SetProperty(ref _changeAffinity, value)) NotifyValidation(); } }
    public bool ChangePriority { get => _changePriority; set { if (SetProperty(ref _changePriority, value)) NotifyValidation(); } }
    public string ProcessPriority { get => _processPriority; set => SetProperty(ref _processPriority, value); }
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

    private void AddNested(ObservableCollection<ActionItemViewModel> target, string type)
    {
        var parameters = type == ActionTypeIds.NotificationShow
            ? new JsonObject { [ActionParameterNames.NotificationLevel] = NotificationLevelIds.Info }
            : new JsonObject
            {
                [ActionParameterNames.StartOnlyIfNotAlreadyRunning] = true,
                [ActionParameterNames.InstanceBehavior] = InstanceBehaviorIds.DoNotStartAgain
            };
        var item = new ActionItemViewModel(new ActionDefinition
        {
            Type = type, SortOrder = target.Count, Parameters = parameters
        }, _localizationService, 1);
        SubscribeNested(item);
        target.Add(item);
        NotifyValidation();
        OnPropertyChanged("NestedConfiguration");
    }

    private void DeleteNestedAction(ActionItemViewModel? item)
    {
        if (item is null) return;
        ThenActions.Remove(item);
        ElseActions.Remove(item);
        NotifyValidation();
        OnPropertyChanged("NestedConfiguration");
    }

    private void SubscribeNested(ActionItemViewModel item) => item.PropertyChanged += (_, _) =>
    {
        NotifyValidation();
        OnPropertyChanged("NestedConfiguration");
    };

    public string Target { get => _target; set => SetWithSummary(ref _target, value); }
    public string ProcessName { get => _processName; set => SetWithSummary(ref _processName, value); }
    public string ExecutablePath { get => _executablePath; set => SetWithSummary(ref _executablePath, value); }
    public int? RuntimeProcessIdHint { get => _runtimeProcessIdHint; set => SetProperty(ref _runtimeProcessIdHint, value); }
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
            if (SetProperty(ref _displayRefreshRate, value)) { OnPropertyChanged(nameof(Summary)); NotifyValidation(); }
        }
    }

    public bool IsValid => !IsEnabled || ValidationLevel != ValidationSeverity.Error;
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
        CurrentStatusText = string.IsNullOrWhiteSpace(status)
            ? _localizationService.GetString("ActionStatus.Unavailable")
            : status;
        _lastChecked = checkedAt;
        OnPropertyChanged(nameof(LastChecked));
        CurrentStatusTooltip = string.IsNullOrWhiteSpace(technicalDetails)
            ? _localizationService.Format("ActionStatus.LastChecked", checkedAt.ToLocalTime().ToString("HH:mm:ss"))
            : technicalDetails + Environment.NewLine +
              _localizationService.Format("ActionStatus.LastChecked", checkedAt.ToLocalTime().ToString("HH:mm:ss"));
    }
    public string ValidationMessage => Type switch
    {
        ActionTypeIds.ProgramRun when string.IsNullOrWhiteSpace(Target) => _localizationService.GetString("Validation.ProgramTarget"),
        ActionTypeIds.ProcessSetState when string.IsNullOrWhiteSpace(ProcessName) => _localizationService.GetString("Validation.ProcessName"),
        ActionTypeIds.ProcessConfigure when string.IsNullOrWhiteSpace(ProcessName) => _localizationService.GetString("Validation.ProcessName"),
        ActionTypeIds.ProcessConfigure when !ChangeAffinity && !ChangePriority => _localizationService.GetString("Validation.ProcessSetting"),
        ActionTypeIds.ProcessConfigure when ChangeAffinity && !LogicalCpus.Any(cpu => cpu.IsSelected) => _localizationService.GetString("Validation.CpuAffinity"),
        ActionTypeIds.WaitProcessStart or ActionTypeIds.WaitProcessExit when string.IsNullOrWhiteSpace(ProcessName) => _localizationService.GetString("Validation.ProcessName"),
        ActionTypeIds.WaitWindow when string.IsNullOrWhiteSpace(ProcessName) => _localizationService.GetString("Validation.ProcessName"),
        ActionTypeIds.WaitWindow when WindowMatchMode is WindowMatchModeIds.Contains or WindowMatchModeIds.Exact && string.IsNullOrWhiteSpace(WindowTitle)
            => _localizationService.GetString("Validation.WindowTitle"),
        ActionTypeIds.AudioConfigure when string.IsNullOrWhiteSpace(AudioOutputDeviceId) && string.IsNullOrWhiteSpace(AudioInputDeviceId) && !ChangeVolume && !ChangeMute
            => _localizationService.GetString("Validation.Audio"),
        ActionTypeIds.DeviceSetState when string.IsNullOrWhiteSpace(DeviceInstanceId) => _localizationService.GetString("Validation.Device"),
        ActionTypeIds.ProfileRun when !Guid.TryParse(TargetProfileId, out _) => _localizationService.GetString("Validation.Profile"),
        ActionTypeIds.ConditionIf when string.IsNullOrWhiteSpace(ConditionType) || string.IsNullOrWhiteSpace(ConditionValue)
            => _localizationService.GetString("Validation.Condition"),
        ActionTypeIds.ConditionIf when ThenActions.Concat(ElseActions).Any(action => !action.IsValid)
            => _localizationService.GetString("Validation.NestedAction"),
        ActionTypeIds.NotificationShow when string.IsNullOrWhiteSpace(NotificationMessage) => _localizationService.GetString("Validation.Notification"),
        ActionTypeIds.ServiceSetState when string.IsNullOrWhiteSpace(ServiceName) => _localizationService.GetString("Validation.ServiceName"),
        ActionTypeIds.PowerSetPlan when !Guid.TryParse(PowerPlanGuid, out _) => _localizationService.GetString("Validation.PowerPlan"),
        ActionTypeIds.DisplayConfigure when string.IsNullOrWhiteSpace(DisplayDeviceName) || _displayWidth <= 0 || _displayHeight <= 0 || DisplayRefreshRate <= 0
            => _localizationService.GetString("Validation.Display"),
        ActionTypeIds.ScriptRun when string.IsNullOrWhiteSpace(ScriptPath) => _localizationService.GetString("Validation.ScriptPath"),
        ActionTypeIds.ProgramRun when RestoreBehaviorId == "closeStarted" && !IsFullExecutablePath(Target)
            => _localizationService.GetString("Validation.ProgramRestorePath"),
        ActionTypeIds.ProcessSetState when RestoreBehaviorId == "restart" && !IsFullExecutablePath(ExecutablePath)
            => _localizationService.GetString("Validation.ProcessRestorePath"),
        ActionTypeIds.ScriptRun when IsRestoreScriptEnabled && string.IsNullOrWhiteSpace(RestoreScriptPath)
            => _localizationService.GetString("Validation.RestoreScriptPath"),
        ActionTypeIds.ProcessSetState when string.Equals(DesiredProcessState, ProcessDesiredStateIds.Unchanged, StringComparison.OrdinalIgnoreCase)
            => _localizationService.GetString("Validation.NoOp"),
        ActionTypeIds.ServiceSetState when string.Equals(DesiredServiceState, ServiceDesiredStateIds.Unchanged, StringComparison.OrdinalIgnoreCase)
            => _localizationService.GetString("Validation.NoOp"),
        ActionTypeIds.DeviceSetState when string.Equals(DeviceState, DeviceStateIds.Unchanged, StringComparison.OrdinalIgnoreCase)
            => _localizationService.GetString("Validation.NoOp"),
        _ => string.Empty
    };

    public ValidationSeverity ValidationLevel => Type switch
    {
        ActionTypeIds.ProcessSetState when string.Equals(DesiredProcessState, ProcessDesiredStateIds.Unchanged, StringComparison.OrdinalIgnoreCase)
            => ValidationSeverity.Warning,
        ActionTypeIds.ServiceSetState when string.Equals(DesiredServiceState, ServiceDesiredStateIds.Unchanged, StringComparison.OrdinalIgnoreCase)
            => ValidationSeverity.Warning,
        ActionTypeIds.DeviceSetState when string.Equals(DeviceState, DeviceStateIds.Unchanged, StringComparison.OrdinalIgnoreCase)
            => ValidationSeverity.Warning,
        _ when ValidationMessage.Length > 0 => ValidationSeverity.Error,
        _ => ValidationSeverity.Valid
    };

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
        NotifyValidation();
        foreach (var option in AvailableProcessStates.Concat(AvailableServiceStates).Concat(AvailableServiceStartupTypes)
                     .Concat(AvailableScriptTypes).Concat(AvailableFailurePolicies).Concat(AvailableRestoreBehaviors)
                     .Concat(AvailableProcessPriorities).Concat(AvailableWindowMatchModes).Concat(AvailableWindowBehaviors)
                     .Concat(AvailableInstanceBehaviors).Concat(AvailableDeviceStates).Concat(AvailableConditions)
                     .Concat(AvailableNotificationLevels))
        {
            option.RefreshDisplayName();
        }
    }

    public void TrySetSuggestedName(string? suggestedName)
    {
        if (string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(suggestedName))
        {
            Name = suggestedName.Trim();
        }
    }

    public void ApplyDisplayCandidate(DisplayCandidate candidate, bool notifyChanges = true)
    {
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
        }
    }

    public ActionDefinition ToModel()
    {
        var parameters = Parameters.DeepClone().AsObject();
        switch (Type)
        {
            case ActionTypeIds.ProgramRun:
                SetString(parameters, ActionParameterNames.Target, Target);
                SetCommonLaunchParameters(parameters);
                parameters[ActionParameterNames.StartOnlyIfNotAlreadyRunning] = StartOnlyIfNotAlreadyRunning;
                break;
            case ActionTypeIds.ProcessSetState:
                SetString(parameters, ActionParameterNames.ProcessName, ProcessName);
                SetString(parameters, ActionParameterNames.ExecutablePath, ExecutablePath);
                parameters[ActionParameterNames.DesiredState] = DesiredProcessState;
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
                break;
            case ActionTypeIds.Delay:
                parameters[ActionParameterNames.DelaySeconds] = DelaySeconds;
                break;
            case ActionTypeIds.ProcessConfigure:
                SetString(parameters, ActionParameterNames.ProcessName, ProcessName);
                SetString(parameters, ActionParameterNames.ExecutablePath, ExecutablePath);
                parameters[ActionParameterNames.ChangeAffinity] = ChangeAffinity;
                parameters[ActionParameterNames.ChangePriority] = ChangePriority;
                parameters[ActionParameterNames.CpuIndices] = new JsonArray(LogicalCpus.Where(cpu => cpu.IsSelected)
                    .Select(cpu => (JsonNode?)JsonValue.Create(cpu.Index)).ToArray());
                parameters[ActionParameterNames.ProcessPriority] = ProcessPriority;
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
        }

        if (Type == ActionTypeIds.ProgramRun)
        {
            parameters[ActionParameterNames.InstanceBehavior] = InstanceBehavior;
            parameters[ActionParameterNames.WindowBehavior] = WindowBehavior;
            parameters[ActionParameterNames.WindowWaitSeconds] = WindowWaitSeconds;
            parameters[ActionParameterNames.RunAsAdministrator] = RunAsAdministrator;
            parameters[ActionParameterNames.UseCustomWorkingDirectory] = UseCustomWorkingDirectory;
        }

        return new ActionDefinition
        {
            Id = Id,
            Type = Type,
            ActionSchemaVersion = ActionSchemaVersion,
            SortOrder = SortOrder,
            Name = string.IsNullOrWhiteSpace(Name) ? null : Name.Trim(),
            IsEnabled = IsEnabled,
            FailurePolicy = string.Equals(FailurePolicyId, "stop", StringComparison.OrdinalIgnoreCase)
                ? ActionFailurePolicy.Stop
                : ActionFailurePolicy.Continue,
            RestoreBehavior = RestoreBehaviorId switch
            {
                "previous" => ActionRestoreBehavior.RestorePreviousState,
                "closeStarted" => ActionRestoreBehavior.CloseIfStartedBySwitchBoard,
                "restart" => ActionRestoreBehavior.RestartIfWasRunning,
                "restoreScript" => ActionRestoreBehavior.RunRestoreScript,
                _ => ActionRestoreBehavior.DoNotRestore
            },
            Timeout = TimeoutSeconds > 0 ? TimeSpan.FromSeconds(TimeoutSeconds) : null,
            RetryOnFailure = RetryOnFailure,
            MaximumAttempts = RetryOnFailure ? MaximumAttempts : 1,
            RetryDelay = TimeSpan.FromSeconds(RetryDelaySeconds),
            RuntimeProcessIdHint = RuntimeProcessIdHint,
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
                if (node?.Deserialize<ActionDefinition>() is { } action)
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
        new(actions.Select(action => JsonSerializer.SerializeToNode(action.ToModel())).ToArray());

    private static void SetString(JsonObject parameters, string propertyName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) parameters.Remove(propertyName);
        else parameters[propertyName] = value.Trim();
    }

    private string GetFileSummary(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return _localizationService.GetString("ActionSummary.NotConfigured");
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !uri.IsFile) return value;
        return Path.GetFileName(value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    private string GetProgramSummary()
    {
        var summary = _localizationService.Format("ActionSummary.RunProgram", GetFileSummary(Target));
        var options = new List<string>();

        if (InstanceBehavior == InstanceBehaviorIds.StartAnother)
            options.Add(_localizationService.GetString("ActionSummary.ProgramStartsAnother"));
        else if (InstanceBehavior == InstanceBehaviorIds.DoNotStartAgain)
            options.Add(_localizationService.GetString("ActionSummary.ProgramDoesNotDuplicate"));
        if (!string.Equals(WindowBehavior, WindowBehaviorIds.None, StringComparison.OrdinalIgnoreCase))
            options.Add(_localizationService.Format("ActionSummary.ProgramWindow",
                AvailableWindowBehaviors.FirstOrDefault(option => option.Value == WindowBehavior)?.DisplayName ?? WindowBehavior));
        if (RetryOnFailure)
            options.Add(_localizationService.GetString("ActionSummary.ProgramRetry"));
        if (UseCustomWorkingDirectory)
            options.Add(_localizationService.GetString("ActionSummary.ProgramCustomDirectory"));
        if (!string.Equals(RestoreBehaviorId, "none", StringComparison.OrdinalIgnoreCase))
            options.Add(_localizationService.Format("ActionSummary.ProgramRestore",
                AvailableRestoreBehaviors.FirstOrDefault(option => option.Value == RestoreBehaviorId)?.DisplayName ?? RestoreBehaviorId));

        return options.Count == 0 ? summary : $"{summary} · {string.Join(" · ", options)}";
    }

    private string GetProcessSummaryTarget() => !string.IsNullOrWhiteSpace(ExecutablePath)
        ? Path.GetFileName(ExecutablePath)
        : string.IsNullOrWhiteSpace(ProcessName)
            ? _localizationService.GetString("ActionSummary.NotConfigured")
            : $"{Path.GetFileNameWithoutExtension(ProcessName)}.exe";

    private string GetServiceSummaryTarget() => !string.IsNullOrWhiteSpace(ServiceDisplayName)
        ? ServiceDisplayName
        : string.IsNullOrWhiteSpace(ServiceName) ? _localizationService.GetString("ActionSummary.NotConfigured") : ServiceName;

    private string GetPowerPlanSummaryTarget() => !string.IsNullOrWhiteSpace(PowerPlanName)
        ? PowerPlanName
        : string.IsNullOrWhiteSpace(PowerPlanGuid) ? _localizationService.GetString("ActionSummary.NotConfigured") : PowerPlanGuid;

    private string GetDisplaySummaryTarget() => string.IsNullOrWhiteSpace(DisplayMonitorName) || _displayWidth <= 0
        ? _localizationService.GetString("ActionSummary.NotConfigured")
        : $"{DisplayMonitorName} • {_displayWidth} × {_displayHeight} @ {DisplayRefreshRate} Hz";

    private static string DefaultIfEmpty(string value, string defaultValue) =>
        string.IsNullOrWhiteSpace(value) ? defaultValue : value;

    private static bool IsFullExecutablePath(string? value) => !string.IsNullOrWhiteSpace(value) &&
        Path.IsPathRooted(value) && string.Equals(Path.GetExtension(value), ".exe", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<LocalizedValueOptionViewModel> BuildRestoreBehaviors(
        string actionType, ILocalizationService localization) => actionType switch
    {
        ActionTypeIds.ProgramRun =>
        [new("none", "RestoreBehavior.None", localization), new("closeStarted", "RestoreBehavior.CloseStarted", localization)],
        ActionTypeIds.ProcessSetState =>
        [new("none", "RestoreBehavior.None", localization), new("restart", "RestoreBehavior.RestartProcess", localization)],
        ActionTypeIds.PowerSetPlan =>
        [new("none", "RestoreBehavior.None", localization), new("previous", "RestoreBehavior.PreviousPlan", localization)],
        ActionTypeIds.DisplayConfigure =>
        [new("none", "RestoreBehavior.None", localization), new("previous", "RestoreBehavior.PreviousDisplay", localization)],
        ActionTypeIds.ScriptRun =>
        [new("none", "RestoreBehavior.None", localization), new("restoreScript", "RestoreBehavior.RestoreScript", localization)],
        ActionTypeIds.ProcessConfigure =>
        [new("none", "RestoreBehavior.None", localization), new("previous", "RestoreBehavior.ProcessSettings", localization)],
        ActionTypeIds.AudioConfigure =>
        [new("none", "RestoreBehavior.None", localization), new("previous", "RestoreBehavior.AudioSettings", localization)],
        ActionTypeIds.DeviceSetState =>
        [new("none", "RestoreBehavior.None", localization), new("previous", "RestoreBehavior.DeviceState", localization)],
        _ => [new("none", "RestoreBehavior.None", localization), new("previous", "RestoreBehavior.Previous", localization)]
    };

    private static string? GetDisplayNameResourceKey(string actionType) => actionType switch
    {
        ActionTypeIds.ProcessSetState => "Action.ProcessState",
        ActionTypeIds.ProgramRun => "Action.RunProgram",
        ActionTypeIds.ServiceSetState => "Action.WindowsServiceState",
        ActionTypeIds.DisplayConfigure => "Action.DisplaySettings",
        ActionTypeIds.PowerSetPlan => "Action.PowerPlan",
        ActionTypeIds.ScriptRun => "Action.RunScript",
        ActionTypeIds.Delay => "Action.Delay",
        ActionTypeIds.ProcessConfigure => "Action.ProcessSettings",
        ActionTypeIds.WaitProcessStart => "Action.WaitProcess",
        ActionTypeIds.WaitProcessExit => "Action.WaitProcessExit",
        ActionTypeIds.WaitWindow => "Action.WaitWindow",
        ActionTypeIds.AudioConfigure => "Action.AudioSettings",
        ActionTypeIds.DeviceSetState => "Action.DeviceState",
        ActionTypeIds.ProfileRun => "Action.RunProfile",
        ActionTypeIds.ConditionIf => "Action.If",
        ActionTypeIds.NotificationShow => "Action.Notification",
        _ => null
    };

    private static IReadOnlyList<LocalizedValueOptionViewModel> BuildOptions(ILocalizationService localization,
        params (string Value, string ResourceKey)[] values) =>
        values.Select(value => new LocalizedValueOptionViewModel(value.Value, value.ResourceKey, localization)).ToList();
}
