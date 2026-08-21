using System.IO;
using SwitchBoard.Models;
using SwitchBoard.Models.Actions;

namespace SwitchBoard.ViewModels.Actions;

public interface IActionValidator
{
    string? Validate(ActionItemViewModel action);
}

internal static class ActionValidationService
{
    private static readonly IReadOnlyDictionary<string, IActionValidator> Validators =
        new Dictionary<string, IActionValidator>(StringComparer.OrdinalIgnoreCase)
        {
            [ActionTypeIds.ProgramRun] = new ProgramRunActionValidator(),
            [ActionTypeIds.ProcessConfigure] = new ProcessActionValidator(),
            [ActionTypeIds.WaitProcessStart] = new ProcessWaitActionValidator(),
            [ActionTypeIds.WaitProcessExit] = new ProcessWaitActionValidator(),
            [ActionTypeIds.WaitWindow] = new WindowWaitActionValidator(),
            [ActionTypeIds.AudioConfigure] = new AudioActionValidator(),
            [ActionTypeIds.DeviceSetState] = new DeviceActionValidator(),
            [ActionTypeIds.ProfileRun] = new ProfileActionValidator(),
            [ActionTypeIds.ConditionIf] = new ConditionActionValidator(),
            [ActionTypeIds.NotificationShow] = new NotificationActionValidator(),
            [ActionTypeIds.ServiceSetState] = new ServiceActionValidator(),
            [ActionTypeIds.PowerSetPlan] = new PowerPlanActionValidator(),
            [ActionTypeIds.DisplayConfigure] = new DisplayActionValidator(),
            [ActionTypeIds.ScriptRun] = new ScriptActionValidator()
        };

    public static string GetMessage(ActionItemViewModel action) =>
        Validators.TryGetValue(action.Type, out var validator)
            ? validator.Validate(action) ?? string.Empty
            : string.Empty;

