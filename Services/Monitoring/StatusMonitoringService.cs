using System.IO;
using SwitchBoard.Models.Actions;
using SwitchBoard.Localization;
using SwitchBoard.Services.Discovery;
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

    public bool IsRunning => Volatile.Read(ref _running) != 0;

    public async Task RefreshSelectedProfileAsync(
        IEnumerable<ActionItemViewModel> actions,
        CancellationToken cancellationToken = default)
    {
        var selectedActions = actions.SelectMany(Flatten).Where(action => action.IsEnabled).ToList();
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
                    try
                    {
                        var snapshot = await RefreshActionAsync(action, processes, cancellationToken);
                        if (snapshot.RequiresProcessScan)
                            processes ??= await processDiscoveryService.GetProcessesAsync(cancellationToken);
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
        var name = action.Type == ActionTypeIds.ProgramRun
            ? Path.GetFileNameWithoutExtension(action.Target)
            : Path.GetFileNameWithoutExtension(action.ProcessName);
        if (string.IsNullOrWhiteSpace(name)) return new(null, "No process name is configured.");
        var matches = processes.Where(process => string.Equals(process.ProcessName, name,
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileNameWithoutExtension(process.ExecutableName), name,
                StringComparison.OrdinalIgnoreCase)).ToList();
        return new(matches.Count > 0 ? localization.GetString("ActionStatus.Running") : localization.GetString("ActionStatus.Stopped"),
            $"ProcessName={name}; MatchingProcesses={matches.Count}");
    }

    private static IEnumerable<ActionItemViewModel> Flatten(ActionItemViewModel action)
    {
        yield return action;
        foreach (var nested in action.ThenActions.SelectMany(Flatten)) yield return nested;
        foreach (var nested in action.ElseActions.SelectMany(Flatten)) yield return nested;
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
