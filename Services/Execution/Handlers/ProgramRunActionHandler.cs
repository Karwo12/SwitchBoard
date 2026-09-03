using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using SwitchBoard.Models.Actions;
using SwitchBoard.Localization;
using SwitchBoard.Services.Logging;

namespace SwitchBoard.Services.Execution.Handlers;

public sealed class ProgramRunActionHandler : IReversibleActionHandler
{
    private static readonly TimeSpan CaptureClockTolerance = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DiscoveryWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan DiscoveryInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan RestoreTimeout = TimeSpan.FromSeconds(8);
    private readonly ILocalizationService? _localization;
    private readonly ProcessSettingsService _settingsService;
    private readonly Func<ProcessStartInfo, Process?> _processStarter;
    private readonly IAppLogger? _logger;

    public ProgramRunActionHandler(ILocalizationService? localization = null,
        ProcessSettingsService? settingsService = null,
        Func<ProcessStartInfo, Process?>? processStarter = null,
        IAppLogger? logger = null)
    {
        _localization = localization;
        _settingsService = settingsService ?? ProcessSettingsService.Shared;
        _processStarter = processStarter ?? (startInfo => Process.Start(startInfo));
        _logger = logger;
    }

    public string ActionType => ActionTypeIds.ProgramRun;

    public async Task<ActionExecutionResult> ExecuteAsync(
        ActionDefinition action,
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var target = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.Target).Trim();
        if (string.IsNullOrWhiteSpace(target))
            return ActionExecutionResult.Failure("Program target is required.");
        if (IsProtocolTarget(target))
        {
            var configuredProcessPath = ActionParameterReader.ReadString(action.Parameters,
                ActionParameterNames.ExecutablePath).Trim();
            try { ProcessTargetResolver.NormalizeConfiguredPath(configuredProcessPath); }
            catch (InvalidOperationException exception)
            {
                return ActionExecutionResult.Failure(exception.Message);
            }
        }

        var legacyStartOnly = ActionParameterReader.ReadBoolean(
            action.Parameters, ActionParameterNames.StartOnlyIfNotAlreadyRunning, defaultValue: true);
        var instanceBehavior = ActionParameterReader.ReadString(action.Parameters,
            ActionParameterNames.InstanceBehavior);
        if (string.IsNullOrWhiteSpace(instanceBehavior))
            instanceBehavior = legacyStartOnly ? InstanceBehaviorIds.DoNotStartAgain : InstanceBehaviorIds.StartAnother;
        if (instanceBehavior == InstanceBehaviorIds.DoNotStartAgain && IsTargetAlreadyRunning(action, target))
        {
            var existingWindowResult = await ApplyWindowBehaviorAsync(action, target, cancellationToken);
            return existingWindowResult is not null && !existingWindowResult.IsSuccessful
                ? existingWindowResult
                : ActionExecutionResult.Skipped("The program is already running.");
        }