    public static ValidationSeverity GetSeverity(ActionItemViewModel action)
    {
        if (action.Type == ActionTypeIds.ServiceSetState &&
            string.Equals(action.DesiredServiceState, ServiceDesiredStateIds.Unchanged, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(action.DesiredServiceStartupType, ServiceStartupTypeIds.Unchanged, StringComparison.OrdinalIgnoreCase))
            return ValidationSeverity.Error;

        if (action.Type == ActionTypeIds.DeviceSetState &&
            string.Equals(action.DeviceState, DeviceStateIds.Unchanged, StringComparison.OrdinalIgnoreCase))
            return ValidationSeverity.Warning;

        return GetMessage(action).Length > 0 ? ValidationSeverity.Error : ValidationSeverity.Valid;
    }

    private abstract class LocalizedValidator : IActionValidator
    {
        public abstract string? Validate(ActionItemViewModel action);

        protected static string Text(ActionItemViewModel action, string key) => action.GetLocalizedText(key);

        protected static bool IsFullExecutablePath(string? value) => !string.IsNullOrWhiteSpace(value) &&
            Path.IsPathRooted(value) && string.Equals(Path.GetExtension(value), ".exe", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ProgramRunActionValidator : LocalizedValidator
    {
        public override string? Validate(ActionItemViewModel action)
        {
            if (string.IsNullOrWhiteSpace(action.Target)) return Text(action, "Validation.ProgramTarget");
            if ((action.ChangeAffinity || action.ShouldChangeProcessPriority || action.ShouldChangeMemoryPriority || action.ShouldChangePerformanceMode) &&
                action.IsManualProcessTarget && string.IsNullOrWhiteSpace(action.ProcessName))
                return Text(action, "Validation.PostLaunchProcess");
            if (action.RestoreBehaviorId == "closeStarted" && !IsFullExecutablePath(action.Target))
                return Text(action, "Validation.ProgramRestorePath");
            return null;
        }
    }

    private sealed class ProcessActionValidator : LocalizedValidator
    {
        public override string? Validate(ActionItemViewModel action)
        {
            if (string.IsNullOrWhiteSpace(action.ProcessName)) return Text(action, "Validation.ProcessName");
            if (!action.IsProcessStopMode && !action.ChangeAffinity && !action.ShouldChangeProcessPriority &&
                !action.ShouldChangeMemoryPriority && !action.ShouldChangePerformanceMode)
                return Text(action, "Validation.NoOp");
            if (!action.IsProcessStopMode && action.ChangeAffinity && !action.LogicalCpus.Any(cpu => cpu.IsSelected))
                return Text(action, "Validation.CpuAffinity");
            if (action.IsProcessStopMode && action.RestoreBehaviorId == "restart" && !IsFullExecutablePath(action.ExecutablePath))
                return Text(action, "Validation.ProcessRestorePath");
            return null;
        }
    }

    private sealed class ProcessWaitActionValidator : LocalizedValidator
    {
        public override string? Validate(ActionItemViewModel action) =>
            string.IsNullOrWhiteSpace(action.ProcessName) ? Text(action, "Validation.ProcessName") : null;
    }

    private sealed class WindowWaitActionValidator : LocalizedValidator
    {
        public override string? Validate(ActionItemViewModel action)
        {
            if (string.IsNullOrWhiteSpace(action.ProcessName)) return Text(action, "Validation.ProcessName");
            return action.WindowMatchMode is WindowMatchModeIds.Contains or WindowMatchModeIds.Exact &&
                   string.IsNullOrWhiteSpace(action.WindowTitle)
                ? Text(action, "Validation.WindowTitle")
                : null;
        }
    }

    private sealed class AudioActionValidator : LocalizedValidator
    {
        public override string? Validate(ActionItemViewModel action) =>
            string.IsNullOrWhiteSpace(action.AudioOutputDeviceId) &&
            string.IsNullOrWhiteSpace(action.AudioInputDeviceId) &&
            !action.ChangeVolume && !action.ChangeMute
                ? Text(action, "Validation.Audio")
                : null;
    }

    private sealed class DeviceActionValidator : LocalizedValidator
    {
        public override string? Validate(ActionItemViewModel action)
        {
            if (string.IsNullOrWhiteSpace(action.DeviceInstanceId)) return Text(action, "Validation.Device");
            return string.Equals(action.DeviceState, DeviceStateIds.Unchanged, StringComparison.OrdinalIgnoreCase)
                ? Text(action, "Validation.NoOp")
                : null;
        }
    }

    private sealed class ProfileActionValidator : LocalizedValidator
    {
        public override string? Validate(ActionItemViewModel action) =>
            !Guid.TryParse(action.TargetProfileId, out _) ? Text(action, "Validation.Profile") : null;
    }

    private sealed class ConditionActionValidator : LocalizedValidator
    {
        public override string? Validate(ActionItemViewModel action)
        {
            if (string.IsNullOrWhiteSpace(action.ConditionType) || string.IsNullOrWhiteSpace(action.ConditionValue))
                return Text(action, "Validation.Condition");
            if (action.ThenActions.Concat(action.ElseActions).Any(nested => !nested.IsValid))
                return Text(action, "Validation.NestedAction");
            return action.HasNestingDepthViolation ? Text(action, "Validation.NestingDepth") : null;
        }
    }

    private sealed class NotificationActionValidator : LocalizedValidator
    {
        public override string? Validate(ActionItemViewModel action) =>
            string.IsNullOrWhiteSpace(action.NotificationMessage) ? Text(action, "Validation.Notification") : null;
    }

    private sealed class ServiceActionValidator : LocalizedValidator
    {
        public override string? Validate(ActionItemViewModel action)
        {
            if (string.IsNullOrWhiteSpace(action.ServiceName)) return Text(action, "Validation.ServiceName");
            return string.Equals(action.DesiredServiceState, ServiceDesiredStateIds.Unchanged, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(action.DesiredServiceStartupType, ServiceStartupTypeIds.Unchanged, StringComparison.OrdinalIgnoreCase)
                ? Text(action, "Validation.NoOp")
                : null;
        }
    }

    private sealed class PowerPlanActionValidator : LocalizedValidator
    {
        public override string? Validate(ActionItemViewModel action) =>
            !Guid.TryParse(action.PowerPlanGuid, out _) ? Text(action, "Validation.PowerPlan") : null;
    }

    private sealed class DisplayActionValidator : LocalizedValidator
    {
        public override string? Validate(ActionItemViewModel action) =>
            string.IsNullOrWhiteSpace(action.DisplayDeviceName) || action.DisplayWidth <= 0 ||
            action.DisplayHeight <= 0 || action.DisplayRefreshRate <= 0
                ? Text(action, "Validation.Display")
                : null;
    }

    private sealed class ScriptActionValidator : LocalizedValidator
    {
        public override string? Validate(ActionItemViewModel action)
        {
            if (string.IsNullOrWhiteSpace(action.ScriptPath)) return Text(action, "Validation.ScriptPath");
            return action.IsRestoreScriptEnabled && string.IsNullOrWhiteSpace(action.RestoreScriptPath)
                ? Text(action, "Validation.RestoreScriptPath")
                : null;
        }
    }
}
