using System.IO;
using System.Text.Json.Nodes;
using System.Collections.ObjectModel;
using SwitchBoard.Localization;
using SwitchBoard.Models.Actions;
using SwitchBoard.Services.Discovery;

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
    private bool _startOnlyIfNotAlreadyRunning;
    private string _processName;
    private string _executablePath;
    private int? _runtimeProcessIdHint;
    private string _desiredProcessState;
    private string _serviceName;
    private string _serviceDisplayName;
    private string _desiredServiceState;
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

    public ActionItemViewModel(ActionDefinition action, ILocalizationService localizationService)
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
        _startOnlyIfNotAlreadyRunning = ReadBoolean(ActionParameterNames.StartOnlyIfNotAlreadyRunning, true);
        _processName = ReadString(ActionParameterNames.ProcessName);
        _executablePath = ReadString(ActionParameterNames.ExecutablePath);
        _runtimeProcessIdHint = action.RuntimeProcessIdHint;
        _desiredProcessState = DefaultIfEmpty(ReadString(ActionParameterNames.DesiredState), ProcessDesiredStateIds.Stopped);
        _serviceName = ReadString(ActionParameterNames.ServiceName);
        _serviceDisplayName = ReadString(ActionParameterNames.ServiceDisplayName);
        _desiredServiceState = DefaultIfEmpty(ReadString(ActionParameterNames.DesiredState), ServiceDesiredStateIds.Unchanged);
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
            new(ServiceDesiredStateIds.Running, "ServiceState.Running", localizationService),
            new(ServiceDesiredStateIds.Stopped, "ServiceState.Stopped", localizationService),
            new(ServiceDesiredStateIds.Unchanged, "ServiceState.Unchanged", localizationService)
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
    }

    public Guid Id { get; }
    public string Type { get; }
    public int ActionSchemaVersion { get; }
    public int SortOrder { get; set; }
    public JsonObject Parameters { get; }
    public IReadOnlyList<LocalizedValueOptionViewModel> AvailableProcessStates { get; }
    public IReadOnlyList<LocalizedValueOptionViewModel> AvailableServiceStates { get; }
    public IReadOnlyList<LocalizedValueOptionViewModel> AvailableScriptTypes { get; }
    public IReadOnlyList<LocalizedValueOptionViewModel> AvailableFailurePolicies { get; }
    public IReadOnlyList<LocalizedValueOptionViewModel> AvailableRestoreBehaviors { get; }

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
        ActionTypeIds.ProgramRun => _localizationService.Format("ActionSummary.RunProgram", GetFileSummary(Target)),
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
        _ => DisplayName
    };

    public bool IsExpanded { get => _isExpanded; set => SetProperty(ref _isExpanded, value); }
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
            NotifyValidation();
        }
    }
    public bool SupportsRestore => Type switch
    {
        ActionTypeIds.ProgramRun => IsFullExecutablePath(Target),
        ActionTypeIds.ProcessSetState => IsFullExecutablePath(ExecutablePath),
        ActionTypeIds.ServiceSetState or ActionTypeIds.PowerSetPlan or ActionTypeIds.DisplayConfigure or ActionTypeIds.ScriptRun => true,
        _ => false
    };
    public bool IsRestoreScriptEnabled => Type == ActionTypeIds.ScriptRun && RestoreBehaviorId == "restoreScript";
    public string Arguments { get => _arguments; set => SetProperty(ref _arguments, value); }
    public string WorkingDirectory { get => _workingDirectory; set => SetProperty(ref _workingDirectory, value); }
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

    public string Target { get => _target; set => SetWithSummary(ref _target, value); }
    public string ProcessName { get => _processName; set => SetWithSummary(ref _processName, value); }
    public string ExecutablePath { get => _executablePath; set => SetWithSummary(ref _executablePath, value); }
    public int? RuntimeProcessIdHint { get => _runtimeProcessIdHint; set => SetProperty(ref _runtimeProcessIdHint, value); }
    public string DesiredProcessState { get => _desiredProcessState; set => SetWithSummary(ref _desiredProcessState, value); }
    public string ServiceName { get => _serviceName; set => SetWithSummary(ref _serviceName, value); }
    public string ServiceDisplayName { get => _serviceDisplayName; set => SetWithSummary(ref _serviceDisplayName, value); }
    public string DesiredServiceState { get => _desiredServiceState; set => SetWithSummary(ref _desiredServiceState, value); }
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

    public bool IsValid => !IsEnabled || ValidationMessage.Length == 0;
    public string ValidationMessage => Type switch
    {
        ActionTypeIds.ProgramRun when string.IsNullOrWhiteSpace(Target) => _localizationService.GetString("Validation.ProgramTarget"),
        ActionTypeIds.ProcessSetState when string.IsNullOrWhiteSpace(ProcessName) => _localizationService.GetString("Validation.ProcessName"),
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
        _ => string.Empty
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
        foreach (var option in AvailableProcessStates.Concat(AvailableServiceStates)
                     .Concat(AvailableScriptTypes).Concat(AvailableFailurePolicies).Concat(AvailableRestoreBehaviors))
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
        _ => null
    };
}