        try
        {
            if (instanceBehavior == InstanceBehaviorIds.RestartExisting)
                await StopExistingExactAsync(action, target, cancellationToken, context.Logger ?? _logger);
            if (action.RestoreBehavior != ActionRestoreBehavior.CloseIfStartedBySwitchBoard)
            {
                using var untrackedProcess = _processStarter(CreateStartInfo(action, target));
                if (untrackedProcess is null)
                    return ActionExecutionResult.Failure("Windows did not start the requested target.");
                var windowResult = await ApplyWindowBehaviorAsync(action, target, cancellationToken);
                if (windowResult is not null) return windowResult;
                if (IsProtocolTarget(target) || IsShellShortcutTarget(target))
                {
                    var postLaunchResult = await ApplyPostLaunchProcessSettingsAsync(action, target,
                        untrackedProcess.Id, cancellationToken);
                    if (postLaunchResult is not null) return postLaunchResult;
                    return ActionExecutionResult.Success(
                        "Windows accepted the shell handoff. The target application cannot be identified reliably for full verification.");
                }
                var expectedName = ProcessTargetResolver.NormalizeName(target);
                var expectedPath = Path.IsPathRooted(target) &&
                                   string.Equals(Path.GetExtension(target), ".exe", StringComparison.OrdinalIgnoreCase)
                    ? Path.GetFullPath(target) : null;
                var wait = GetPostLaunchWait(action);
                using var verificationProcess = await ProcessWaitService.WaitForStartAsync(expectedName, expectedPath,
                    wait.UseDirectProcessId ? untrackedProcess.Id : null, wait.MaximumWait, cancellationToken);
                var result = verificationProcess is not null
                    ? ActionExecutionResult.Success($"Verified: '{expectedName}' is running.")
                    : ActionExecutionResult.Failure(
                        $"Windows accepted the start request, but no matching '{expectedName}' process was found.");
                if (!result.IsSuccessful) return result;
                return await ApplyPostLaunchProcessSettingsAsync(action, target, untrackedProcess.Id, cancellationToken)
                    ?? result;
            }

            if (IsProtocolTarget(target))
            {
                var processName = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.ProcessName).Trim();
                if (string.IsNullOrWhiteSpace(processName))
                    return ActionExecutionResult.Failure(Format("Result.PostLaunchTargetRequired",
                        "A process target is required when close-on-restore is enabled."));
                using var uriProcess = _processStarter(CreateStartInfo(action, target));
                if (uriProcess is null)
                    return ActionExecutionResult.Failure("Windows did not start the requested URI.");
                var executablePath = ActionParameterReader.ReadString(action.Parameters,
                    ActionParameterNames.ExecutablePath).Trim();
                var waitForProcess = ActionParameterReader.ReadBoolean(action.Parameters,
                    ActionParameterNames.WaitForProcessStart, true);
                var processTrackingWait = GetConfiguredProcessWait(action);
                var trackingState = ProcessLaunchTracker.CreateTrackingState(context.CapturedState,
                    processName, executablePath, processTrackingWait);
                JsonArray? trackedUri = null;
                if (waitForProcess)
                {
                    trackedUri = await ProcessLaunchTracker.TrackAsync(context.CapturedState, processName,
                        executablePath, processTrackingWait, cancellationToken);
                    ProcessLaunchTracker.ApplyTrackingResult(trackingState, trackedUri);
                }
                else
                {
                    _ = ProcessLaunchTracker.TrackInBackgroundAsync(context.CapturedState, processName,
                        executablePath, processTrackingWait,
                        launched => PublishTrackingStateAsync(context, trackingState, launched),
                        context.ReportBackgroundError);
                }
                var windowResult = await ApplyWindowBehaviorAsync(action, target, cancellationToken);
                if (windowResult is not null && !windowResult.IsSuccessful) return windowResult;
                var state = trackingState;
                return ActionExecutionResult.Success(
                    trackedUri is { Count: > 0 }
                        ? $"Verified: identified {trackedUri.Count} new '{processName}' process(es)."
                        : waitForProcess
                            ? "The URI was accepted; no new target process was identified within the configured timeout."
                            : "The URI was accepted; target process tracking continues in the background.", state);
            }

            var fullPath = ValidateRestorableTarget(target);
            var baseline = ReadIdentities(context.CapturedState?["preExistingProcesses"] as JsonArray);
            if (baseline.Count == 0)
                baseline = CaptureMatchingProcesses(fullPath);

            var launchedAfterTicks = DateTime.UtcNow.Subtract(CaptureClockTolerance).Ticks;
            using var startedProcess = _processStarter(CreateStartInfo(action, target));
            if (startedProcess is null)
                return ActionExecutionResult.Failure("Windows did not start the requested target.");

            var directProcessId = TryRead(() => startedProcess.Id) ?? 0;
            var tracked = new Dictionary<RuntimeProcessIdentityKey, RuntimeProcessIdentity>();
            if (directProcessId > 0 && TryCaptureIdentity(directProcessId, parentProcessId: 0) is { } direct &&
                direct.StartedAtUtcTicks >= launchedAfterTicks &&
                !string.IsNullOrWhiteSpace(direct.ExecutablePath) &&
                ProcessTargetResolver.PathsEqual(direct.ExecutablePath, fullPath) &&
                !WasPresentBefore(direct, baseline))
                tracked[direct.Key] = direct;

            await DiscoverLaunchedProcessesAsync(fullPath, directProcessId, launchedAfterTicks,
                baseline, tracked, cancellationToken);

            if (tracked.Count == 0)
                return baseline.Count > 0
                    ? ActionExecutionResult.Skipped(
                        "Windows handed the request to the already running application; no new instance was identified for restore.")
                    : ActionExecutionResult.Failure(
                        "The program start request completed, but SwitchBoard could not verify a new process that can be closed safely.");

