using System.IO;
using SwitchBoard.Models.Actions;

namespace SwitchBoard.ViewModels.Actions;

public interface IActionSummaryProvider
{
    string GetSummary(ActionItemViewModel action);
}

internal static class ActionSummaryService
{
    private static readonly IReadOnlyDictionary<string, IActionSummaryProvider> Providers =
        new Dictionary<string, IActionSummaryProvider>(StringComparer.OrdinalIgnoreCase)
        {
            [ActionTypeIds.ProgramRun] = new ProgramRunSummaryProvider(),
            [ActionTypeIds.ProcessConfigure] = new ProcessSummaryProvider(),
            [ActionTypeIds.ServiceSetState] = new ServiceSummaryProvider(),
            [ActionTypeIds.PowerSetPlan] = new SimpleSummaryProvider((action, localization) =>
                localization.Format("ActionSummary.PowerPlan", PowerPlanTarget(action, localization))),
            [ActionTypeIds.ScriptRun] = new SimpleSummaryProvider((action, localization) =>
                localization.Format("ActionSummary.Script", FileSummary(action.ScriptPath, localization))),
            [ActionTypeIds.DisplayConfigure] = new SimpleSummaryProvider((action, localization) =>
                localization.Format("ActionSummary.Display", DisplayTarget(action, localization))),
            [ActionTypeIds.Delay] = new SimpleSummaryProvider((action, localization) =>
                localization.Format("ActionSummary.Delay", action.DelaySeconds)),
            [ActionTypeIds.WaitProcessStart] = new SimpleSummaryProvider((action, localization) => ProcessTarget(action, localization)),
            [ActionTypeIds.WaitProcessExit] = new SimpleSummaryProvider((action, localization) => ProcessTarget(action, localization)),
            [ActionTypeIds.WaitWindow] = new SimpleSummaryProvider((action, localization) => ProcessTarget(action, localization)),
            [ActionTypeIds.AudioConfigure] = new SimpleSummaryProvider((action, _) =>
                string.IsNullOrWhiteSpace(action.AudioOutputDeviceName) ? action.AudioInputDeviceName : action.AudioOutputDeviceName),
            [ActionTypeIds.DeviceSetState] = new SimpleSummaryProvider((action, _) =>
                string.IsNullOrWhiteSpace(action.DeviceFriendlyName) ? action.DeviceInstanceId : action.DeviceFriendlyName),
            [ActionTypeIds.ProfileRun] = new SimpleSummaryProvider((action, _) =>
                string.IsNullOrWhiteSpace(action.TargetProfileName) ? action.TargetProfileId : action.TargetProfileName),
            [ActionTypeIds.ConditionIf] = new SimpleSummaryProvider((action, _) => action.ConditionValue),
            [ActionTypeIds.NotificationShow] = new SimpleSummaryProvider((action, _) => action.NotificationMessage),
            [ActionTypeIds.Comment] = new SimpleSummaryProvider((_, _) => string.Empty)
        };

    public static string GetSummary(ActionItemViewModel action) =>
        Providers.TryGetValue(action.Type, out var provider)
            ? provider.GetSummary(action)
            : action.DisplayName;

    private sealed class SimpleSummaryProvider(Func<ActionItemViewModel, SwitchBoard.Localization.ILocalizationService, string> factory)
        : IActionSummaryProvider
    {
        public string GetSummary(ActionItemViewModel action) => factory(action, action.LocalizationService);
    }

    private sealed class ProgramRunSummaryProvider : IActionSummaryProvider
    {
        public string GetSummary(ActionItemViewModel action)
        {
            var localization = action.LocalizationService;
            var summary = localization.Format("ActionSummary.RunProgram", FileSummary(action.Target, localization));
            var options = new List<string>();

            if (action.InstanceBehavior == InstanceBehaviorIds.StartAnother)
                options.Add(localization.GetString("ActionSummary.ProgramStartsAnother"));
            else if (action.InstanceBehavior == InstanceBehaviorIds.DoNotStartAgain)
                options.Add(localization.GetString("ActionSummary.ProgramDoesNotDuplicate"));
            if (!string.Equals(action.WindowBehavior, WindowBehaviorIds.None, StringComparison.OrdinalIgnoreCase))
                options.Add(localization.Format("ActionSummary.ProgramWindow",
                    action.AvailableWindowBehaviors.FirstOrDefault(option => option.Value == action.WindowBehavior)?.DisplayName ?? action.WindowBehavior));
            if (action.RetryOnFailure)
                options.Add(localization.GetString("ActionSummary.ProgramRetry"));
            if (action.UseCustomWorkingDirectory)
                options.Add(localization.GetString("ActionSummary.ProgramCustomDirectory"));
            if (action.ShouldChangeProcessPriority)
                options.Add(localization.Format("ActionSummary.ProcessPriority",
                    action.AvailableProcessPriorities.FirstOrDefault(option => option.Value == action.ProcessPriority)?.DisplayName ?? action.ProcessPriority));
            if (action.ShouldChangeMemoryPriority)
                options.Add(localization.Format("ActionSummary.ProcessMemoryPriority",
                    action.AvailableProcessMemoryPriorities.FirstOrDefault(option => option.Value == action.ProcessMemoryPriority)?.DisplayName ?? action.ProcessMemoryPriority));
            if (action.ShouldChangePerformanceMode)
                options.Add(localization.Format("ActionSummary.ProcessPerformanceMode",
                    action.AvailableProcessPerformanceModes.FirstOrDefault(option => option.Value == action.ProcessPerformanceMode)?.DisplayName ?? action.ProcessPerformanceMode));
            if (action.ChangeAffinity)
                options.Add(localization.Format("ActionSummary.ProcessAffinity", SelectedCpuSummary(action, localization)));
            if (!string.Equals(action.RestoreBehaviorId, "none", StringComparison.OrdinalIgnoreCase))
                options.Add(localization.Format("ActionSummary.ProgramRestore",
                    action.AvailableRestoreBehaviors.FirstOrDefault(option => option.Value == action.RestoreBehaviorId)?.DisplayName ?? action.RestoreBehaviorId));

            return options.Count == 0 ? summary : $"{summary} · {string.Join(" · ", options)}";
        }
    }

