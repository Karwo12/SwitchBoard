using System.IO;
using System.Security.Principal;
using SwitchBoard.Models.Actions;
using SwitchBoard.Localization;
using SwitchBoard.Services.Discovery;
using SwitchBoard.Services.Execution.Handlers;
using SwitchBoard.Services.Windows;
using SwitchBoard.ViewModels;

namespace SwitchBoard.Services.Monitoring;

public sealed class StatusMonitoringService(
    IWindowsServiceManager serviceManager,
    IPowerPlanManager powerPlanManager,
    IDisplayManager displayManager,
    IAudioManager audioManager,
    IDeviceManager deviceManager,
    IProcessDiscoveryService processDiscoveryService,
    ILocalizationService localization)
{
    private int _running;
    private int _refreshAgain;
    private readonly SemaphoreSlim _systemCaptureGate = new(1, 1);

    public bool IsRunning => Volatile.Read(ref _running) != 0;

    /// <summary>
    /// Reuses the same Windows managers that power profile-action status cards to
    /// build a bounded overview for the System panel. Individual provider failures
    /// intentionally become unavailable fields instead of failing the whole view.
    /// </summary>
    public async Task<SystemStatusSnapshot> CaptureSystemSummaryAsync(
        IEnumerable<ActionItemViewModel> actions,
        CancellationToken cancellationToken = default)
    {
        await _systemCaptureGate.WaitAsync(cancellationToken);
        try
        {
            var activePlanName = await TryReadAsync(async () =>
            {
                var activeId = await powerPlanManager.GetActivePlanAsync(cancellationToken);
                var plans = await powerPlanManager.GetPlansAsync(cancellationToken);
                return plans.FirstOrDefault(plan => plan.Id == activeId)?.DisplayName;
            });

            var displays = await TryReadAsync(() => displayManager.GetDisplaysAsync(cancellationToken)) ?? [];
            var displayStatuses = displays.Select(display => new SystemDisplayStatus(
                display.DisplayName,
                true,
                display.CurrentWidth,
                display.CurrentHeight,
                display.CurrentRefreshRate,
                display.IsPrimary)).ToList();

            // Reading the current endpoint directly remains reliable on machines where endpoint
            // enumeration is restricted by a remote/virtual audio driver.
            var defaultOutputDevice = await TryReadAsync(() =>
                audioManager.GetDefaultDeviceAsync(false, false, cancellationToken));
            var defaultInputDevice = await TryReadAsync(() =>
                audioManager.GetDefaultDeviceAsync(true, false, cancellationToken));
            var audioDevices = await TryReadAsync(() => audioManager.GetDevicesAsync(cancellationToken)) ?? [];
            var defaultOutputId = await TryReadAsync(() =>
                audioManager.GetDefaultDeviceIdAsync(false, false, cancellationToken));
            var defaultInputId = await TryReadAsync(() =>
                audioManager.GetDefaultDeviceIdAsync(true, false, cancellationToken));
            var defaultOutput = defaultOutputDevice?.FriendlyName ?? audioDevices.FirstOrDefault(device =>
                string.Equals(device.Id, defaultOutputId, StringComparison.OrdinalIgnoreCase) ||
                (!device.IsInput && device.IsDefaultMultimedia))?.FriendlyName;
            var defaultInput = defaultInputDevice?.FriendlyName ?? audioDevices.FirstOrDefault(device =>
                string.Equals(device.Id, defaultInputId, StringComparison.OrdinalIgnoreCase) ||
                (device.IsInput && device.IsDefaultMultimedia))?.FriendlyName;

            var managedTargets = await CaptureManagedTargetsAsync(actions, cancellationToken);
            return new SystemStatusSnapshot(
                WindowsElevation.IsProcessElevated(),
                TimeSpan.FromMilliseconds(Math.Max(0, Environment.TickCount64)),
                activePlanName,
                displayStatuses,
                defaultOutput,
                defaultInput,
                managedTargets);
        }
        finally
        {
            _systemCaptureGate.Release();
        }
    }

    public async Task RefreshSelectedProfileAsync(
        IEnumerable<ActionItemViewModel> actions,
        CancellationToken cancellationToken = default)
    {
        var selectedActions = actions.SelectMany(Flatten)
            .Where(action => action.IsEnabled && !action.IsComment)
            .ToList();
        if (selectedActions.Count == 0) return;
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            Interlocked.Exchange(ref _refreshAgain, 1);
            return;
        }

        try
        {
            do
            {
                Interlocked.Exchange(ref _refreshAgain, 0);
                IReadOnlyList<ProcessCandidate>? processes = null;
                foreach (var action in selectedActions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!action.ShouldMonitorCurrentStatus)
                    {
                        action.ClearCurrentStatus();
                        continue;
                    }
                    try
                    {
                        var snapshot = await RefreshActionAsync(action, processes, cancellationToken);
                        if (snapshot.RequiresProcessScan)
                            // Status cards only need process identity/state. Loading shell icons for
                            // every process is useful in the picker, but is unnecessary work here.
                            processes ??= await processDiscoveryService.GetProcessesAsync(cancellationToken,
                                includeIcons: false);
                        if (snapshot.RequiresProcessScan && snapshot.Deferred is not null)
                            snapshot = await snapshot.Deferred(processes!, cancellationToken);
                        action.SetCurrentStatus(snapshot.Text, snapshot.TechnicalDetails, DateTimeOffset.Now);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
                    catch (Exception exception)
                    {
                        action.SetCurrentStatus(null, exception.Message, DateTimeOffset.Now);
                    }
                }
            } while (Interlocked.Exchange(ref _refreshAgain, 0) != 0);
        }
        finally { Volatile.Write(ref _running, 0); }
    }

    private async Task<StatusSnapshot> RefreshActionAsync(ActionItemViewModel action,
        IReadOnlyList<ProcessCandidate>? processes, CancellationToken cancellationToken)
    {
        switch (action.Type)
        {
            case ActionTypeIds.ServiceSetState:
            {
                var snapshot = await serviceManager.GetSnapshotAsync(action.ServiceName, cancellationToken);
                return new(localization.Format("ActionStatus.Service", LocalizeRuntime(snapshot.RuntimeState),
                        LocalizeStartup(snapshot.StartupType)),
                    $"Status={snapshot.RuntimeState}; StartupType={snapshot.StartupType}");
            }
            case ActionTypeIds.PowerSetPlan:
            {
                var active = await powerPlanManager.GetActivePlanAsync(cancellationToken);
                var isActive = Guid.TryParse(action.PowerPlanGuid, out var requested) && requested == active;
                return new(localization.GetString(isActive ? "ActionStatus.Active" : "ActionStatus.Inactive"), $"ActivePlan={active:D}");
            }
            case ActionTypeIds.DisplayConfigure:
            {
                var state = await displayManager.GetCurrentStateAsync(action.DisplayDeviceId, action.DisplayDeviceName,
                    cancellationToken);
                return new(localization.Format("ActionStatus.Display", state.Width, state.Height, state.RefreshRate),
                    $"Monitor={state.DisplayName}; Device={state.DeviceId}; Width={state.Width}; Height={state.Height}; RefreshRate={state.RefreshRate}");
            }
            case ActionTypeIds.AudioConfigure:
            {
                var defaultId = await audioManager.GetDefaultDeviceIdAsync(false, false, cancellationToken);
                var volume = await audioManager.GetMasterVolumeAsync(action.AudioOutputDeviceId, cancellationToken);
                return new(localization.Format("ActionStatus.Audio", localization.GetString(volume.Muted ? "ActionStatus.Muted" : "ActionStatus.Unmuted"), volume.Volume),
                    $"DefaultOutput={defaultId ?? "none"}; Volume={volume.Volume:0.##}; Muted={volume.Muted}");
            }
            case ActionTypeIds.DeviceSetState:
            {
                var device = await deviceManager.GetDeviceAsync(action.DeviceInstanceId, cancellationToken);
                return device is null
                    ? new(null, $"Device '{action.DeviceInstanceId}' was not found.")
                    : new(localization.GetString(device.IsEnabled ? "ActionStatus.Enabled" : "ActionStatus.Disabled"),
                        $"Device={device.InstanceId}; Enabled={device.IsEnabled}");
            }
            case ActionTypeIds.ProgramRun:
            case ActionTypeIds.ProcessSetState:
            case ActionTypeIds.ProcessConfigure:
            case ActionTypeIds.WaitProcessStart:
            case ActionTypeIds.WaitProcessExit:
            case ActionTypeIds.WaitWindow:
                return new(null, null, true, async (found, _) => ProcessStatus(action, found));
            default:
                return new(localization.GetString("ActionStatus.NotApplicable"), null);
        }
    }

    private StatusSnapshot ProcessStatus(ActionItemViewModel action, IReadOnlyList<ProcessCandidate> processes)
    {
        var usesConfiguredProgramTarget = action.Type == ActionTypeIds.ProgramRun && action.IsUriTarget;
        var name = ProcessTargetResolver.NormalizeName(action.Type == ActionTypeIds.ProgramRun &&
                                                       !usesConfiguredProgramTarget
            ? action.Target
            : action.ProcessName);
        var path = action.Type == ActionTypeIds.ProgramRun && !usesConfiguredProgramTarget
            ? Path.IsPathRooted(action.Target) &&
              string.Equals(Path.GetExtension(action.Target), ".exe", StringComparison.OrdinalIgnoreCase)
                ? action.Target
                : null
            : string.IsNullOrWhiteSpace(action.ExecutablePath) ? null : action.ExecutablePath;
        if (string.IsNullOrWhiteSpace(name)) return new(null, "No process name is configured.");
        var matching = 0;
        var unverified = 0;
        foreach (var process in processes)
        {
            var match = ProcessTargetResolver.MatchesSnapshot(name, path, process.ProcessName,
                process.ExecutablePath, !string.IsNullOrWhiteSpace(process.ExecutablePath));
            if (match == ProcessTargetMatch.Match) matching++;
            else if (match == ProcessTargetMatch.PathUnavailable) unverified++;
        }
        var status = matching > 0
            ? localization.GetString("ActionStatus.Running")
            : unverified > 0 ? null : localization.GetString("ActionStatus.Stopped");
        return new(status,
            $"ProcessName={name}; ExecutablePath={path ?? "not configured"}; " +
            $"MatchingProcesses={matching}; PathUnverifiedProcesses={unverified}");
    }

    private static IEnumerable<ActionItemViewModel> Flatten(ActionItemViewModel action)
    {
        yield return action;
        foreach (var nested in action.ThenActions.SelectMany(Flatten)) yield return nested;
        foreach (var nested in action.ElseActions.SelectMany(Flatten)) yield return nested;
    }

    private async Task<IReadOnlyList<ManagedTargetStatus>> CaptureManagedTargetsAsync(
        IEnumerable<ActionItemViewModel> actions, CancellationToken cancellationToken)
    {
        var targets = actions.SelectMany(Flatten)
            .Where(action => action.IsEnabled && action.ShouldMonitorCurrentStatus && IsManagedTarget(action))
            .GroupBy(action => $"{action.Type}:{TargetKey(action)}", StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .Take(16)
            .ToList();
        if (targets.Count == 0) return [];

        var snapshots = new List<(ActionItemViewModel Action, StatusSnapshot Snapshot)>();
        foreach (var action in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { snapshots.Add((action, await RefreshActionAsync(action, null, cancellationToken))); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { snapshots.Add((action, new StatusSnapshot(null, null))); }
        }

        IReadOnlyList<ProcessCandidate>? processes = null;
        if (snapshots.Any(item => item.Snapshot.RequiresProcessScan))
        {
            try { processes = await processDiscoveryService.GetProcessesAsync(cancellationToken, includeIcons: false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch { processes = []; }
        }

        var result = new List<ManagedTargetStatus>();
        foreach (var (action, initial) in snapshots)
        {
            var snapshot = initial;
            if (snapshot.RequiresProcessScan && snapshot.Deferred is not null)
                snapshot = await snapshot.Deferred(processes ?? [], cancellationToken);
            result.Add(new ManagedTargetStatus(action.DisplayName, TargetDisplayName(action),
                snapshot.Text ?? localization.GetString("ActionStatus.Unavailable")));
        }
        return result;
    }

    private static bool IsManagedTarget(ActionItemViewModel action) => action.Type is
        ActionTypeIds.ServiceSetState or ActionTypeIds.PowerSetPlan or ActionTypeIds.DisplayConfigure or
        ActionTypeIds.AudioConfigure or ActionTypeIds.DeviceSetState or ActionTypeIds.ProgramRun or
        ActionTypeIds.ProcessSetState or ActionTypeIds.ProcessConfigure or ActionTypeIds.WaitProcessStart or
        ActionTypeIds.WaitProcessExit or ActionTypeIds.WaitWindow;

    private static string TargetKey(ActionItemViewModel action) => action.Type switch
    {
        ActionTypeIds.ServiceSetState => action.ServiceName,
        ActionTypeIds.PowerSetPlan => action.PowerPlanGuid,
        ActionTypeIds.DisplayConfigure => action.DisplayDeviceId,
        ActionTypeIds.AudioConfigure => action.AudioOutputDeviceId,
        ActionTypeIds.DeviceSetState => action.DeviceInstanceId,
        ActionTypeIds.ProgramRun => action.Target,
        _ => action.ProcessName
    };

    private static string TargetDisplayName(ActionItemViewModel action)
    {
        var target = action.Type switch
        {
            ActionTypeIds.ServiceSetState => FirstNonEmpty(action.ServiceDisplayName, action.ServiceName),
            ActionTypeIds.PowerSetPlan => FirstNonEmpty(action.PowerPlanName, action.PowerPlanGuid),
            ActionTypeIds.DisplayConfigure => FirstNonEmpty(action.DisplayMonitorName, action.DisplayDeviceName),
            ActionTypeIds.AudioConfigure => FirstNonEmpty(action.AudioOutputDeviceName, action.AudioInputDeviceName),
            ActionTypeIds.DeviceSetState => FirstNonEmpty(action.DeviceFriendlyName, action.DeviceInstanceId),
            ActionTypeIds.ProgramRun => FirstNonEmpty(action.Target, action.ProcessName),
            _ => FirstNonEmpty(action.ProcessName, action.ExecutablePath)
        };
        return string.IsNullOrWhiteSpace(target) ? action.DisplayName : target;
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static async Task<T?> TryReadAsync<T>(Func<Task<T>> read)
    {
        try { return await read(); }
        catch (OperationCanceledException) { throw; }
        catch { return default; }
    }

    private string LocalizeRuntime(string value) => value switch
    {
        "Running" => localization.GetString("ServiceState.Running"),
        "Stopped" => localization.GetString("ServiceState.Stopped"),
        _ => value
    };

    private string LocalizeStartup(string value) => value switch
    {
        "Automatic" => localization.GetString("ServiceStartupType.Automatic"),
        "Automatic (Delayed Start)" => localization.GetString("ServiceStartupType.AutomaticDelayed"),
        "Manual" => localization.GetString("ServiceStartupType.Manual"),
        "Disabled" => localization.GetString("ServiceStartupType.Disabled"),
        _ => value
    };

    private sealed record StatusSnapshot(string? Text, string? TechnicalDetails, bool RequiresProcessScan = false,
        Func<IReadOnlyList<ProcessCandidate>, CancellationToken, Task<StatusSnapshot>>? Deferred = null);
}

public sealed record SystemStatusSnapshot(
    bool IsAdministrator,
    TimeSpan Uptime,
    string? ActivePowerPlanName,
    IReadOnlyList<SystemDisplayStatus> Displays,
    string? DefaultOutputDevice,
    string? DefaultInputDevice,
    IReadOnlyList<ManagedTargetStatus> ManagedTargets);

public sealed record SystemDisplayStatus(string Name, bool IsActive, int Width, int Height, int RefreshRate,
    bool IsPrimary)
{
    public string ResolutionText => $"{Width} × {Height} @ {RefreshRate} Hz";
}

public sealed record ManagedTargetStatus(string Name, string Target, string Status);