            var windowBehaviorResult = await ApplyWindowBehaviorAsync(action, target, cancellationToken);
            if (windowBehaviorResult is not null && !windowBehaviorResult.IsSuccessful) return windowBehaviorResult;
            var processSettingsResult = await ApplyPostLaunchProcessSettingsAsync(action, target, directProcessId, cancellationToken);
            if (processSettingsResult is not null) return processSettingsResult;
            var ordered = tracked.Values
                .OrderBy(identity => identity.StartedAtUtcTicks)
                .ThenBy(identity => identity.ProcessId)
                .ToList();
            var primary = ordered.FirstOrDefault(identity => identity.ProcessId == directProcessId) ?? ordered[0];
            return ActionExecutionResult.Success(
                $"Verified: identified {ordered.Count} new '{primary.ProcessName}' process(es) started by SwitchBoard.",
                new JsonObject
            {
                ["startedBySwitchBoard"] = true,
                ["processId"] = primary.ProcessId,
                ["startedAtUtcTicks"] = primary.StartedAtUtcTicks,
                ["executablePath"] = fullPath,
                ["processName"] = ProcessTargetResolver.NormalizeName(fullPath),
                ["captureAtUtcTicks"] = context.CapturedState?["captureAtUtcTicks"]?.GetValue<long>() ?? launchedAfterTicks,
                ["launchedProcesses"] = new JsonArray(ordered.Select(RuntimeProcessIdentityService.ToJson).ToArray())
            });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or
                                           FileNotFoundException or DirectoryNotFoundException)
        {
            return ActionExecutionResult.Failure($"Could not start '{target}': {exception.Message}");
        }
    }

    internal static ProcessStartInfo CreateStartInfo(ActionDefinition action, string target)
    {
        var arguments = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.Arguments);
        var isShellShortcut = IsShellShortcutTarget(target);
        var storedWorkingDirectory = ActionParameterReader.ReadString(
            action.Parameters, ActionParameterNames.WorkingDirectory).Trim();
        var useCustomWorkingDirectory = ActionParameterReader.ReadBoolean(
            action.Parameters, ActionParameterNames.UseCustomWorkingDirectory,
            !string.IsNullOrWhiteSpace(storedWorkingDirectory));
        // A shortcut carries its own arguments and working directory. Passing either here would
        // override what Windows Shell reads from the shortcut instead of matching Explorer's behavior.
        var workingDirectory = !isShellShortcut && useCustomWorkingDirectory ? storedWorkingDirectory : string.Empty;
        var useShellExecute = IsProtocolTarget(target) ||
                              isShellShortcut ||
                              ActionParameterReader.ReadBoolean(action.Parameters,
                                  ActionParameterNames.RunAsAdministrator, false);

        var startInfo = new ProcessStartInfo
        {
            FileName = target,
            Arguments = isShellShortcut ? string.Empty : arguments,
            UseShellExecute = useShellExecute
        };
        if (ActionParameterReader.ReadBoolean(action.Parameters, ActionParameterNames.RunAsAdministrator, false))
            startInfo.Verb = "runas";
        if (!string.IsNullOrWhiteSpace(workingDirectory))
            startInfo.WorkingDirectory = workingDirectory;
        else if (!useShellExecute && Path.IsPathRooted(target))
            startInfo.WorkingDirectory = Path.GetDirectoryName(target) ?? string.Empty;

        return startInfo;
    }

    internal static bool IsProtocolTarget(string target) =>
        Uri.TryCreate(target, UriKind.Absolute, out var uri) && !uri.IsFile &&
        !string.IsNullOrWhiteSpace(uri.Scheme);

    private static bool IsShellShortcutTarget(string target) =>
        string.Equals(Path.GetExtension(target), ".lnk", StringComparison.OrdinalIgnoreCase);

    private async Task<ActionExecutionResult?> ApplyPostLaunchProcessSettingsAsync(
        ActionDefinition action, string launchTarget, int directProcessId, CancellationToken cancellationToken)
    {
        var changeAffinity = ActionParameterReader.ReadBoolean(action.Parameters,
            ActionParameterNames.ChangeAffinity, false);
        var changePriority = ProcessSettingsService.ShouldChangeProcessPriority(action.Parameters);
        var changeMemoryPriority = ProcessSettingsService.IsConcreteMemoryPriority(
            ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.ProcessMemoryPriority));
        var changePerformanceMode = ProcessSettingsService.IsConcretePerformanceMode(
            ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.ProcessPerformanceMode));
        if (!changeAffinity && !changePriority && !changeMemoryPriority && !changePerformanceMode) return null;

        var targetMode = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.ProcessTargetMode);
        if (string.IsNullOrWhiteSpace(targetMode)) targetMode = ProcessTargetModeIds.Automatic;
        var processName = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.ProcessName).Trim();
        var executablePath = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.ExecutablePath).Trim();
        if (string.Equals(targetMode, ProcessTargetModeIds.Automatic, StringComparison.OrdinalIgnoreCase) && !IsProtocolTarget(launchTarget))
        {
            processName = ProcessTargetResolver.NormalizeName(launchTarget);
            executablePath = Path.IsPathRooted(launchTarget) &&
                             string.Equals(Path.GetExtension(launchTarget), ".exe", StringComparison.OrdinalIgnoreCase)
                ? Path.GetFullPath(launchTarget) : string.Empty;
        }
        else if (string.IsNullOrWhiteSpace(processName))
        {
            return ActionExecutionResult.Failure(Format("Result.PostLaunchTargetRequired",
                "A process target is required when manual selection is enabled."));
        }

        var wait = GetPostLaunchWait(action);
        using var process = await ProcessWaitService.WaitForStartAsync(processName, executablePath,
            string.Equals(targetMode, ProcessTargetModeIds.Automatic, StringComparison.OrdinalIgnoreCase) && !IsProtocolTarget(launchTarget)
                ? directProcessId : null,
            wait.MaximumWait, cancellationToken);
        if (process is null)
            return ActionExecutionResult.Failure(Format("Result.PostLaunchProcessNotFound",
                $"The post-launch process '{processName}' could not be found.", processName));
        try
        {
            _settingsService.Apply(process, action.Parameters);
            return ActionExecutionResult.Success(Format("Result.PostLaunchSettingsVerified",
                "Verified: the requested post-launch process settings are active."));
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or
                                           Win32Exception or NotSupportedException)
        {
            return ActionExecutionResult.Failure(Format("Result.PostLaunchSettingsFailed",
                $"Could not change post-launch process settings: {exception.Message}", exception.Message));
        }
    }

    private static (bool UseDirectProcessId, TimeSpan MaximumWait) GetPostLaunchWait(ActionDefinition action)
    {
        var settingsRequested = ActionParameterReader.ReadBoolean(action.Parameters,
            ActionParameterNames.ChangeAffinity, false) ||
            ProcessSettingsService.ShouldChangeProcessPriority(action.Parameters) ||
            ProcessSettingsService.IsConcreteMemoryPriority(ActionParameterReader.ReadString(action.Parameters,
                ActionParameterNames.ProcessMemoryPriority)) ||
            ProcessSettingsService.IsConcretePerformanceMode(ActionParameterReader.ReadString(action.Parameters,
                ActionParameterNames.ProcessPerformanceMode));
        if (!settingsRequested) return (false, TimeSpan.Zero);
        var enabled = ActionParameterReader.ReadBoolean(action.Parameters,
            ActionParameterNames.WaitForProcessStart, true);
        var seconds = Math.Clamp(ActionParameterReader.ReadInt32(action.Parameters,
            ActionParameterNames.ProcessStartWaitSeconds, 10), 1, 120);
        return (true, enabled ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero);
    }

    private static TimeSpan GetConfiguredProcessWait(ActionDefinition action)
    {
        var seconds = Math.Clamp(ActionParameterReader.ReadInt32(action.Parameters,
            ActionParameterNames.ProcessStartWaitSeconds, 10), 1, 120);
        return TimeSpan.FromSeconds(seconds);
    }

    private static Task PublishTrackingStateAsync(ActionExecutionContext context, JsonObject state,
        JsonArray launchedProcesses)
    {
        ProcessLaunchTracker.ApplyTrackingResult(state, launchedProcesses);
        return context.UpdateRestoreStateAsync?.Invoke(state) ?? Task.CompletedTask;
    }

    internal static bool IsAlreadyRunning(string target)
    {
        var processName = ProcessTargetResolver.NormalizeName(target);
        if (string.IsNullOrWhiteSpace(processName)) return false;

        var expectedPath = Path.IsPathRooted(target) &&
                           string.Equals(Path.GetExtension(target), ".exe", StringComparison.OrdinalIgnoreCase)
            ? Path.GetFullPath(target)
            : null;
        var lookup = ProcessTargetResolver.FindWithDiagnostics(processName, expectedPath);
        try
        {
            // Avoid starting a duplicate when a same-name process path cannot be inspected.
            return lookup.Processes.Count > 0 || lookup.InspectionFailures > 0;
        }
        finally { lookup.DisposeAll(); }
    }

    private static bool IsTargetAlreadyRunning(ActionDefinition action, string target)
    {
        if (!IsProtocolTarget(target)) return IsAlreadyRunning(target);
        var processName = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.ProcessName).Trim();
        if (string.IsNullOrWhiteSpace(processName)) return false;
        var executablePath = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.ExecutablePath).Trim();
        var lookup = ProcessTargetResolver.FindWithDiagnostics(processName, executablePath);
        if (lookup.ErrorMessage is not null)
        {
            lookup.DisposeAll();
            throw new InvalidOperationException(lookup.ErrorMessage);
        }
        try { return lookup.Processes.Count > 0 || lookup.InspectionFailures > 0; }
        finally { lookup.DisposeAll(); }
    }

    private static async Task StopExistingExactAsync(ActionDefinition action, string target,
        CancellationToken cancellationToken, IAppLogger? logger)
    {
        string processName;
        string? executablePath;
        if (IsProtocolTarget(target))
        {
            processName = ActionParameterReader.ReadString(action.Parameters,
                ActionParameterNames.ProcessName).Trim();
            if (string.IsNullOrWhiteSpace(processName))
                throw new InvalidOperationException(
                    "Restarting a URI requires the target process name to be configured.");

            var configuredPath = ActionParameterReader.ReadString(action.Parameters,
                ActionParameterNames.ExecutablePath).Trim();
            if (!string.IsNullOrWhiteSpace(configuredPath) &&
                (!Path.IsPathRooted(configuredPath) ||
                 !string.Equals(Path.GetExtension(configuredPath), ".exe", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(
                    "The optional URI process path must be a full path to an EXE.");
            executablePath = string.IsNullOrWhiteSpace(configuredPath)
                ? null : Path.GetFullPath(configuredPath);
        }
        else
        {
            if (!Path.IsPathRooted(target) ||
                !string.Equals(Path.GetExtension(target), ".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Restart requires a full path to an EXE so the existing process can be identified safely.");
            executablePath = Path.GetFullPath(target);
            processName = ProcessTargetResolver.NormalizeName(executablePath);
        }

        var lookup = ProcessTargetResolver.FindWithDiagnostics(processName, executablePath);
        if (lookup.ErrorMessage is not null)
        {
            lookup.DisposeAll();
            throw new InvalidOperationException(lookup.ErrorMessage);
        }
        if (lookup.InspectionFailures > 0)
        {
            lookup.DisposeAll();
            throw new InvalidOperationException(
                "Windows could not safely verify every same-name process against the configured executable path.");
        }
        try
        {
            foreach (var process in lookup.Processes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (process.HasExited) continue;
                    if (!ProcessTerminationGuard.TryPrepareForKill(process, processName, executablePath, logger,
                            "ProgramRun.RestartExisting", out var safetyError))
                        throw new InvalidOperationException(safetyError);
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(cancellationToken)
                        .WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
        }
        finally { lookup.DisposeAll(); }
    }

    private static async Task<ActionExecutionResult?> ApplyWindowBehaviorAsync(ActionDefinition action,
        string target, CancellationToken cancellationToken)
    {
        var behavior = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.WindowBehavior);
        if (string.IsNullOrWhiteSpace(behavior) || behavior == WindowBehaviorIds.None) return null;
        var processName = IsProtocolTarget(target)
            ? ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.ProcessName).Trim()
            : ProcessTargetResolver.NormalizeName(target);
        var path = IsProtocolTarget(target)
            ? ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.ExecutablePath).Trim()
            : Path.IsPathRooted(target) ? Path.GetFullPath(target) : null;
        if (string.IsNullOrWhiteSpace(processName))
            return ActionExecutionResult.Failure("A process target is required for window behavior.", false);
        var seconds = Math.Clamp(ActionParameterReader.ReadInt32(action.Parameters,
            ActionParameterNames.WindowWaitSeconds, 10), 1, 300);
        var found = await WindowBehaviorService.ApplyAsync(processName, path, behavior, seconds, cancellationToken);
        if (found) return null;
        return behavior == WindowBehaviorIds.Hide
            ? ActionExecutionResult.Success($"The program window was not found within {seconds} seconds; hide was skipped.")
            : ActionExecutionResult.Failure($"The program window did not appear within {seconds} seconds.");
    }

    public Task<JsonObject?> CaptureStateAsync(ActionDefinition action, ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.Target).Trim();
        if (IsProtocolTarget(target))
        {
            var processName = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.ProcessName).Trim();
            if (string.IsNullOrWhiteSpace(processName))
                throw new InvalidOperationException("A process target is required when close-on-restore is enabled.");
            var path = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.ExecutablePath).Trim();
            return Task.FromResult<JsonObject?>(ProcessLaunchTracker.Capture(processName, path));
        }
        var fullPath = ValidateRestorableTarget(target);
        var existing = CaptureMatchingProcesses(fullPath);
        return Task.FromResult<JsonObject?>(new JsonObject
        {
            ["wasRunningBefore"] = existing.Count > 0,
            ["executablePath"] = fullPath,
            ["processName"] = ProcessTargetResolver.NormalizeName(fullPath),
            ["captureAtUtcTicks"] = DateTime.UtcNow.Ticks,
            ["startedBySwitchBoard"] = false,
            ["preExistingProcesses"] = new JsonArray(existing.Select(RuntimeProcessIdentityService.ToJson).ToArray())
        });
    }

    public async Task<ActionExecutionResult> RestoreAsync(
        ActionDefinition action,
        JsonObject restoreState,
        ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var target = ActionParameterReader.ReadString(action.Parameters, ActionParameterNames.Target).Trim();
        if (IsProtocolTarget(target))
            return await ProcessLaunchTracker.CloseAsync(restoreState, cancellationToken, context.Logger ?? _logger);

        if (!(restoreState["startedBySwitchBoard"]?.GetValue<bool>() ?? false))
            return ActionExecutionResult.Skipped("SwitchBoard did not start an independently identifiable program instance.");

        var identities = ReadIdentities(restoreState["launchedProcesses"] as JsonArray);
        if (identities.Count == 0 && ReadLegacyIdentity(restoreState) is { } legacy)
            identities.Add(legacy);
        var processName = restoreState["processName"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(processName))
            processName = ProcessTargetResolver.NormalizeName(
                restoreState["executablePath"]?.GetValue<string>() ?? string.Empty);
        if (identities.Count == 0 || string.IsNullOrWhiteSpace(processName))
            return ActionExecutionResult.Failure("The saved program identity is incomplete.", false);

        var killed = 0;
        var failures = new List<string>();
        var timer = Stopwatch.StartNew();
        while (timer.Elapsed < RestoreTimeout)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidates = FindRestoreCandidates(identities);
            if (candidates.Count == 0)
                return ActionExecutionResult.Success(
                    Format("Result.ProgramRestoreSuccess",
                        "Verified: the program was closed. Closed {0} '{1}.exe' process(es); none remain.", killed, processName));
            foreach (var candidate in OrderChildrenFirst(candidates))
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    using var process = Process.GetProcessById(candidate.ProcessId);
                    var match = RuntimeProcessIdentityService.Match(process, candidate);
                    if (match == RuntimeProcessMatch.NoMatch) continue;
                    if (match == RuntimeProcessMatch.Unknown)
                    {
                        failures.Add($"PID {candidate.ProcessId}: Windows could not verify the saved process identity.");
                        continue;
                    }
                    if (!ProcessTerminationGuard.TryPrepareForKill(process, processName,
                            restoreState["executablePath"]?.GetValue<string>(), context.Logger ?? _logger,
                            "ProgramRun.Restore", out var safetyError))
                        return ActionExecutionResult.Failure(safetyError!, false);
                    process.Kill(entireProcessTree: true);
                    killed++;
                }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
                catch (Win32Exception exception) { failures.Add($"PID {candidate.ProcessId}: {exception.Message}"); }
            }
            await Task.Delay(200, cancellationToken);
        }

        var remaining = FindRestoreCandidates(identities);
        return ActionExecutionResult.Failure(
            Format("Result.ProgramRestoreFailed",
                "Could not close the program. {0} '{1}.exe' process(es) started by SwitchBoard remain.",
                remaining.Count, processName) +
            (failures.Count == 0 ? string.Empty : " Administrator privileges may be required. " + string.Join(" ", failures.Distinct())));
    }

    private string Format(string key, string fallback, params object?[] arguments) => _localization is null
        ? string.Format(System.Globalization.CultureInfo.CurrentCulture, fallback, arguments)
        : _localization.Format(key, arguments);

    private static List<RuntimeProcessIdentity> FindRestoreCandidates(
        IReadOnlyCollection<RuntimeProcessIdentity> originallyTracked)
    {
        var result = originallyTracked
            .Where(identity => RuntimeProcessIdentityService.GetLiveMatch(identity) != RuntimeProcessMatch.NoMatch)
            .ToList();
        var graph = CaptureProcessGraph();
        var activeTrackedProcessIds = originallyTracked
            .Where(identity => RuntimeProcessIdentityService.GetLiveMatch(identity) == RuntimeProcessMatch.Match)
            .Select(identity => identity.ProcessId)
            .ToHashSet();
        foreach (var entry in graph.Values)
        {
            var identity = TryCaptureIdentity(entry.ProcessId, entry.ParentProcessId);
            if (identity is null) continue;
            var wasTracked = originallyTracked.Any(tracked =>
                RuntimeProcessIdentityService.SameInstance(tracked, identity));
            var descendantOfTracked = IsDescendantOf(entry.ProcessId,
                activeTrackedProcessIds, graph);
            // Persisted tracked identities and their descendants are owned even when the child executable
            // has a different name from the launcher.
            if ((wasTracked || descendantOfTracked) &&
                result.All(candidate => candidate.Key != identity.Key))
                result.Add(identity);
        }
        return result;
    }

    private static bool IsDescendantOf(int processId, HashSet<int> ancestors,
        IReadOnlyDictionary<int, ProcessGraphEntry> graph)
    {
        var visited = new HashSet<int>();
        var current = processId;
        while (graph.TryGetValue(current, out var entry) && entry.ParentProcessId > 0 && visited.Add(current))
        {
            if (ancestors.Contains(entry.ParentProcessId)) return true;
            current = entry.ParentProcessId;
        }
        return false;
    }

    private static async Task DiscoverLaunchedProcessesAsync(
        string targetPath,
        int directProcessId,
        long launchedAfterTicks,
        IReadOnlyCollection<RuntimeProcessIdentity> baseline,
        IDictionary<RuntimeProcessIdentityKey, RuntimeProcessIdentity> tracked,
        CancellationToken cancellationToken)
    {
        var targetName = ProcessTargetResolver.NormalizeName(targetPath);
        var deadline = Stopwatch.StartNew();
        var unchangedPasses = 0;
        var lastCount = -1;
        while (deadline.Elapsed < DiscoveryWindow)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var graph = CaptureProcessGraph();
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var entry in graph.Values)
                {
                    if (tracked.Keys.Any(key => key.ProcessId == entry.ProcessId)) continue;
                    var isDirect = entry.ProcessId == directProcessId;
                    var parentIsTracked = tracked.Values.Any(identity =>
                        identity.ProcessId == entry.ParentProcessId &&
                        RuntimeProcessIdentityService.GetLiveMatch(identity) == RuntimeProcessMatch.Match);
                    var isNewMatchingTarget = ProcessTargetResolver.NamesEqual(entry.ExecutableName, targetName);
                    if (!isDirect && !parentIsTracked && !isNewMatchingTarget) continue;

                    var identity = TryCaptureIdentity(entry.ProcessId, entry.ParentProcessId);
                    if (identity is null || WasPresentBefore(identity, baseline)) continue;
                    if (isDirect && (identity.StartedAtUtcTicks < launchedAfterTicks ||
                                     string.IsNullOrWhiteSpace(identity.ExecutablePath) ||
                                     !ProcessTargetResolver.PathsEqual(identity.ExecutablePath, targetPath)))
                        continue;
                    if (!isDirect && !parentIsTracked)
                    {
                        if (identity.StartedAtUtcTicks < launchedAfterTicks ||
                            string.IsNullOrWhiteSpace(identity.ExecutablePath) ||
                            !ProcessTargetResolver.PathsEqual(identity.ExecutablePath, targetPath))
                            continue;
                    }
                    tracked[identity.Key] = identity;
                    changed = true;
                }
            }

            unchangedPasses = tracked.Count == lastCount ? unchangedPasses + 1 : 0;
            lastCount = tracked.Count;
            if (tracked.Count > 0 && unchangedPasses >= 5 && deadline.Elapsed >= TimeSpan.FromMilliseconds(650))
                break;
            await Task.Delay(DiscoveryInterval, cancellationToken);
        }
    }

    private static List<RuntimeProcessIdentity> CaptureMatchingProcesses(string fullPath)
    {
        var result = new List<RuntimeProcessIdentity>();
        var name = ProcessTargetResolver.NormalizeName(fullPath);
        var graph = CaptureProcessGraph();
        foreach (var entry in graph.Values.Where(entry =>
                     ProcessTargetResolver.NamesEqual(entry.ExecutableName, name)))
        {
            var identity = TryCaptureIdentity(entry.ProcessId, entry.ParentProcessId);
            if (identity is null) continue;
            // If path inspection is denied, include the same-name process in the baseline. This can only
            // prevent a close; it can never cause a pre-existing process to be killed.
            if (string.IsNullOrWhiteSpace(identity.ExecutablePath) ||
                ProcessTargetResolver.PathsEqual(identity.ExecutablePath, fullPath))
                result.Add(identity);
        }
        return result;
    }

    private static RuntimeProcessIdentity? TryCaptureIdentity(int processId, int parentProcessId) =>
        RuntimeProcessIdentityService.TryCapture(processId, parentProcessId);

    private static bool WasPresentBefore(RuntimeProcessIdentity identity,
        IReadOnlyCollection<RuntimeProcessIdentity> baseline) =>
        RuntimeProcessIdentityService.WasPresentBefore(identity, baseline);

    private static IEnumerable<RuntimeProcessIdentity> OrderChildrenFirst(
        IReadOnlyCollection<RuntimeProcessIdentity> identities)
    {
        var ids = identities.Select(identity => identity.ProcessId).ToHashSet();
        int Depth(RuntimeProcessIdentity identity)
        {
            var depth = 0;
            var parent = identity.ParentProcessId;
            var visited = new HashSet<int>();
            while (parent > 0 && ids.Contains(parent) && visited.Add(parent))
            {
                depth++;
                parent = identities.First(candidate => candidate.ProcessId == parent).ParentProcessId;
            }
            return depth;
        }
        return identities.OrderByDescending(Depth).ThenByDescending(identity => identity.StartedAtUtcTicks);
    }

    private static List<RuntimeProcessIdentity> ReadIdentities(JsonArray? array) =>
        RuntimeProcessIdentityService.ReadIdentities(array);

    private static RuntimeProcessIdentity? ReadLegacyIdentity(JsonObject state) =>
        RuntimeProcessIdentityService.ReadIdentity(state);

    private static string ValidateRestorableTarget(string target)
    {
        if (!Path.IsPathRooted(target) ||
            !string.Equals(Path.GetExtension(target), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Close-on-restore requires a full path to an executable file.");
        return Path.GetFullPath(target);
    }

    private static T? TryRead<T>(Func<T> read) where T : struct
    {
        try { return read(); }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) { return null; }
    }

    private static Dictionary<int, ProcessGraphEntry> CaptureProcessGraph()
    {
        var result = new Dictionary<int, ProcessGraphEntry>();
        var snapshot = CreateToolhelp32Snapshot(Th32CsSnapProcess, 0);
        if (snapshot == InvalidHandleValue) return result;
        try
        {
            var entry = new ProcessEntry32 { Size = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (!Process32First(snapshot, ref entry)) return result;
            do
            {
                var pid = unchecked((int)entry.ProcessId);
                if (pid > 0) result[pid] = new(pid, unchecked((int)entry.ParentProcessId), entry.ExecutableFile ?? string.Empty);
                entry.Size = (uint)Marshal.SizeOf<ProcessEntry32>();
            } while (Process32Next(snapshot, ref entry));
            return result;
        }
        finally
        {
            CloseHandle(snapshot);
        }
    }

    private sealed record ProcessGraphEntry(int ProcessId, int ParentProcessId, string ExecutableName);

    private const uint Th32CsSnapProcess = 0x00000002;
    private static readonly nint InvalidHandleValue = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public UIntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string? ExecutableFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(nint snapshot, ref ProcessEntry32 entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
