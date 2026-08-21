using System.Text.Json.Nodes;
using SwitchBoard.Models.Actions;

namespace SwitchBoard.Services.Actions;

/// <summary>
/// The stable, non-visual metadata shared by the action picker and action editors.
/// Keeping this data here prevents the picker and view models from maintaining
/// separate lists of the same action identifiers.
/// </summary>
public sealed class ActionDescriptor
{
    private readonly Func<bool, JsonObject> _defaultParametersFactory;

    public ActionDescriptor(
        string typeId,
        string displayNameResourceKey,
        string categoryResourceKey,
        IReadOnlyList<string> keywords,
        Func<bool, JsonObject>? defaultParametersFactory = null,
        bool showInPicker = true)
    {
        TypeId = typeId;
        DisplayNameResourceKey = displayNameResourceKey;
        CategoryResourceKey = categoryResourceKey;
        Keywords = keywords;
        _defaultParametersFactory = defaultParametersFactory ?? (_ => []);
        ShowInPicker = showInPicker;
    }

    public string TypeId { get; }
    public string DisplayNameResourceKey { get; }
    public string CategoryResourceKey { get; }
    public IReadOnlyList<string> Keywords { get; }
    public bool ShowInPicker { get; }

    public JsonObject CreateDefaultParameters(bool nested) =>
        _defaultParametersFactory(nested).DeepClone().AsObject();
}

