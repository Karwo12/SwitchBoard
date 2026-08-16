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

    public ActionItemViewModel(ActionDefinition action, ILocalizationService localizationService)
    {
        _localizationService = localizationService;
        _displayNameResourceKey = GetDisplayNameResourceKey(action.Type);
        Id = action.Id;
        Type = action.Type;
        ActionSchemaVersion = action.ActionSchemaVersion;
        _name = action.Name;
        _isEnabled = action.IsEnabled;
        FailurePolicy = action.FailurePolicy;
        Timeout = action.Timeout;
        Parameters = action.Parameters.DeepClone().AsObject();
    }

    public Guid Id { get; }

    public string Type { get; }

    public int ActionSchemaVersion { get; }

    public string? Name
    {
        get => _name;
        set
        {
            if (SetProperty(ref _name, value))
            {
                OnPropertyChanged(nameof(DisplayName));
            }
        }
    }

    public string DisplayName => _displayNameResourceKey is not null
        ? _localizationService.GetString(_displayNameResourceKey)
        : string.IsNullOrWhiteSpace(Name) ? Type : Name;

    public bool IsEnabled
    {
        get => _isEnabled;
        set => SetProperty(ref _isEnabled, value);
    }

    public ActionFailurePolicy FailurePolicy { get; set; }

    public TimeSpan? Timeout { get; set; }

    public JsonObject Parameters { get; }

    public void RefreshDisplayName() => OnPropertyChanged(nameof(DisplayName));

    public ActionDefinition ToModel() => new()
    {
        Id = Id,
        Type = Type,
        ActionSchemaVersion = ActionSchemaVersion,
        Name = string.IsNullOrWhiteSpace(Name) ? null : Name.Trim(),
        IsEnabled = IsEnabled,
        FailurePolicy = FailurePolicy,
        Timeout = Timeout,
        Parameters = Parameters.DeepClone().AsObject()
    };

    private static string? GetDisplayNameResourceKey(string actionType) => actionType switch
    {
        ActionTypeIds.ProcessSetState => "Action.ProcessState",
        ActionTypeIds.ProgramRun => "Action.RunProgram",
        ActionTypeIds.SteamLaunch => "Action.LaunchSteamGame",
        ActionTypeIds.ServiceSetState => "Action.WindowsServiceState",
        ActionTypeIds.DisplayConfigure => "Action.DisplayConfiguration",
        ActionTypeIds.PowerSetPlan => "Action.PowerPlan",
        ActionTypeIds.ScriptRun => "Action.RunScript",
        ActionTypeIds.Delay => "Action.Delay",
        _ => null
    };
}
