using System.IO;
using System.Text.Json.Nodes;
using SwitchBoard.Localization;
using SwitchBoard.Models.Actions;

namespace SwitchBoard.ViewModels;

public sealed class ActionItemViewModel : ObservableObject
{
    private readonly ILocalizationService _localizationService;
    private readonly string? _displayNameResourceKey;
    private string? _name;
    private bool _isEnabled;
    private string _target;
    private string _arguments;
    private string _workingDirectory;
    private bool _startOnlyIfNotAlreadyRunning;
    private string _processName;
    private string _executablePath;
    private string _desiredProcessState;
    private int _delaySeconds;
    private bool _isExpanded;

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
        FailurePolicy = action.FailurePolicy;
        Timeout = action.Timeout;
        Parameters = action.Parameters.DeepClone().AsObject();
        _target = ReadString(ActionParameterNames.Target);
        _arguments = ReadString(ActionParameterNames.Arguments);
        _workingDirectory = ReadString(ActionParameterNames.WorkingDirectory);
        _startOnlyIfNotAlreadyRunning = ReadBoolean(
            ActionParameterNames.StartOnlyIfNotAlreadyRunning,
            defaultValue: true);
        _processName = ReadString(ActionParameterNames.ProcessName);
        _executablePath = ReadString(ActionParameterNames.ExecutablePath);
        _desiredProcessState = ReadString(ActionParameterNames.DesiredState);
        if (string.IsNullOrWhiteSpace(_desiredProcessState))
        {
            _desiredProcessState = ProcessDesiredStateIds.Stopped;
        }
        _delaySeconds = Math.Clamp(ReadInt32(ActionParameterNames.DelaySeconds, 0), 0, 3600);
        AvailableProcessStates =
        [
            new(ProcessDesiredStateIds.Stopped, "ProcessState.Stopped", localizationService),
            new(ProcessDesiredStateIds.Unchanged, "ProcessState.Unchanged", localizationService)
        ];
    }

    public Guid Id { get; }

    public string Type { get; }

    public int ActionSchemaVersion { get; }

    public int SortOrder { get; set; }

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
        ActionTypeIds.ProgramRun => _localizationService.Format(
            "ActionSummary.RunProgram",
            GetProgramSummaryTarget()),
        ActionTypeIds.ProcessSetState => _localizationService.Format(
            string.Equals(DesiredProcessState, ProcessDesiredStateIds.Unchanged, StringComparison.OrdinalIgnoreCase)
                ? "ActionSummary.ProcessUnchanged"
                : "ActionSummary.StopProcess",
            GetProcessSummaryTarget()),
        ActionTypeIds.Delay => _localizationService.Format("ActionSummary.Delay", DelaySeconds),
        _ => DisplayName
    };

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }
    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public string Target
    {
        get => _target;
        set
        {
            if (SetProperty(ref _target, value))
            {
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    public string Arguments
    {
        get => _arguments;
        set => SetProperty(ref _arguments, value);
    }

    public string WorkingDirectory
    {
        get => _workingDirectory;
        set => SetProperty(ref _workingDirectory, value);
    }

    public bool StartOnlyIfNotAlreadyRunning
    {
        get => _startOnlyIfNotAlreadyRunning;
        set => SetProperty(ref _startOnlyIfNotAlreadyRunning, value);
    }

    public string ProcessName
    {
        get => _processName;
        set
        {
            if (SetProperty(ref _processName, value))
            {
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    public string ExecutablePath
    {
        get => _executablePath;
        set
        {
            if (SetProperty(ref _executablePath, value))
            {
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

    public string DesiredProcessState
    {
        get => _desiredProcessState;
        set
        {
            if (SetProperty(ref _desiredProcessState, value))
            {
                OnPropertyChanged(nameof(Summary));
            }
        }
    }

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

    public IReadOnlyList<LocalizedValueOptionViewModel> AvailableProcessStates { get; }

    public ActionFailurePolicy FailurePolicy { get; set; }

    public TimeSpan? Timeout { get; set; }

    public JsonObject Parameters { get; }

    public void RefreshDisplayName()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Summary));
        foreach (var option in AvailableProcessStates)
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
    public ActionDefinition ToModel()
    {
        var parameters = Parameters.DeepClone().AsObject();
        switch (Type)
        {
            case ActionTypeIds.ProgramRun:
                SetString(parameters, ActionParameterNames.Target, Target);
                SetString(parameters, ActionParameterNames.Arguments, Arguments);
                SetString(parameters, ActionParameterNames.WorkingDirectory, WorkingDirectory);
                parameters[ActionParameterNames.StartOnlyIfNotAlreadyRunning] = StartOnlyIfNotAlreadyRunning;
                break;
            case ActionTypeIds.ProcessSetState:
                SetString(parameters, ActionParameterNames.ProcessName, ProcessName);
                SetString(parameters, ActionParameterNames.ExecutablePath, ExecutablePath);
                parameters[ActionParameterNames.DesiredState] = DesiredProcessState;
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
            FailurePolicy = FailurePolicy,
            Timeout = Timeout,
            Parameters = parameters
        };
    }

    private string ReadString(string propertyName)
    {
        try
        {
            return Parameters[propertyName]?.GetValue<string>() ?? string.Empty;
        }
        catch (InvalidOperationException)
        {
            return string.Empty;
        }
    }

    private bool ReadBoolean(string propertyName, bool defaultValue)
    {
        try
        {
            return Parameters[propertyName]?.GetValue<bool>() ?? defaultValue;
        }
        catch (InvalidOperationException)
        {
            return defaultValue;
        }
    }

    private int ReadInt32(string propertyName, int defaultValue)
    {
        try
        {
            return Parameters[propertyName]?.GetValue<int>() ?? defaultValue;
        }
        catch (InvalidOperationException)
        {
            return defaultValue;
        }
    }

    private static void SetString(JsonObject parameters, string propertyName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            parameters.Remove(propertyName);
        }
        else
        {
            parameters[propertyName] = value.Trim();
        }
    }

    private string GetProgramSummaryTarget()
    {
        if (string.IsNullOrWhiteSpace(Target))
        {
            return _localizationService.GetString("ActionSummary.NotConfigured");
        }

        if (Uri.TryCreate(Target, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            return Target;
        }

        return Path.GetFileName(Target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    private string GetProcessSummaryTarget()
    {
        if (!string.IsNullOrWhiteSpace(ExecutablePath))
        {
            return Path.GetFileName(ExecutablePath);
        }

        return string.IsNullOrWhiteSpace(ProcessName)
            ? _localizationService.GetString("ActionSummary.NotConfigured")
            : $"{Path.GetFileNameWithoutExtension(ProcessName)}.exe";
    }
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