/// <summary>
/// Ordered action metadata. The order intentionally matches the existing picker.
/// This is an internal registry, not a plugin/service locator mechanism.
/// </summary>
public static class ActionDescriptorRegistry
{
    private static readonly IReadOnlyList<ActionDescriptor> _descriptors =
    [
        new(ActionTypeIds.ProgramRun, "Action.RunProgram", "ActionPicker.Category.Programs",
            ["program", "programy"], nested => nested
                ? new JsonObject
                {
                    [ActionParameterNames.StartOnlyIfNotAlreadyRunning] = true,
                    [ActionParameterNames.InstanceBehavior] = InstanceBehaviorIds.DoNotStartAgain,
                    [ActionParameterNames.ProcessPriority] = ProcessPriorityIds.NoChange,
                    [ActionParameterNames.ProcessMemoryPriority] = ProcessMemoryPriorityIds.NoChange,
                    [ActionParameterNames.ProcessPerformanceMode] = ProcessPerformanceModeIds.NoChange,
                    [ActionParameterNames.ProcessTargetMode] = ProcessTargetModeIds.Automatic,
                    [ActionParameterNames.WaitForProcessStart] = true,
                    [ActionParameterNames.ProcessStartWaitSeconds] = 10
                }
                : new JsonObject
                {
                    [ActionParameterNames.StartOnlyIfNotAlreadyRunning] = true,
                    [ActionParameterNames.ChangeAffinity] = false,
                    [ActionParameterNames.ChangePriority] = false,
                    [ActionParameterNames.ProcessPriority] = ProcessPriorityIds.NoChange,
                    [ActionParameterNames.ProcessMemoryPriority] = ProcessMemoryPriorityIds.NoChange,
                    [ActionParameterNames.ProcessPerformanceMode] = ProcessPerformanceModeIds.NoChange,
                    [ActionParameterNames.WaitForProcessStart] = true,
                    [ActionParameterNames.ProcessStartWaitSeconds] = 10,
                    [ActionParameterNames.ProcessTargetMode] = ProcessTargetModeIds.Automatic
                }),
        new(ActionTypeIds.ServiceSetState, "Action.WindowsServiceState", "ActionPicker.Category.SystemDevices",
            ["service", "usługa", "usluga"], _ => new JsonObject
            {
                [ActionParameterNames.DesiredState] = ServiceDesiredStateIds.Unchanged,
                [ActionParameterNames.ServiceStartupType] = ServiceStartupTypeIds.Unchanged
            }),
        new(ActionTypeIds.ProcessConfigure, "Action.ProcessSettings", "ActionPicker.Category.Programs",
            ["process", "proces"], _ => new JsonObject
            {
                [ActionParameterNames.ProcessOperation] = ProcessOperationIds.Configure,
                [ActionParameterNames.ChangeAffinity] = false,
                [ActionParameterNames.ChangePriority] = false,
                [ActionParameterNames.ProcessPriority] = ProcessPriorityIds.NoChange,
                [ActionParameterNames.ProcessMemoryPriority] = ProcessMemoryPriorityIds.NoChange,
                [ActionParameterNames.ProcessPerformanceMode] = ProcessPerformanceModeIds.NoChange
            }),
        new(ActionTypeIds.WaitProcessStart, "Action.WaitProcess", "ActionPicker.Category.WaitingTiming",
            ["process", "proces"]),
        new(ActionTypeIds.WaitProcessExit, "Action.WaitProcessExit", "ActionPicker.Category.WaitingTiming",
            ["process", "proces"]),
        new(ActionTypeIds.WaitWindow, "Action.WaitWindow", "ActionPicker.Category.WaitingTiming",
            ["window", "okno"], nested => nested
                ? new JsonObject { [ActionParameterNames.WindowMatchMode] = WindowMatchModeIds.Any }
                : []),
        new(ActionTypeIds.PowerSetPlan, "Action.PowerPlan", "ActionPicker.Category.SystemDevices",
            ["power", "zasilanie"]),
        new(ActionTypeIds.DisplayConfigure, "Action.DisplaySettings", "ActionPicker.Category.SystemDevices",
            ["display", "ekran"]),
        new(ActionTypeIds.DeviceSetState, "Action.DeviceState", "ActionPicker.Category.SystemDevices",
            ["device", "urządzenie", "urzadzenie"], _ => new JsonObject
            {
                [ActionParameterNames.DesiredState] = DeviceStateIds.Unchanged
            }),
        new(ActionTypeIds.AudioConfigure, "Action.AudioSettings", "ActionPicker.Category.SystemDevices",
            ["audio", "dźwięk", "dzwiek"], _ => new JsonObject
            {
                [ActionParameterNames.SetDefaultMultimedia] = true,
                [ActionParameterNames.SetDefaultCommunications] = false
            }),
        new(ActionTypeIds.Delay, "Action.Delay", "ActionPicker.Category.WaitingTiming",
            ["delay", "opóźnienie", "opoznienie"], _ => new JsonObject
            {
                [ActionParameterNames.DelaySeconds] = 0
            }),
        new(ActionTypeIds.ScriptRun, "Action.RunScript", "ActionPicker.Category.Automation",
            ["script", "skrypt"], _ => new JsonObject
            {
                [ActionParameterNames.ScriptType] = ScriptTypeIds.AutoDetect,
                [ActionParameterNames.WaitForExit] = true,
                [ActionParameterNames.RunAsAdministrator] = false
            }),
        new(ActionTypeIds.NotificationShow, "Action.Notification", "ActionPicker.Category.Automation",
            ["notification", "powiadomienie"], _ => new JsonObject
            {
                [ActionParameterNames.NotificationLevel] = NotificationLevelIds.Info
            }),
        new(ActionTypeIds.ProfileRun, "Action.RunProfile", "ActionPicker.Category.Automation",
            ["profile", "profil"]),
        new(ActionTypeIds.ConditionIf, "Action.If", "ActionPicker.Category.Automation",
            ["if", "warunek"], _ => new JsonObject
            {
                [ActionParameterNames.ConditionType] = ConditionTypeIds.ProcessRunning,
                [ActionParameterNames.ThenActions] = new JsonArray(),
                [ActionParameterNames.ElseActions] = new JsonArray()
            }),
        // Kept for old serialized process.setState definitions. It is normalized
        // by ActionItemViewModel and is intentionally not offered by the picker.
        new(ActionTypeIds.ProcessSetState, "Action.ProcessSettings", "ActionPicker.Category.Programs",
            ["process", "proces"], _ => new JsonObject
            {
                [ActionParameterNames.DesiredState] = ProcessDesiredStateIds.Stopped
            }, showInPicker: false)
    ];

    private static readonly IReadOnlyDictionary<string, ActionDescriptor> _byType =
        _descriptors.ToDictionary(descriptor => descriptor.TypeId, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<ActionDescriptor> Descriptors => _descriptors;

    public static IEnumerable<ActionDescriptor> PickerDescriptors =>
        _descriptors.Where(descriptor => descriptor.ShowInPicker);

    public static bool TryGet(string? typeId, out ActionDescriptor? descriptor) =>
        _byType.TryGetValue(typeId ?? string.Empty, out descriptor);

    public static ActionDescriptor? Get(string? typeId) =>
        TryGet(typeId, out var descriptor) ? descriptor : null;
}