    private sealed class ProcessSummaryProvider : IActionSummaryProvider
    {
        public string GetSummary(ActionItemViewModel action)
        {
            var localization = action.LocalizationService;
            var target = ProcessTarget(action, localization);
            if (action.IsProcessStopMode)
                return localization.Format("ActionSummary.StopProcessInAction", target);

            var summary = localization.Format("ActionSummary.ConfigureProcess", target);
            var options = new List<string>();
            if (action.ShouldChangeProcessPriority)
                options.Add(localization.Format("ActionSummary.ProcessPriority",
                    action.AvailableProcessPriorities.FirstOrDefault(option => option.Value == action.ProcessPriority)?.DisplayName ?? action.ProcessPriority));
            if (action.ShouldChangeMemoryPriority)
                options.Add(localization.Format("ActionSummary.ProcessMemoryPriority",
                    action.AvailableProcessMemoryPriorities.FirstOrDefault(option => option.Value == action.ProcessMemoryPriority)?.DisplayName ?? action.ProcessMemoryPriority));
            if (action.ShouldChangePerformanceMode)
                options.Add(localization.Format("ActionSummary.ProcessPerformanceMode",
                    action.AvailableProcessPerformanceModes.FirstOrDefault(option => option.Value == action.ProcessPerformanceMode)?.DisplayName ?? action.ProcessPerformanceMode));
            if (action.ChangeAffinity)
                options.Add(localization.Format("ActionSummary.ProcessAffinity", SelectedCpuSummary(action, localization)));
            return options.Count == 0 ? summary : $"{summary} • {string.Join(" • ", options)}";
        }
    }

    private sealed class ServiceSummaryProvider : IActionSummaryProvider
    {
        public string GetSummary(ActionItemViewModel action)
        {
            var localization = action.LocalizationService;
            var target = string.IsNullOrWhiteSpace(action.ServiceDisplayName)
                ? string.IsNullOrWhiteSpace(action.ServiceName)
                    ? localization.GetString("ActionSummary.NotConfigured")
                    : action.ServiceName
                : action.ServiceDisplayName;
            var changes = new List<string>();
            if (!string.Equals(action.DesiredServiceState, ServiceDesiredStateIds.Unchanged, StringComparison.OrdinalIgnoreCase))
                changes.Add(action.AvailableServiceStates.FirstOrDefault(option => option.Value == action.DesiredServiceState)?.DisplayName ?? action.DesiredServiceState);
            if (!string.Equals(action.DesiredServiceStartupType, ServiceStartupTypeIds.Unchanged, StringComparison.OrdinalIgnoreCase))
                changes.Add(action.AvailableServiceStartupTypes.FirstOrDefault(option => option.Value == action.DesiredServiceStartupType)?.DisplayName ?? action.DesiredServiceStartupType);
            return changes.Count == 0
                ? localization.Format("ActionSummary.Service", target)
                : localization.Format("ActionSummary.ServiceConfigured", target, string.Join(", ", changes));
        }
    }

    private static string FileSummary(string value, SwitchBoard.Localization.ILocalizationService localization)
    {
        if (string.IsNullOrWhiteSpace(value)) return localization.GetString("ActionSummary.NotConfigured");
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !uri.IsFile) return value;
        return Path.GetFileName(value.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    private static string ProcessTarget(ActionItemViewModel action, SwitchBoard.Localization.ILocalizationService localization) =>
        !string.IsNullOrWhiteSpace(action.ExecutablePath)
            ? Path.GetFileName(action.ExecutablePath)
            : string.IsNullOrWhiteSpace(action.ProcessName)
                ? localization.GetString("ActionSummary.NotConfigured")
                : $"{Path.GetFileNameWithoutExtension(action.ProcessName)}.exe";

    private static string PowerPlanTarget(ActionItemViewModel action, SwitchBoard.Localization.ILocalizationService localization) =>
        !string.IsNullOrWhiteSpace(action.PowerPlanName)
            ? action.PowerPlanName
            : string.IsNullOrWhiteSpace(action.PowerPlanGuid)
                ? localization.GetString("ActionSummary.NotConfigured")
                : action.PowerPlanGuid;

    private static string DisplayTarget(ActionItemViewModel action, SwitchBoard.Localization.ILocalizationService localization) =>
        string.IsNullOrWhiteSpace(action.DisplayMonitorName) || action.DisplayWidth <= 0
            ? localization.GetString("ActionSummary.NotConfigured")
            : $"{action.DisplayMonitorName} · {action.DisplayWidth} × {action.DisplayHeight} @ {action.DisplayRefreshRate} Hz";

    private static string SelectedCpuSummary(ActionItemViewModel action, SwitchBoard.Localization.ILocalizationService localization)
    {
        var selected = action.LogicalCpus.Where(cpu => cpu.IsSelected).Select(cpu => cpu.Index).ToArray();
        return selected.Length == 0
            ? localization.GetString("ActionSummary.NotConfigured")
            : selected.Length == action.LogicalCpus.Count
                ? localization.GetString("ActionSummary.AllCpus")
                : string.Join(", ", selected);
    }
}
