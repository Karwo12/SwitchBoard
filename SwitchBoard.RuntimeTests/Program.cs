using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text.Json.Serialization;
using SwitchBoard.Models.Actions;
using SwitchBoard.Models.Execution;
using SwitchBoard.Models.Profiles;
using SwitchBoard.Services.Execution;
using SwitchBoard.Services.Execution.Handlers;
using SwitchBoard.Services.Windows;
using SwitchBoard.Localization;
using SwitchBoard.ViewModels;
using SwitchBoard.Data;
using SwitchBoard.Services.Persistence;
using SwitchBoard.Services;
using SwitchBoard.Models.Categories;
using SwitchBoard.Services.Profiles;
using SwitchBoard.Themes;
using SwitchBoard.Services.Logging;
using SwitchBoard.Services.Activity;
using System.Windows.Media;
using System.Windows.Media.Imaging;

if (args is ["--program-run-tree-helper", var childPidPath])
{
    var helperPowerShellPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
    var childInfo = new ProcessStartInfo(helperPowerShellPath) { UseShellExecute = false };
    childInfo.ArgumentList.Add("-NoProfile");
    childInfo.ArgumentList.Add("-Command");
    childInfo.ArgumentList.Add("Start-Sleep -Seconds 60");
    using var child = Process.Start(childInfo);
    if (child is null) return 2;
    await File.WriteAllTextAsync(childPidPath, child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    await Task.Delay(1000);
    return 0;
}

if (args is ["--service-status", .. var serviceNames])
{
    var statusManager = new WindowsServiceManager();
    foreach (var serviceName in serviceNames)
    {
        try
        {
            var snapshot = await statusManager.GetSnapshotAsync(serviceName);
            Console.WriteLine($"{serviceName}|{snapshot.RuntimeState}|{snapshot.StartupType}");
        }
        catch (Exception exception) { Console.WriteLine($"{serviceName}|ERROR|{exception.GetType().Name}|{exception.Message}"); }
    }
    return 0;
}

if (args is ["--service-configuration-test", var configurationService, var runtimeTarget, var startupTarget])
{
    var configurationManager = new WindowsServiceManager();
    var before = await configurationManager.GetSnapshotAsync(configurationService);
    var result = await configurationManager.SetConfigurationAsync(configurationService, runtimeTarget,
        startupTarget, TimeSpan.FromSeconds(30));
    var after = await configurationManager.GetSnapshotAsync(configurationService);
    Console.WriteLine($"BEFORE_STATUS={before.RuntimeState}");
    Console.WriteLine($"BEFORE_STARTUP={before.StartupType}");
    Console.WriteLine($"RESULT_SUCCESS={result.IsSuccessful}");
    Console.WriteLine($"RESULT_SKIPPED={result.IsSkipped}");
    Console.WriteLine($"RESULT_ERROR={result.Win32Error?.ToString() ?? "none"}");
    Console.WriteLine($"RESULT_MESSAGE={result.Message}");
    Console.WriteLine($"AFTER_STATUS={after.RuntimeState}");
    Console.WriteLine($"AFTER_STARTUP={after.StartupType}");
    return 0;
}

if (args is ["--service-profile-test", var testedService, var testedDisplayName])
{
    var serviceTestManager = new WindowsServiceManager();
    var before = await serviceTestManager.GetSnapshotAsync(testedService);
    var serviceTestRoot = Path.Combine(Path.GetTempPath(), $"SwitchBoard-service-test-{Guid.NewGuid():N}");
    var serviceTestPaths = new AppDataPaths(serviceTestRoot);
    using var serviceTestRepository = new JsonExecutionSessionRepository(serviceTestPaths);
    var serviceTestActivity = new ActivityService(serviceTestPaths);
    var serviceTestRegistry = new ActionRegistry([
        new ServiceSetStateActionHandler(serviceTestManager), new DelayActionHandler()
    ]);
    var serviceAction = Action(ActionTypeIds.ServiceSetState, new JsonObject
    {
        [ActionParameterNames.ServiceName] = testedService,
        [ActionParameterNames.ServiceDisplayName] = testedDisplayName,
        [ActionParameterNames.DesiredState] = ServiceDesiredStateIds.Stopped,
        [ActionParameterNames.ServiceStartupType] = ServiceStartupTypeIds.Disabled
    });
    serviceAction.Name = testedDisplayName;
    serviceAction.RestoreBehavior = ActionRestoreBehavior.RestorePreviousState;
    var serviceProfile = new ProfileDefinition
    {
        CategoryId = Guid.NewGuid(), Name = $"{testedService} physical test", Actions = [serviceAction]
    };
    var execution = await new ProfileRunner(serviceTestRegistry, serviceTestRepository,
        activity: serviceTestActivity).RunAsync(serviceProfile);
    var pendingSession = await serviceTestRepository.GetLatestPendingAsync(serviceProfile.Id);
    var savedAction = (pendingSession ??
        throw new InvalidOperationException("No pending service session was created.")).Actions.Single();
    Console.WriteLine($"BEFORE_STATUS={before.RuntimeState}");
    Console.WriteLine($"BEFORE_STARTUP={before.StartupType}");
    var afterExecute = await serviceTestManager.GetSnapshotAsync(testedService);
    Console.WriteLine($"AFTER_EXECUTE_STATUS={afterExecute.RuntimeState}");
    Console.WriteLine($"AFTER_EXECUTE_STARTUP={afterExecute.StartupType}");
    Console.WriteLine($"EXECUTION_STATUS={execution.Status}");
    Console.WriteLine($"PREVIOUS_STATE={savedAction.PreviousState?["previousState"]?.GetValue<string>()}");
    Console.WriteLine($"PREVIOUS_STARTUP={savedAction.PreviousState?["previousStartupType"]?.GetValue<string>()}");
    Console.WriteLine($"REQUESTED_STATE={savedAction.RequestedState}");
    Console.WriteLine($"EXECUTION_ATTEMPTED={savedAction.ExecutionAttempted}");
    Console.WriteLine($"EXECUTION_VERIFIED={savedAction.ExecutionVerified}");
    Console.WriteLine($"RESTORE_REQUIRED={savedAction.RequiresRestore}");
    Console.WriteLine($"PENDING_COUNT={pendingSession.PendingRestoreCount}");
    Console.WriteLine($"SYSTEM_CHANGE_STATUS={serviceTestActivity.SystemChanges.Single().Status}");
    Console.WriteLine($"PENDING_NAMES={string.Join("|", pendingSession.GetPendingRestoreEntries().Select(item =>
        item.Parameters[ActionParameterNames.ServiceDisplayName]?.GetValue<string>() ?? item.ActionName ?? item.ActionType))}");
    var restored = await new ProfileRestoreRunner(serviceTestRegistry, serviceTestRepository,
        activity: serviceTestActivity).RunAsync(pendingSession);
    var afterRestore = await serviceTestManager.GetSnapshotAsync(testedService);
    Console.WriteLine($"AFTER_RESTORE_STATUS={afterRestore.RuntimeState}");
    Console.WriteLine($"AFTER_RESTORE_STARTUP={afterRestore.StartupType}");
    Console.WriteLine($"RESTORE_STATUS={restored.Status}");
    Console.WriteLine($"PENDING_AFTER_RESTORE={restored.PendingRestoreCount}");
    var restartedActivity = new ActivityService(serviceTestPaths);
    Console.WriteLine($"SYSTEM_CHANGE_AFTER_RESTART={restartedActivity.SystemChanges.Single().Status}");
    Console.WriteLine($"HISTORY_AFTER_RESTART={restartedActivity.HistoryEntries.Count}");
    return 0;
}

if (args is ["--service-discard-test", var discardService, var discardDisplayName])
{
    var discardManager = new WindowsServiceManager();
    var original = await discardManager.GetSnapshotAsync(discardService);
    var discardRoot = Path.Combine(Path.GetTempPath(), $"SwitchBoard-service-discard-{Guid.NewGuid():N}");
    var discardPaths = new AppDataPaths(discardRoot);
    try
    {
        using var discardRepository = new JsonExecutionSessionRepository(discardPaths);
        var discardActivity = new ActivityService(discardPaths);
        var discardRegistry = new ActionRegistry([new ServiceSetStateActionHandler(discardManager)]);
        var discardAction = Action(ActionTypeIds.ServiceSetState, new JsonObject
        {
            [ActionParameterNames.ServiceName] = discardService,
            [ActionParameterNames.ServiceDisplayName] = discardDisplayName,
            [ActionParameterNames.DesiredState] = ServiceDesiredStateIds.Stopped,
            [ActionParameterNames.ServiceStartupType] = ServiceStartupTypeIds.Disabled
        });
        discardAction.Name = discardDisplayName;
        discardAction.RestoreBehavior = ActionRestoreBehavior.RestorePreviousState;
        var discardProfile = new ProfileDefinition
        {
            CategoryId = Guid.NewGuid(), Name = $"{discardService} discard test", Actions = [discardAction]
        };
        await new ProfileRunner(discardRegistry, discardRepository, activity: discardActivity)
            .RunAsync(discardProfile);
        var discardSession = await discardRepository.GetLatestPendingAsync(discardProfile.Id) ??
                             throw new InvalidOperationException("Discard test did not create pending Restore.");
        var discarded = discardSession.DiscardPendingRestore();
        foreach (var item in discarded)
        {
            item.RestoreStatus = PersistentActionRestoreStatus.NotRequired;
            item.RestoreMessage = "Discarded by physical test.";
            discardActivity.Record(new PersistentActivityRecord
            {
                SessionId = discardSession.SessionId,
                ProfileId = discardSession.ProfileId,
                ProfileName = discardSession.ProfileName,
                ActionId = item.ActionId,
                ActionType = item.ActionType,
                FriendlyName = item.ActionName ?? discardDisplayName,
                EventType = ActivityEventTypes.Discard,
                Level = ActivityLevel.Warning,
                StateBefore = item.PreviousState?.DeepClone().AsObject(),
                StateAfter = item.StateAfter?.DeepClone().AsObject(),
                Result = "discarded",
                RestoreStatus = SystemChangeStatuses.Discarded,
                Message = $"{discardDisplayName}: restore discarded; change left in place."
            });
        }
        await discardRepository.SaveAsync(discardSession);
        var changed = await discardManager.GetSnapshotAsync(discardService);
        var reloaded = new ActivityService(discardPaths);
        Console.WriteLine($"BEFORE_STATUS={original.RuntimeState}");
        Console.WriteLine($"BEFORE_STARTUP={original.StartupType}");
        Console.WriteLine($"AFTER_DISCARD_STATUS={changed.RuntimeState}");
        Console.WriteLine($"AFTER_DISCARD_STARTUP={changed.StartupType}");
        Console.WriteLine($"PENDING_AFTER_DISCARD={discardSession.PendingRestoreCount}");
        Console.WriteLine($"RELOADED_CHANGE_STATUS={reloaded.SystemChanges.Single().Status}");
        Console.WriteLine($"RELOADED_HISTORY_COUNT={reloaded.HistoryEntries.Count}");
        Console.WriteLine($"JSONL_COUNT={Directory.EnumerateFiles(discardPaths.LogsDirectory, "activity-*.jsonl").Count()}");
    }
    finally
    {
        var runtime = original.RuntimeState == "Running"
            ? ServiceDesiredStateIds.Running
            : ServiceDesiredStateIds.Stopped;
        var startup = original.StartupType switch
        {
            "Automatic" => ServiceStartupTypeIds.Automatic,
            "Automatic (Delayed Start)" => ServiceStartupTypeIds.AutomaticDelayed,
            "Manual" => ServiceStartupTypeIds.Manual,
            "Disabled" => ServiceStartupTypeIds.Disabled,
            _ => ServiceStartupTypeIds.Unchanged
        };
        var cleanup = await discardManager.SetConfigurationAsync(discardService, runtime, startup,
            TimeSpan.FromSeconds(30));
        var final = await discardManager.GetSnapshotAsync(discardService);
        Console.WriteLine($"CLEANUP_SUCCESS={cleanup.IsSuccessful}");
        Console.WriteLine($"FINAL_STATUS={final.RuntimeState}");
        Console.WriteLine($"FINAL_STARTUP={final.StartupType}");
    }
    return 0;
}

var failures = new List<string>();

static ActionDefinition Action(string type, JsonObject parameters, ActionFailurePolicy failurePolicy = ActionFailurePolicy.Continue) =>
    new() { Type = type, Parameters = parameters, FailurePolicy = failurePolicy };

static void Check(bool condition, string name, List<string> failures)
{
    Console.WriteLine($"{(condition ? "PASS" : "FAIL")} {name}");
    if (!condition) failures.Add(name);
}

var serviceManager = new WindowsServiceManager();
var powerManager = new WindowsPowerPlanManager();
var displayManager = new WindowsDisplayManager();
var testRoot = Path.Combine(Path.GetTempPath(), $"SwitchBoard-runtime-{Guid.NewGuid():N}");
Directory.CreateDirectory(testRoot);
var testAppDataRoot = Path.Combine(testRoot, "appdata");
using var sessionRepository = new JsonExecutionSessionRepository(new AppDataPaths(testAppDataRoot));
var restoreOrder = new List<string>();
var reversibleHandler = new TestReversibleHandler(restoreOrder, sessionRepository);
var registry = new ActionRegistry(
[
    new ProgramRunActionHandler(),
    new ProcessSetStateActionHandler(),
    new ServiceSetStateActionHandler(serviceManager),
    new PowerSetPlanActionHandler(powerManager),
    new ScriptRunActionHandler(),
    new DelayActionHandler(),
    reversibleHandler
]);
var runner = new ProfileRunner(registry, sessionRepository);

try
{
    var outputPath = Path.Combine(testRoot, "script-output.txt");
    var successScript = Path.Combine(testRoot, "success.ps1");
    await File.WriteAllTextAsync(successScript, "param([string]$Value)\nSet-Content -LiteralPath $env:SB_TEST_OUTPUT -Value $Value\nexit 0\n");
    Environment.SetEnvironmentVariable("SB_TEST_OUTPUT", outputPath);
    var scriptSuccess = Action(ActionTypeIds.ScriptRun, new JsonObject
    {
        [ActionParameterNames.ScriptPath] = successScript,
        [ActionParameterNames.ScriptType] = ScriptTypeIds.AutoDetect,
        [ActionParameterNames.Arguments] = "\"argument with spaces\"",
        [ActionParameterNames.WorkingDirectory] = testRoot,
        [ActionParameterNames.WaitForExit] = true
    });
    var successResult = await new ScriptRunActionHandler().ExecuteAsync(scriptSuccess, new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
    Check(successResult.IsSuccessful && File.Exists(outputPath) && (await File.ReadAllTextAsync(outputPath)).Trim() == "argument with spaces",
        "script.run PowerShell wait/arguments/exit 0", failures);

    var batchOutputPath = Path.Combine(testRoot, "batch-output.txt");
    var batchScript = Path.Combine(testRoot, "success.cmd");
    await File.WriteAllTextAsync(batchScript, "@echo off\r\n>\"%SB_TEST_BATCH_OUTPUT%\" echo %~1\r\nexit /b 0\r\n");
    Environment.SetEnvironmentVariable("SB_TEST_BATCH_OUTPUT", batchOutputPath);
    var batchResult = await new ScriptRunActionHandler().ExecuteAsync(
        Action(ActionTypeIds.ScriptRun, new JsonObject
        {
            [ActionParameterNames.ScriptPath] = batchScript,
            [ActionParameterNames.ScriptType] = ScriptTypeIds.BatchCmd,
            [ActionParameterNames.Arguments] = "\"batch argument\"",
            [ActionParameterNames.WaitForExit] = true
        }), new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
    Check(batchResult.IsSuccessful && File.Exists(batchOutputPath) && (await File.ReadAllTextAsync(batchOutputPath)).Trim() == "batch argument",
        "script.run Batch/CMD quoting and exit 0", failures);

    var failedScript = Path.Combine(testRoot, "failure.ps1");
    await File.WriteAllTextAsync(failedScript, "exit 1\n");
    var profile = new ProfileDefinition
    {
        CategoryId = Guid.NewGuid(),
        Name = "Failure policy runtime test",
        Actions =
        [
            Action(ActionTypeIds.ScriptRun, new JsonObject
            {
                [ActionParameterNames.ScriptPath] = failedScript,
                [ActionParameterNames.ScriptType] = ScriptTypeIds.PowerShell,
                [ActionParameterNames.WaitForExit] = true
            }),
            Action(ActionTypeIds.Delay, new JsonObject { [ActionParameterNames.DelaySeconds] = 0 })
        ]
    };
    profile.Actions[0].SortOrder = 0;
    profile.Actions[1].SortOrder = 1;
    var failedSession = await runner.RunAsync(profile);
    Check(failedSession.Status == ExecutionSessionStatus.CompletedWithErrors &&
          failedSession.Journal[0].Status == ActionJournalStatus.Failed &&
          failedSession.Journal[1].Status == ActionJournalStatus.Success,
        "ProfileRunner continues and reports CompletedWithErrors", failures);

    var probePath = Path.Combine(testRoot, "sbwaitprobe.exe");
    File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), probePath);
    var waitStartHandler = new WaitProcessActionHandler(ActionTypeIds.WaitProcessStart);
    using (var waitStartCts = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
    {
        var waitAction = Action(ActionTypeIds.WaitProcessStart, new JsonObject
        { [ActionParameterNames.ProcessName] = "sbwaitprobe" });
        var waitTask = waitStartHandler.ExecuteAsync(waitAction, new(Guid.NewGuid(), Guid.NewGuid()), waitStartCts.Token);
        await Task.Delay(250, waitStartCts.Token);
        using var probe = Process.Start(new ProcessStartInfo(probePath)
        { UseShellExecute = false, Arguments = "/c ping -n 30 127.0.0.1 > nul" });
        var waitResult = await waitTask;
        Check(waitResult.IsSuccessful, "wait.processStart observes a process asynchronously", failures);
        if (probe is not null)
        {
            var waitExitHandler = new WaitProcessActionHandler(ActionTypeIds.WaitProcessExit);
            var exitTask = waitExitHandler.ExecuteAsync(Action(ActionTypeIds.WaitProcessExit, new JsonObject
                { [ActionParameterNames.ProcessName] = "sbwaitprobe" }), new(Guid.NewGuid(), Guid.NewGuid()), waitStartCts.Token);
            await Task.Delay(250, waitStartCts.Token);
            probe.Kill(entireProcessTree: true);
            var exitResult = await exitTask;
            Check(exitResult.IsSuccessful, "wait.processExit observes exact process-name disappearance", failures);
        }
    }

    using (var cancelWait = new CancellationTokenSource(180))
    {
        try
        {
            await waitStartHandler.ExecuteAsync(Action(ActionTypeIds.WaitProcessStart, new JsonObject
                { [ActionParameterNames.ProcessName] = $"missing-{Guid.NewGuid():N}" }),
                new(Guid.NewGuid(), Guid.NewGuid()), cancelWait.Token);
            Check(false, "wait.processStart cancellation", failures);
        }
        catch (OperationCanceledException) { Check(true, "wait.processStart cancellation", failures); }
    }

    using (var windowProcess = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true }))
    {
        if (windowProcess is not null)
        {
            using var windowCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var windowResult = await new WaitWindowActionHandler().ExecuteAsync(
                Action(ActionTypeIds.WaitWindow, new JsonObject
                {
                    [ActionParameterNames.ProcessName] = "notepad",
                    [ActionParameterNames.WindowMatchMode] = WindowMatchModeIds.Any
                }), new(Guid.NewGuid(), Guid.NewGuid()), windowCts.Token);
            Check(windowResult.IsSuccessful, "wait.window detects a visible main window", failures);
            try { if (!windowProcess.HasExited) windowProcess.Kill(entireProcessTree: true); } catch { }
        }
        else Console.WriteLine("SKIP wait.window: Notepad could not start.");
    }

    var affinityPath = Path.Combine(testRoot, "sbaffinity.exe");
    File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), affinityPath);
    using (var affinityProcess = Process.Start(new ProcessStartInfo(affinityPath)
    { UseShellExecute = false, Arguments = "/c ping -n 30 127.0.0.1 > nul" }))
    {
        if (affinityProcess is not null)
        {
            var affinityHandler = new ProcessConfigureActionHandler();
            var cpus = Enumerable.Range(0, Math.Min(Environment.ProcessorCount, IntPtr.Size * 8))
                .Where(cpu => cpu != 0 || Environment.ProcessorCount == 1).ToArray();
            var affinityAction = Action(ActionTypeIds.ProcessConfigure, new JsonObject
            {
                [ActionParameterNames.ProcessName] = "sbaffinity",
                [ActionParameterNames.ExecutablePath] = affinityPath,
                [ActionParameterNames.ChangeAffinity] = true,
                [ActionParameterNames.ChangePriority] = true,
                [ActionParameterNames.ProcessPriority] = ProcessPriorityIds.BelowNormal,
                [ActionParameterNames.CpuIndices] = new JsonArray(cpus.Select(cpu => (JsonNode?)JsonValue.Create(cpu)).ToArray())
            });
            affinityAction.RuntimeProcessIdHint = affinityProcess.Id;
            var affinityContext = new ActionExecutionContext(Guid.NewGuid(), Guid.NewGuid());
            var oldState = await affinityHandler.CaptureStateAsync(affinityAction, affinityContext, CancellationToken.None);
            var affinityResult = await affinityHandler.ExecuteAsync(affinityAction, affinityContext, CancellationToken.None);
            affinityProcess.Refresh();
            var expectedMask = unchecked((long)ProcessConfigureActionHandler.ReadAffinityMask(
                affinityAction.Parameters[ActionParameterNames.CpuIndices] as JsonArray));
            Check(affinityResult.IsSuccessful && affinityProcess.ProcessorAffinity.ToInt64() == expectedMask &&
                  affinityProcess.PriorityClass == ProcessPriorityClass.BelowNormal,
                "process.configure applies all-except-CPU0 affinity and priority", failures);
            if (oldState is not null)
            {
                await affinityHandler.RestoreAsync(affinityAction, oldState, affinityContext, CancellationToken.None);
                affinityProcess.Refresh();
                Check(affinityProcess.ProcessorAffinity.ToInt64() == oldState["affinityMask"]!.GetValue<long>(),
                    "process.configure restores previous affinity", failures);
            }
            try { affinityProcess.Kill(entireProcessTree: true); } catch { }
        }
    }

    var activity = new ActivityService();
    var flaky = new TestFlakyHandler();
    var automationRegistry = new ActionRegistry([
        new ProfileRunActionHandler(), new NotificationShowActionHandler(activity),
        new ConditionIfActionHandler(serviceManager),
        new WaitProcessActionHandler(ActionTypeIds.WaitProcessStart), new ProgramRunActionHandler(), flaky
    ]);
    var profileA = new ProfileDefinition { Name = "A", CategoryId = Guid.NewGuid() };
    profileA.Actions.Add(Action(ActionTypeIds.NotificationShow, new JsonObject
    {
        [ActionParameterNames.NotificationMessage] = "Notification A",
        [ActionParameterNames.NotificationLevel] = NotificationLevelIds.Info
    }));
    var profileB = new ProfileDefinition { Name = "B", CategoryId = profileA.CategoryId };
    profileB.Actions.Add(Action(ActionTypeIds.ProfileRun, new JsonObject
        { [ActionParameterNames.ProfileId] = profileA.Id.ToString("D") }));
    profileB.Actions.Add(Action(ActionTypeIds.NotificationShow, new JsonObject
    {
        [ActionParameterNames.NotificationMessage] = "Notification B",
        [ActionParameterNames.NotificationLevel] = NotificationLevelIds.Success
    }));
    profileB.Actions[0].SortOrder = 0; profileB.Actions[1].SortOrder = 1;
    var automationProfiles = new Dictionary<Guid, ProfileDefinition> { [profileA.Id] = profileA, [profileB.Id] = profileB };
    var automationRunner = new ProfileRunner(automationRegistry, sessionRepository, profileResolver: id => automationProfiles.GetValueOrDefault(id), activity: activity);
    var nestedSession = await automationRunner.RunAsync(profileB);
    var notificationMessages = activity.Entries.Where(entry => entry.Message.StartsWith("Notification ")).Select(entry => entry.Message).ToList();
    Check(nestedSession.Status == ExecutionSessionStatus.Completed &&
          notificationMessages.IndexOf("Notification A") < notificationMessages.IndexOf("Notification B") &&
          nestedSession.Journal.Any(item => item.ParentActionId == profileB.Actions[0].Id),
        "profile.run executes nested profile in order and journals parent action", failures);
    Check(activity.Entries.Any(entry => entry.Message == "Profile started: B") &&
          activity.Entries.Any(entry => entry.Message == "Profile started: A") &&
          activity.Entries.Any(entry => entry.Message.StartsWith("Action: ")) &&
          activity.Entries.Any(entry => entry.Message == "Profile completed: B"),
        "Activity identifies profile and action execution events", failures);

    profileA.Actions.Clear();
    profileA.Actions.Add(Action(ActionTypeIds.ProfileRun, new JsonObject
        { [ActionParameterNames.ProfileId] = profileB.Id.ToString("D") }));
    var cycleSession = await automationRunner.RunAsync(profileA);
    Check(cycleSession.Status == ExecutionSessionStatus.CompletedWithErrors &&
          cycleSession.Journal.Any(item => item.Status == ActionJournalStatus.Failed),
        "profile.run detects A-B-A cycle without recursion", failures);

    var thenAction = Action(ActionTypeIds.NotificationShow, new JsonObject
    { [ActionParameterNames.NotificationMessage] = "running", [ActionParameterNames.NotificationLevel] = NotificationLevelIds.Info });
    var elseAction = Action(ActionTypeIds.NotificationShow, new JsonObject
    { [ActionParameterNames.NotificationMessage] = "not running", [ActionParameterNames.NotificationLevel] = NotificationLevelIds.Info });
    var ifAction = Action(ActionTypeIds.ConditionIf, new JsonObject
    {
        [ActionParameterNames.ConditionType] = ConditionTypeIds.ProcessNotRunning,
        [ActionParameterNames.ConditionValue] = $"missing-{Guid.NewGuid():N}",
        [ActionParameterNames.ThenActions] = new JsonArray(JsonSerializer.SerializeToNode(thenAction)),
        [ActionParameterNames.ElseActions] = new JsonArray(JsonSerializer.SerializeToNode(elseAction))
    });
    var ifProfile = new ProfileDefinition { Name = "IF", CategoryId = Guid.NewGuid(), Actions = [ifAction] };
    automationProfiles[ifProfile.Id] = ifProfile;
    var ifSession = await automationRunner.RunAsync(ifProfile);
    Check(ifSession.Status == ExecutionSessionStatus.Completed && ifSession.Journal.Any(item => item.Branch == "then") &&
          activity.Entries.Any(entry => entry.Message == "running"), "condition.if executes only the selected branch", failures);

    ifAction.Parameters[ActionParameterNames.ConditionType] = ConditionTypeIds.ProcessRunning;
    var elseSession = await automationRunner.RunAsync(ifProfile);
    Check(elseSession.Status == ExecutionSessionStatus.Completed && elseSession.Journal.Any(item => item.Branch == "else") &&
          activity.Entries.Any(entry => entry.Message == "not running"), "condition.if executes ELSE when the condition is false", failures);

    var ifProgramOutput = Path.Combine(testRoot, "if-program.txt");
    var nestedProgram = Action(ActionTypeIds.ProgramRun, new JsonObject
    {
        [ActionParameterNames.Target] = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
        [ActionParameterNames.Arguments] = $"/c echo nested> \"{ifProgramOutput}\"",
        [ActionParameterNames.InstanceBehavior] = InstanceBehaviorIds.StartAnother
    });
    ifAction.Parameters[ActionParameterNames.ConditionType] = ConditionTypeIds.FileNotExists;
    ifAction.Parameters[ActionParameterNames.ConditionValue] = ifProgramOutput;
    ifAction.Parameters[ActionParameterNames.ThenActions] = new JsonArray(JsonSerializer.SerializeToNode(nestedProgram));
    var ifProgramSession = await automationRunner.RunAsync(ifProfile);
    for (var wait = 0; wait < 30 && !File.Exists(ifProgramOutput); wait++) await Task.Delay(50);
    Check(ifProgramSession.Status == ExecutionSessionStatus.Completed && File.Exists(ifProgramOutput),
        "condition.if executes a normal nested program.run action", failures);

    var retryProfile = new ProfileDefinition { Name = "Retry", CategoryId = Guid.NewGuid(), Actions =
    [new ActionDefinition { Type = TestFlakyHandler.TypeId, RetryOnFailure = true, MaximumAttempts = 3,
        RetryDelay = TimeSpan.FromMilliseconds(20), FailurePolicy = ActionFailurePolicy.Stop }] };
    var retrySession = await automationRunner.RunAsync(retryProfile);
    Check(retrySession.Status == ExecutionSessionStatus.Completed && flaky.Attempts == 3 &&
          retrySession.Journal.Single().AttemptCount == 3, "central retry fails twice and succeeds on attempt three", failures);

    var exhaustedProfile = new ProfileDefinition { Name = "Retry exhausted", CategoryId = Guid.NewGuid(), Actions =
    [new ActionDefinition { Type = TestFlakyHandler.TypeId, RetryOnFailure = true, MaximumAttempts = 3,
        RetryDelay = TimeSpan.FromMilliseconds(10), FailurePolicy = ActionFailurePolicy.Stop,
        Parameters = new JsonObject { ["failAlways"] = true } }] };
    var attemptsBefore = flaky.Attempts;
    var exhaustedSession = await automationRunner.RunAsync(exhaustedProfile);
    Check(exhaustedSession.Status == ExecutionSessionStatus.Failed && flaky.Attempts - attemptsBefore == 3,
        "central retry exhausts attempts before applying Stop profile", failures);

    var timeoutProfile = new ProfileDefinition { Name = "Timeout", CategoryId = Guid.NewGuid(), Actions =
    [new ActionDefinition { Type = ActionTypeIds.WaitProcessStart, Timeout = TimeSpan.FromMilliseconds(250),
        FailurePolicy = ActionFailurePolicy.Stop, Parameters = new JsonObject
        { [ActionParameterNames.ProcessName] = $"missing-{Guid.NewGuid():N}" } }] };
    var timeoutSession = await automationRunner.RunAsync(timeoutProfile);
    Check(timeoutSession.Status == ExecutionSessionStatus.Failed &&
          timeoutSession.Journal.Single().ErrorMessage?.Contains("timed out", StringComparison.OrdinalIgnoreCase) == true,
        "wait action timeout is enforced by the central pipeline", failures);

    for (var index = 0; index < 600; index++) activity.Add(ActivityLevel.Info, $"event {index}");
    Check(activity.Entries.Count == 300 && activity.Entries[0].Message == "event 300",
        "Activity keeps a bounded 300-entry session buffer", failures);
    activity.Clear();
    Check(activity.Entries.Count == 0, "Activity Clear removes the user-facing session history", failures);

    var audioManager = new WindowsAudioManager();
    try
    {
        var audioDevices = await audioManager.GetDevicesAsync();
        Check(audioDevices.All(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.FriendlyName)),
            "audio device discovery returns friendly names and stable IDs", failures);
        if (audioDevices.Count == 0) Console.WriteLine("SKIP audio endpoint verification: VM exposes no compatible Core Audio endpoints.");
        if (audioDevices.Any(item => !item.IsInput && item.IsDefaultMultimedia))
        {
            var currentVolume = await audioManager.GetMasterVolumeAsync();
            var testVolume = currentVolume.Volume > 0.02f ? currentVolume.Volume - 0.01f : currentVolume.Volume + 0.01f;
            await audioManager.SetMasterVolumeAsync(testVolume, currentVolume.Muted);
            var changedVolume = await audioManager.GetMasterVolumeAsync();
            await audioManager.SetMasterVolumeAsync(currentVolume.Volume, currentVolume.Muted);
            Check(Math.Abs(changedVolume.Volume - testVolume) < 0.02f,
                "audio master volume changes safely and restores", failures);
        }
        else Console.WriteLine("SKIP audio volume change: VM exposes no default output endpoint.");
    }
    catch (Exception exception) { Console.WriteLine($"LIMIT audio discovery/control unavailable: {exception.Message}"); }

    try
    {
        var devices = await new WindowsDeviceManager().GetDevicesAsync();
        Check(devices.Count > 0 && devices.All(item => !string.IsNullOrWhiteSpace(item.InstanceId)),
            "Windows device discovery returns instance IDs", failures);
        Check(devices.Where(item => item.DeviceClass is "System" or "DiskDrive" or "Display").All(item => item.IsCritical),
            "critical Windows device classes are protected", failures);
    }
    catch (Exception exception) { Console.WriteLine($"LIMIT Windows device discovery unavailable: {exception.Message}"); }

    var reversibleProfile = new ProfileDefinition
    {
        CategoryId = Guid.NewGuid(), Name = "Persistent restore test",
        Actions =
        [
            Action(TestReversibleHandler.TypeId, new JsonObject { ["key"] = "first" }),
            Action(TestReversibleHandler.TypeId, new JsonObject { ["key"] = "second" })
        ]
    };
    for (var index = 0; index < reversibleProfile.Actions.Count; index++)
    {
        reversibleProfile.Actions[index].SortOrder = index;
        reversibleProfile.Actions[index].RestoreBehavior = ActionRestoreBehavior.RestorePreviousState;
    }
    var reversibleSession = await runner.RunAsync(reversibleProfile);
    var pending = await new JsonExecutionSessionRepository(new AppDataPaths(Path.Combine(testRoot, "appdata")))
        .GetLatestPendingAsync(reversibleProfile.Id);
    Check(reversibleSession.Status == ExecutionSessionStatus.Completed && pending?.PendingRestoreCount == 2,
        "session survives repository restart with two pending changes", failures);
    Check(reversibleHandler.CaptureWasPersistedBeforeExecute,
        "CaptureState is persisted before Execute", failures);
    if (pending is not null)
    {
        var restoreActivity = new ActivityService();
        await new ProfileRestoreRunner(registry, sessionRepository, activity: restoreActivity).RunAsync(pending);
        Check(restoreOrder.SequenceEqual(["second", "first"]), "Restore runs in reverse action order", failures);
        var restored = await sessionRepository.LoadAsync(pending.SessionId);
        Check(restored?.Status == PersistentSessionStatus.Restored && restored.PendingRestoreCount == 0,
            "successful Restore atomically clears pending actions", failures);
        Check(restoreActivity.Entries.Any(entry => entry.Message == "Restoring profile: Persistent restore test") &&
              restoreActivity.Entries.Count(entry => entry.Message.StartsWith("Restoring action: ")) == 2 &&
              restoreActivity.Entries.Count(entry => entry.Message.StartsWith("Action restored: ")) == 2 &&
              restoreActivity.Entries.Any(entry => entry.Message == "Profile restored: Persistent restore test"),
            "Activity reports profile and action restore events", failures);
    }

    var partialProfile = new ProfileDefinition
    {
        CategoryId = Guid.NewGuid(), Name = "Partial restore test",
        Actions =
        [
            Action(TestReversibleHandler.TypeId, new JsonObject { ["key"] = "partial-first", ["failOnce"] = true }),
            Action(TestReversibleHandler.TypeId, new JsonObject { ["key"] = "partial-second" })
        ]
    };
    for (var index = 0; index < partialProfile.Actions.Count; index++)
    {
        partialProfile.Actions[index].SortOrder = index;
        partialProfile.Actions[index].RestoreBehavior = ActionRestoreBehavior.RestorePreviousState;
    }
    await runner.RunAsync(partialProfile);
    var partial = await sessionRepository.GetLatestPendingAsync(partialProfile.Id);
    if (partial is not null)
    {
        var restoreRunner = new ProfileRestoreRunner(registry, sessionRepository);
        partial = await restoreRunner.RunAsync(partial);
        Check(partial.Status == PersistentSessionStatus.PartiallyRestored && partial.PendingRestoreCount == 1,
            "partial Restore keeps only failed action pending", failures);
        partial = await restoreRunner.RunAsync(partial);
        Check(partial.Status == PersistentSessionStatus.Restored &&
              reversibleHandler.RestoreAttempts["partial-second"] == 1 &&
              reversibleHandler.RestoreAttempts["partial-first"] == 2,
            "Restore retry skips actions already restored", failures);
    }

    var combinedServiceRoot = Path.Combine(testRoot, "service-combined");
    var combinedPaths = new AppDataPaths(combinedServiceRoot);
    var combinedServiceManager = new TestServiceManager(ServiceDesiredStateIds.Running, changeSucceeds: true);
    using (var combinedRepository = new JsonExecutionSessionRepository(combinedPaths))
    {
        var combinedActivity = new ActivityService(combinedPaths);
        var combinedRegistry = new ActionRegistry([new ServiceSetStateActionHandler(combinedServiceManager)]);
        var combinedAction = Action(ActionTypeIds.ServiceSetState, new JsonObject
        {
            [ActionParameterNames.ServiceName] = "Spooler",
            [ActionParameterNames.ServiceDisplayName] = "Print Spooler",
            [ActionParameterNames.DesiredState] = ServiceDesiredStateIds.Stopped,
            [ActionParameterNames.ServiceStartupType] = ServiceStartupTypeIds.Disabled
        });
        combinedAction.Name = "Print Spooler";
        combinedAction.RestoreBehavior = ActionRestoreBehavior.RestorePreviousState;
        var combinedProfile = new ProfileDefinition
        {
            CategoryId = Guid.NewGuid(), Name = "Combined service configuration", Actions = [combinedAction]
        };
        await new ProfileRunner(combinedRegistry, combinedRepository, activity: combinedActivity)
            .RunAsync(combinedProfile);
        var combinedPending = await combinedRepository.GetLatestPendingAsync(combinedProfile.Id);
        var combinedSaved = combinedPending?.Actions.Single();
        Check(combinedServiceManager.Snapshot == new WindowsServiceSnapshot("Stopped", "Disabled") &&
              combinedSaved?.PreviousState?["previousState"]?.GetValue<string>() == ServiceDesiredStateIds.Running &&
              combinedSaved.PreviousState?["previousStartupType"]?.GetValue<string>() == ServiceStartupTypeIds.Automatic &&
              combinedSaved.RequiresRestore && combinedPending?.PendingRestoreCount == 1,
            "service status and startup type are captured, verified and pending Restore", failures);

        var reloadedActivity = new ActivityService(combinedPaths);
        Check(reloadedActivity.SystemChanges.Count == 1 &&
              reloadedActivity.SystemChanges[0].Status == SystemChangeStatuses.Pending &&
              reloadedActivity.HistoryEntries.Count > 0 &&
              Directory.EnumerateFiles(combinedPaths.LogsDirectory, "activity-*.jsonl").Any(),
            "persistent Activity JSONL reloads pending system changes after restart", failures);

        if (combinedPending is not null)
        {
            await new ProfileRestoreRunner(combinedRegistry, combinedRepository, activity: combinedActivity)
                .RunAsync(combinedPending);
            Check(combinedServiceManager.Snapshot == new WindowsServiceSnapshot("Running", "Automatic") &&
                  combinedActivity.SystemChanges.Single().Status == SystemChangeStatuses.Restored,
                "service Restore verifies runtime status and startup type", failures);
        }

        await new ProfileRunner(combinedRegistry, combinedRepository, activity: combinedActivity)
            .RunAsync(combinedProfile);
        var discardSession = await combinedRepository.GetLatestPendingAsync(combinedProfile.Id);
        if (discardSession is not null)
        {
            foreach (var item in discardSession.GetPendingRestoreEntries())
            {
                combinedActivity.Record(new PersistentActivityRecord
                {
                    SessionId = discardSession.SessionId,
                    ProfileId = discardSession.ProfileId,
                    ProfileName = discardSession.ProfileName,
                    ActionId = item.ActionId,
                    ActionType = item.ActionType,
                    FriendlyName = item.ActionName ?? item.ActionType,
                    EventType = ActivityEventTypes.Discard,
                    Level = ActivityLevel.Warning,
                    StateBefore = item.PreviousState?.DeepClone().AsObject(),
                    StateAfter = item.StateAfter?.DeepClone().AsObject(),
                    RestoreStatus = SystemChangeStatuses.Discarded,
                    Result = "discarded",
                    Message = "Restore discarded by persistence test."
                });
            }
            discardSession.Status = PersistentSessionStatus.Discarded;
            await combinedRepository.SaveAsync(discardSession);
            var afterRestart = new ActivityService(combinedPaths);
            Check(combinedServiceManager.Snapshot == new WindowsServiceSnapshot("Stopped", "Disabled") &&
                  afterRestart.SystemChanges.Any(change => change.SessionId == discardSession.SessionId &&
                      change.Status == SystemChangeStatuses.Discarded),
                "Discard leaves the service changed and persists System Changes across restart", failures);
            await combinedServiceManager.SetConfigurationAsync("Spooler", ServiceDesiredStateIds.Running,
                ServiceStartupTypeIds.Automatic, TimeSpan.FromSeconds(1));
        }
    }

    var resolvedRetentionPaths = new AppDataPaths(Path.Combine(testRoot, "activity-retention-resolved"));
    var resolvedRetentionActivity = new ActivityService(resolvedRetentionPaths);
    resolvedRetentionActivity.Record(new PersistentActivityRecord
    {
        Timestamp = DateTimeOffset.UtcNow.AddDays(-100),
        SessionId = Guid.NewGuid(), ProfileId = Guid.NewGuid(), ActionId = Guid.NewGuid(),
        ActionType = ActionTypeIds.PowerSetPlan, FriendlyName = "Old resolved change",
        EventType = ActivityEventTypes.Restore, Level = ActivityLevel.Success,
        RestoreStatus = SystemChangeStatuses.Restored, Message = "Old change restored."
    });
    _ = new ActivityService(resolvedRetentionPaths);
    Check(!Directory.EnumerateFiles(resolvedRetentionPaths.LogsDirectory, "activity-*.jsonl").Any(),
        "Activity retention removes resolved history older than 90 days", failures);

    var pendingRetentionPaths = new AppDataPaths(Path.Combine(testRoot, "activity-retention-pending"));
    var pendingRetentionActivity = new ActivityService(pendingRetentionPaths);
    pendingRetentionActivity.Record(new PersistentActivityRecord
    {
        Timestamp = DateTimeOffset.UtcNow.AddDays(-100),
        SessionId = Guid.NewGuid(), ProfileId = Guid.NewGuid(), ActionId = Guid.NewGuid(),
        ActionType = ActionTypeIds.PowerSetPlan, FriendlyName = "Old pending change",
        EventType = ActivityEventTypes.Verify, Level = ActivityLevel.Success,
        StateBefore = new JsonObject { ["previousPowerPlanGuid"] = Guid.NewGuid().ToString() },
        RestoreStatus = SystemChangeStatuses.Pending, Message = "Old change still pending."
    });
    _ = new ActivityService(pendingRetentionPaths);
    Check(Directory.EnumerateFiles(pendingRetentionPaths.LogsDirectory, "activity-*.jsonl").Any(),
        "Activity retention preserves unresolved changes older than 90 days", failures);

    var unchangedServiceManager = new TestServiceManager(ServiceDesiredStateIds.Running, changeSucceeds: false);
    using (var serviceJournalRepository = new JsonExecutionSessionRepository(
               new AppDataPaths(Path.Combine(testRoot, "service-journal"))))
    {
        var serviceJournalRegistry = new ActionRegistry([
            new ServiceSetStateActionHandler(unchangedServiceManager)
        ]);
        var failedServiceAction = Action(ActionTypeIds.ServiceSetState, new JsonObject
        {
            [ActionParameterNames.ServiceName] = "TestSvc",
            [ActionParameterNames.ServiceDisplayName] = "Test service",
            [ActionParameterNames.DesiredState] = ServiceDesiredStateIds.Stopped
        });
        failedServiceAction.RestoreBehavior = ActionRestoreBehavior.RestorePreviousState;
        var failedServiceProfile = new ProfileDefinition
        {
            CategoryId = Guid.NewGuid(), Name = "Service no-change failure", Actions = [failedServiceAction]
        };
        var failedServiceExecution = await new ProfileRunner(serviceJournalRegistry, serviceJournalRepository)
            .RunAsync(failedServiceProfile);
        var failedServiceSaved = await serviceJournalRepository.LoadAsync(failedServiceExecution.Id);
        Check(failedServiceSaved?.Actions.Single().ExecutionAttempted == true &&
              failedServiceSaved.Actions.Single().ExecutionVerified &&
              !failedServiceSaved.Actions.Single().RequiresRestore &&
              failedServiceSaved.PendingRestoreCount == 0,
            "service failure without observed state change does not create pending Restore", failures);
    }

    var cancelledProfile = new ProfileDefinition
    {
        CategoryId = Guid.NewGuid(), Name = "Cancelled restore test",
        Actions =
        [
            Action(TestReversibleHandler.TypeId, new JsonObject { ["key"] = "cancel-first" }),
            Action(TestReversibleHandler.TypeId, new JsonObject { ["key"] = "cancel-slow", ["restoreDelayMs"] = 2000 }),
            Action(TestReversibleHandler.TypeId, new JsonObject { ["key"] = "cancel-last" })
        ]
    };
    for (var index = 0; index < cancelledProfile.Actions.Count; index++)
    {
        cancelledProfile.Actions[index].SortOrder = index;
        cancelledProfile.Actions[index].RestoreBehavior = ActionRestoreBehavior.RestorePreviousState;
    }
    await runner.RunAsync(cancelledProfile);
    var cancellable = await sessionRepository.GetLatestPendingAsync(cancelledProfile.Id);
    if (cancellable is not null)
    {
        using var restoreCancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(350));
        try
        {
            await new ProfileRestoreRunner(registry, sessionRepository).RunAsync(cancellable,
                cancellationToken: restoreCancellation.Token);
            failures.Add("Restore cancellation should throw OperationCanceledException");
        }
        catch (OperationCanceledException) { }
        var afterCancel = await sessionRepository.LoadAsync(cancellable.SessionId);
        Check(afterCancel?.Status == PersistentSessionStatus.RestoreCancelled &&
              afterCancel.PendingRestoreCount == 2 &&
              afterCancel.Actions.Single(item => item.PreviousState?["key"]?.GetValue<string>() == "cancel-last").IsRestored,
            "Restore cancellation preserves completed restores and keeps remaining actions pending", failures);
    }

    var recoverySession = new PersistentExecutionSession
    {
        ProfileId = Guid.NewGuid(), ProfileName = "Interrupted", Status = PersistentSessionStatus.Executing,
        Actions = [new PersistentSessionAction
        {
            ActionId = Guid.NewGuid(), ActionType = TestReversibleHandler.TypeId, RequiresRestore = true,
            PreviousState = new JsonObject { ["key"] = "recovery" }, RestoreStatus = PersistentActionRestoreStatus.Pending
        }]
    };
    await sessionRepository.SaveAsync(recoverySession);
    await sessionRepository.MaintainAsync(TimeSpan.FromDays(30));
    var recovered = await sessionRepository.LoadAsync(recoverySession.SessionId);
    Check(recovered?.Status == PersistentSessionStatus.RecoveryRequired && recovered.PendingRestoreCount == 1,
        "startup recovery preserves interrupted pending state", failures);

    var discardedActions = recovered!.DiscardPendingRestore();
    discardedActions[0].RestoreMessage = "discarded by test";
    await sessionRepository.SaveAsync(recovered);
    var discardedReloaded = await sessionRepository.LoadAsync(recovered.SessionId);
    Check(discardedActions.Count == 1 && discardedReloaded?.Status == PersistentSessionStatus.Discarded &&
          discardedReloaded.PendingRestoreCount == 0 &&
          await sessionRepository.GetLatestPendingAsync(recovered.ProfileId) is null,
        "discard pending Restore keeps JSON audit record and suppresses future Restore", failures);

    var counterSession = new PersistentExecutionSession
    {
        ProfileId = Guid.NewGuid(), ProfileName = "Restore counter selector",
        Status = PersistentSessionStatus.RestorePending,
        Actions =
        [
            new PersistentSessionAction { ActionType = ActionTypeIds.ProgramRun, ActionName = "Microsoft Edge",
                RequiresRestore = true, ExecutionAttempted = true, ExecutionVerified = true },
            new PersistentSessionAction { ActionType = ActionTypeIds.ProcessSetState, ActionName = "Notatnik",
                RequiresRestore = true, ExecutionAttempted = true, ExecutionVerified = true },
            new PersistentSessionAction { ActionType = ActionTypeIds.ServiceSetState, ActionName = "Bufor wydruku",
                RequiresRestore = true, ExecutionAttempted = true, ExecutionVerified = true,
                Parameters = new JsonObject { [ActionParameterNames.ServiceName] = "Spooler",
                    [ActionParameterNames.ServiceDisplayName] = "Bufor wydruku" } },
            new PersistentSessionAction { ActionType = ActionTypeIds.ServiceSetState, ActionName = "Skipped service",
                RequiresRestore = false, ExecutionAttempted = true, ExecutionVerified = true }
        ]
    };
    Check(counterSession.PendingRestoreCount == 3 &&
          counterSession.GetPendingRestoreEntries().Select(item => item.ActionName)
              .SequenceEqual(["Microsoft Edge", "Notatnik", "Bufor wydruku"]),
        "Restore counter and preview selector include exactly three verified changed actions", failures);

    var oldRestored = new PersistentExecutionSession
    {
        ProfileId = Guid.NewGuid(), ProfileName = "Old restored", Status = PersistentSessionStatus.Restored
    };
    await sessionRepository.SaveAsync(oldRestored);
    var oldPath = Path.Combine(testAppDataRoot, "sessions", $"{oldRestored.SessionId:N}.json");
    var oldJson = JsonNode.Parse(await File.ReadAllTextAsync(oldPath))!.AsObject();
    oldJson["updatedAt"] = DateTimeOffset.UtcNow.AddDays(-40).ToString("O");
    await File.WriteAllTextAsync(oldPath, oldJson.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    await sessionRepository.MaintainAsync(TimeSpan.FromDays(30));
    Check(!File.Exists(oldPath), "session cleanup removes only old fully restored sessions", failures);

    var settingsRepository = new JsonSettingsRepository(new AppDataPaths(Path.Combine(testRoot, "settings-appdata")));
    var persistedTheme = new CustomThemeDefinition { Name = "First custom theme" };
    persistedTheme.Colors.Accent = "#FF123456";
    persistedTheme.Colors.SecondaryButtonBackground = "#FF223344";
    persistedTheme.Colors.MenuBackground = "#FF334455";
    persistedTheme.Colors.MenuHoverBackground = "#FF445566";
    persistedTheme.Colors.SurfaceOpacity = 0.72;
    persistedTheme.Colors.CategoriesPanelOpacity = 0.51;
    persistedTheme.Colors.ProfilesPanelOpacity = 0.63;
    persistedTheme.Colors.ProfileEditorPanelOpacity = 0.84;
    persistedTheme.Colors.ActivityPanelOpacity = 0.47;
    persistedTheme.Colors.BackgroundAssetFileName = "background-test.gif";
    var customSettings = new UserSettings
    {
        ThemeId = persistedTheme.Id, LanguageId = "pl", CustomThemes = [persistedTheme]
    };
    await settingsRepository.SaveAsync(customSettings);
    var customReloaded = await settingsRepository.LoadAsync();
    Check(customReloaded.ThemeId == persistedTheme.Id && customReloaded.CustomThemes.Count == 1 &&
          customReloaded.CustomThemes[0].Name == "First custom theme" &&
          customReloaded.CustomThemes[0].Colors.Accent == "#FF123456" &&
          customReloaded.CustomThemes[0].Colors.SecondaryButtonBackground == "#FF223344" &&
          customReloaded.CustomThemes[0].Colors.MenuBackground == "#FF334455" &&
          customReloaded.CustomThemes[0].Colors.MenuHoverBackground == "#FF445566" &&
          Math.Abs(customReloaded.CustomThemes[0].Colors.SurfaceOpacity - 0.72) < 0.001 &&
          Math.Abs(customReloaded.CustomThemes[0].Colors.CategoriesPanelOpacity - 0.51) < 0.001 &&
          Math.Abs(customReloaded.CustomThemes[0].Colors.ProfilesPanelOpacity - 0.63) < 0.001 &&
          Math.Abs(customReloaded.CustomThemes[0].Colors.ProfileEditorPanelOpacity - 0.84) < 0.001 &&
          Math.Abs(customReloaded.CustomThemes[0].Colors.ActivityPanelOpacity - 0.47) < 0.001 &&
          customReloaded.CustomThemes[0].Colors.BackgroundAssetFileName == "background-test.gif" &&
          customReloaded.CustomThemes[0].CreatedAt != default && customReloaded.CustomThemes[0].UpdatedAt != default,
        "Custom Theme collection, metadata, colors, and background persist", failures);
    var legacyOpacity = CustomThemeSettings.CreateDefault();
    legacyOpacity.Panel = "#80223344";
    legacyOpacity.MigrateSurfaceOpacityFromLegacyAlpha();
    Check(Math.Abs(legacyOpacity.SurfaceOpacity - 128d / 255) < 0.001 &&
          legacyOpacity.CategoriesPanelOpacity == legacyOpacity.SurfaceOpacity &&
          legacyOpacity.ProfilesPanelOpacity == legacyOpacity.SurfaceOpacity &&
          legacyOpacity.ProfileEditorPanelOpacity == legacyOpacity.SurfaceOpacity &&
          legacyOpacity.ActivityPanelOpacity == legacyOpacity.SurfaceOpacity,
        "legacy custom surface alpha migrates to all opacity controls", failures);
    settingsRepository.Dispose();
    var logPaths = new AppDataPaths(Path.Combine(testRoot, "logging-appdata"));
    var testLogger = new RollingFileLogger(logPaths);
    testLogger.Info("Regression", "technical log smoke test");
    Check(File.Exists(Path.Combine(logPaths.LogsDirectory, "switchboard.log")),
        "rotating technical log is created in LocalAppData layout", failures);

    var themeTestResults = new List<(bool Passed, string Name)>();
    var themeThread = new Thread(() =>
    {
        var app = new System.Windows.Application();
        try
        {
            var themePaths = new AppDataPaths(Path.Combine(testRoot, "theme-appdata"));
            Directory.CreateDirectory(themePaths.CustomThemeDirectory);
            CreateTestImages(themePaths.CustomThemeDirectory);
            var manager = new ThemeManager(themePaths);
            app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
            {
                Source = new Uri("/SwitchBoard;component/Themes/BaseStyles.xaml", UriKind.Relative)
            });
            foreach (var theme in manager.AvailableThemes.Where(item => item.Id != ThemeIds.Custom))
            {
                manager.ApplyTheme(theme.Id);
                var required = new[] { "BackgroundBrush", "SurfaceBrush", "CardSurfaceBrush", "ElevatedSurfaceBrush",
                    "BorderBrush", "TextPrimaryBrush", "TextSecondaryBrush", "PrimaryButtonBackground",
                    "PrimaryButtonForeground", "PrimaryButtonHoverForeground", "PrimaryButtonPressedForeground",
                    "PrimaryButtonDisabledForeground", "SecondaryButtonBackground", "SecondaryButtonForeground",
                    "SecondaryButtonDisabledBackground", "SecondaryButtonDisabledForeground", "MenuBackground",
                    "MenuForeground", "MenuHoverBackground", "MenuHoverForeground", "MenuDisabledForeground",
                    "SelectionForeground", "HoverForeground", "DisabledInputBackground", "DisabledInputForeground",
                    "CategoriesSurfaceBrush", "ProfilesSurfaceBrush", "ProfileEditorSurfaceBrush", "ActivitySurfaceBrush",
                    "IconPrimary", "IconAccent", "IconMuted" };
                var complete = required.All(key => app.TryFindResource(key) is Brush);
                var readable = SemanticContrastIsAccessible(app);
                var rendered = RenderedSemanticControlsAreAccessible(app);
                var editable = manager.CreateEditableCopy(theme.Id);
                var editableColors = new[] { editable.Background, editable.Panel, editable.Card, editable.Elevated,
                    editable.Border, editable.PrimaryText, editable.SecondaryText, editable.Accent, editable.Hover,
                    editable.Selection, editable.PrimaryButtonBackground, editable.IconForeground };
                var canCopy = editableColors.All(value => CustomThemeColorItemViewModel.TryColor(value, out _));
                themeTestResults.Add((complete && readable && rendered && canCopy,
                    $"rendered theme contract and WCAG contrast: {theme.Id} " +
                    $"[complete={complete}, resources={readable}, controls={rendered}, copy={canCopy}]"));
            }
            foreach (var buttonColor in new[]
                     {
                         "#FFFFFFFF", "#FFE8E8EA", "#FF000000", "#FF24262B",
                         "#FF1473E6", "#FFFFE600", "#FFFFFF66", "#FF07101F"
                     })
            {
                var custom = CustomThemeSettings.CreateDefault();
                custom.Background = buttonColor;
                custom.PrimaryText = buttonColor;
                custom.SecondaryText = buttonColor;
                custom.Elevated = buttonColor;
                custom.PrimaryButtonBackground = buttonColor;
                custom.SecondaryButtonBackground = buttonColor;
                custom.MenuBackground = buttonColor;
                custom.MenuHoverBackground = buttonColor;
                custom.Hover = buttonColor;
                custom.Selection = buttonColor;
                custom.Accent = buttonColor;
                manager.ApplyTheme($"contrast-{buttonColor[3..]}", custom);
                var accentBackground = ((SolidColorBrush)app.TryFindResource("AccentBrush")!).Color;
                var accentForeground = ((SolidColorBrush)app.TryFindResource("AccentForegroundBrush")!).Color;
                themeTestResults.Add((SemanticContrastIsAccessible(app) &&
                                      ContrastRatio(accentBackground, accentForeground) >= 4.5,
                    $"WCAG semantic contrast matrix: {buttonColor}"));
            }
            foreach (var asset in new[] { "test.jpg", "test.png", "test.bmp", "test.gif" })
            {
                var custom = CustomThemeSettings.CreateDefault();
                custom.BackgroundAssetFileName = asset;
                manager.ApplyTheme(ThemeIds.Custom, custom);
                themeTestResults.Add((string.Equals(app.TryFindResource("CustomBackgroundPath") as string,
                    Path.Combine(themePaths.CustomThemeDirectory, asset), StringComparison.OrdinalIgnoreCase),
                    $"Custom background resource: {Path.GetExtension(asset)}"));
            }
            var transparentSurfaces = CustomThemeSettings.CreateDefault();
            transparentSurfaces.Background = "#FF102030";
            transparentSurfaces.Border = "#AAABCDEF";
            transparentSurfaces.SurfaceOpacity = 0.70;
            transparentSurfaces.CategoriesPanelOpacity = 0.25;
            transparentSurfaces.ProfilesPanelOpacity = 0.50;
            transparentSurfaces.ProfileEditorPanelOpacity = 0.85;
            transparentSurfaces.ActivityPanelOpacity = 0.40;
            transparentSurfaces.BackgroundOpacity = 0.37;
            manager.ApplyTheme("surface-opacity", transparentSurfaces);
            themeTestResults.Add((
                ((SolidColorBrush)app.TryFindResource("BackgroundBrush")!).Color == Color.FromArgb(255, 16, 32, 48) &&
                ((SolidColorBrush)app.TryFindResource("SurfaceBrush")!).Color.A == 178 &&
                ((SolidColorBrush)app.TryFindResource("CardSurfaceBrush")!).Color.A == 178 &&
                ((SolidColorBrush)app.TryFindResource("ElevatedSurfaceBrush")!).Color.A == 178 &&
                ((SolidColorBrush)app.TryFindResource("CategoriesSurfaceBrush")!).Color.A == 64 &&
                ((SolidColorBrush)app.TryFindResource("ProfilesSurfaceBrush")!).Color.A == 128 &&
                ((SolidColorBrush)app.TryFindResource("ProfileEditorSurfaceBrush")!).Color.A == 217 &&
                ((SolidColorBrush)app.TryFindResource("ActivitySurfaceBrush")!).Color.A == 102 &&
                ((SolidColorBrush)app.TryFindResource("BorderBrush")!).Color.A == 170 &&
                Math.Abs((double)app.TryFindResource("CustomBackgroundOpacity")! - 0.37) < 0.001,
                "surface opacity changes only surface alpha and supports four independent main blocks"));
            using var gifStream = File.OpenRead(Path.Combine(themePaths.CustomThemeDirectory, "test.gif"));
            var gifDecoder = new GifBitmapDecoder(gifStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            themeTestResults.Add((gifDecoder.Frames.Count == 2, "animated GIF decodes and caches two frames"));

            var extreme = CustomThemeSettings.CreateDefault();
            extreme.Background = "#FFFFFFFF";
            extreme.Panel = "#FFFFFFFF";
            extreme.Card = "#FFFFFFFF";
            extreme.PrimaryText = "#FFFFFFFF";
            extreme.SecondaryText = "#FF000000";
            var liveApplyCount = 0;
            var editor = new SwitchBoard.Views.CustomThemeWindow(
                new CustomThemeEditRequest(CustomThemeEditMode.Add, "Extreme", extreme, [], "draft-live",
                    settings =>
                    {
                        liveApplyCount++;
                        manager.ApplyTemporary("draft-live", settings);
                    }),
                themePaths, new TestLocalizationService());
            var editorBackground = ((SolidColorBrush)editor.Resources["EditorBackgroundBrush"]).Color;
            var editorText = ((SolidColorBrush)editor.Resources["EditorTextBrush"]).Color;
            var editorInput = ((SolidColorBrush)editor.Resources["EditorInputBrush"]).Color;
            themeTestResults.Add((ContrastRatio(editorBackground, editorText) >= 12 &&
                                  ContrastRatio(editorInput, editorText) >= 10,
                "Custom Theme editor keeps fixed high-contrast dark resources for extreme theme colors"));
            editor.ViewModel.Colors.First(item => item.Key == "primaryText").Color = "#FF000000";
            editor.ViewModel.Colors.First(item => item.Key == "background").Color = "#FF000000";
            themeTestResults.Add((((SolidColorBrush)editor.Resources["EditorTextBrush"]).Color == editorText,
                "white/black live theme extremes cannot mutate the editor form colors"));
            themeTestResults.Add((liveApplyCount >= 2 && manager.CurrentThemeId == "draft-live" &&
                                  ((SolidColorBrush)app.TryFindResource("BackgroundBrush")!).Color == Colors.Black,
                "theme editor draft updates the real application resources live"));
            editor.ViewModel.SurfaceOpacityPercent = 68;
            editor.ViewModel.CategoriesPanelOpacityPercent = 31;
            editor.ViewModel.ActivityPanelOpacityPercent = 44;
            themeTestResults.Add((Math.Abs(editor.ViewModel.Settings.SurfaceOpacity - 0.68) < 0.001 &&
                                  Math.Abs(editor.ViewModel.Settings.CategoriesPanelOpacity - 0.31) < 0.001 &&
                                  Math.Abs(editor.ViewModel.Settings.ProfilesPanelOpacity - 0.68) < 0.001 &&
                                  Math.Abs(editor.ViewModel.Settings.ActivityPanelOpacity - 0.44) < 0.001 &&
                                  ((SolidColorBrush)app.TryFindResource("CategoriesSurfaceBrush")!).Color.A == 79 &&
                                  ((SolidColorBrush)app.TryFindResource("ProfilesSurfaceBrush")!).Color.A == 173 &&
                                  ((SolidColorBrush)app.TryFindResource("ActivitySurfaceBrush")!).Color.A == 112,
                "global and per-column opacity sliders live-apply independently"));
            var originalName = editor.ViewModel.Name;
            editor.ViewModel.Colors.First(item => item.Key == "accent").Color = "#FFFF0000";
            editor.ViewModel.Reset();
            themeTestResults.Add((editor.ViewModel.Name == originalName &&
                                  editor.ViewModel.Settings.Accent == extreme.Accent,
                "Reset changes only the current editor form values and preserves its name"));
            editor.Close();
            var pickerEvents = new List<Color>();
            var picker = new SwitchBoard.Views.ThemeColorPickerWindow(Colors.White, new TestLocalizationService(),
                color => pickerEvents.Add(color));
            var pickerBackground = ((SolidColorBrush)picker.Background).Color;
            var pickerText = ((SolidColorBrush)picker.Foreground).Color;
            themeTestResults.Add((ContrastRatio(pickerBackground, pickerText) >= 12,
                "ARGB color picker has a fixed high-contrast dark appearance"));
            ((System.Windows.Controls.Slider)picker.FindName("Red")).Value = 16;
            var livePickerUpdate = pickerEvents.LastOrDefault().R == 16;
            ((System.Windows.Controls.Button)picker.FindName("CancelButton")).RaiseEvent(
                new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            themeTestResults.Add((livePickerUpdate && pickerEvents.LastOrDefault() == Colors.White,
                "ARGB picker live-applies slider changes and Cancel restores its color snapshot"));
        }
        finally { app.Shutdown(); }
    });
    themeThread.SetApartmentState(ApartmentState.STA);
    themeThread.Start();
    themeThread.Join();
    foreach (var result in themeTestResults) Check(result.Passed, result.Name, failures);

    var undo = new UndoService<string>(75, TimeSpan.FromSeconds(2));
    undo.Record("before typing", "field:name", true);
    undo.Record("after N", "field:name", true);
    undo.Record("after No", "field:name", true);
    Check(undo.Count == 1 && undo.TryUndo(out var undoState) && undoState == "before typing",
        "Undo stack coalesces consecutive TextBox edits", failures);

    var configurationCatalog = new SwitchBoardCatalog
    {
        Categories = [new CategoryDefinition { Name = "Category", SortOrder = 0 }]
    };
    configurationCatalog.Profiles.Add(new ProfileDefinition
    {
        CategoryId = configurationCatalog.Categories[0].Id, Name = "Profile", SortOrder = 0,
        Actions =
        [
            Action(ActionTypeIds.ProgramRun, new JsonObject { [ActionParameterNames.Target] = "one.exe" }),
            Action(ActionTypeIds.Delay, new JsonObject { [ActionParameterNames.DelaySeconds] = 1 })
        ]
    });
    configurationCatalog.Profiles[0].Actions[0].SortOrder = 0;
    configurationCatalog.Profiles[0].Actions[1].SortOrder = 1;
    var originalCategoryId = configurationCatalog.Categories[0].Id;
    var originalProfileId = configurationCatalog.Profiles[0].Id;
    var originalActionId = configurationCatalog.Profiles[0].Actions[0].Id;
    var testLocalization = new TestLocalizationService();
    var testCatalogService = new TestCatalogService();
    var themeEditor = new TestCustomThemeEditorService();
    var themeManager = new TestThemeManager();
    var themeSettingsRepository = new TestSettingsRepository();
    var editableSettings = new UserSettings { ThemeId = ThemeIds.Graphite, LanguageId = "en" };
    var main = new MainWindowViewModel(testCatalogService, new TestDialogService(), configurationCatalog,
        themeManager, testLocalization, themeSettingsRepository,
        editableSettings, runner,
        new ProfileRestoreRunner(registry, sessionRepository), sessionRepository,
        new TestCompletionBehavior(), new TestDisplayManager(new("", "", "", 1, 1, 1, 32, 0, 0, 0, 0)),
        themeEditor);

    var initialActionCount = main.SelectedProfile!.Actions.Count;
    main.AddActionCommand.Execute(null);
    main.UndoCommand.Execute(null);
    Check(main.SelectedProfile!.Actions.Count == initialActionCount, "Undo add action", failures);
    var initialProfileCount = main.Profiles.Count;
    main.AddProfileCommand.Execute(null);
    main.UndoCommand.Execute(null);
    Check(main.Profiles.Count == initialProfileCount, "Undo add profile", failures);
    var initialCategoryCount = main.Categories.Count;
    main.AddCategoryCommand.Execute(null);
    main.UndoCommand.Execute(null);
    Check(main.Categories.Count == initialCategoryCount, "Undo add category", failures);
    var actionToDelete = main.SelectedProfile.Actions.First(item => item.Id == originalActionId);
    main.DeleteActionCommand.Execute(actionToDelete);
    main.UndoCommand.Execute(null);
    Check(main.SelectedProfile!.Actions.Any(item => item.Id == originalActionId), "Undo delete action keeps GUID", failures);
    var originalProfileName = main.SelectedProfile!.Name;
    main.SelectedProfile.Name = "Renamed profile";
    main.UndoCommand.Execute(null);
    Check(main.SelectedProfile!.Name == originalProfileName, "Undo profile rename", failures);
    var originalCategoryName = main.SelectedCategory!.Name;
    main.SelectedCategory.Name = "Renamed category";
    main.UndoCommand.Execute(null);
    Check(main.SelectedCategory!.Name == originalCategoryName, "Undo category rename", failures);
    var renamed = main.SelectedProfile.Actions.First(item => item.Id == originalActionId);
    var oldActionName = renamed.Name;
    renamed.Name = "Renamed action";
    main.UndoCommand.Execute(null);
    Check(main.SelectedProfile!.Actions.First(item => item.Id == originalActionId).Name == oldActionName, "Undo action name", failures);

    void ChangeAndUndo(Action<ActionItemViewModel> change, Func<ActionItemViewModel, bool> restored, string testName)
    {
        var currentAction = main.SelectedProfile!.Actions.First(item => item.Id == originalActionId);
        change(currentAction);
        main.UndoCommand.Execute(null);
        Check(restored(main.SelectedProfile!.Actions.First(item => item.Id == originalActionId)), testName, failures);
    }
    ChangeAndUndo(item => item.Target = "changed.exe", item => item.Target == "one.exe", "Undo action target");
    ChangeAndUndo(item => item.TimeoutSeconds = 42, item => item.TimeoutSeconds == 0, "Undo timeout");
    ChangeAndUndo(item => item.FailurePolicyId = "stop", item => item.FailurePolicyId == "continue", "Undo failure policy");
    ChangeAndUndo(item => item.RestoreBehaviorId = "previous", item => item.RestoreBehaviorId == "none", "Undo restore behavior");
    ChangeAndUndo(item => item.IsEnabled = false, item => item.IsEnabled, "Undo enable/disable action");
    var firstAction = main.SelectedProfile!.Actions.First(item => item.Id == originalActionId);
    main.MoveActionDownCommand.Execute(firstAction);
    main.UndoCommand.Execute(null);
    Check(main.SelectedProfile.Actions[0].Id == originalActionId, "Undo action reorder", failures);

    main.DeleteProfileCommand.Execute(main.SelectedProfile);
    main.UndoCommand.Execute(null);
    Check(main.Profiles.Any(item => item.Id == originalProfileId), "Undo delete profile keeps children", failures);
    main.DeleteCategoryCommand.Execute(main.SelectedCategory);
    main.UndoCommand.Execute(null);
    Check(main.Categories.Any(item => item.Id == originalCategoryId) && main.Profiles.Any(item => item.Id == originalProfileId),
        "Undo delete category keeps profiles and GUIDs", failures);

    var currentProfile = main.SelectedProfile!;
    currentProfile.Name = "First change";
    main.SelectedProfile!.Actions.First(item => item.Id == originalActionId).TimeoutSeconds = 9;
    main.UndoCommand.Execute(null);
    main.UndoCommand.Execute(null);
    Check(main.SelectedProfile!.Name == "Profile" &&
          main.SelectedProfile.Actions.First(item => item.Id == originalActionId).TimeoutSeconds == 0 &&
          !main.UndoCommand.CanExecute(null), "multiple Ctrl+Z command steps and disabled empty Undo", failures);
    main.SelectedProfile.Actions.First(item => item.Id == originalActionId).Target = "saved-change.exe";
    main.SaveCommand.Execute(null);
    for (var wait = 0; wait < 20 && main.HasUnsavedChanges; wait++) await Task.Delay(25);
    main.UndoCommand.Execute(null);
    Check(main.SelectedProfile.Actions.First(item => item.Id == originalActionId).Target == "one.exe" && main.HasUnsavedChanges,
        "Undo remains available after Save", failures);
    Check(testCatalogService.Saved.Categories.Any(item => item.Id == originalCategoryId) &&
          testCatalogService.Saved.Profiles.Any(item => item.Id == originalProfileId &&
              item.Actions.Any(action => action.Id == originalActionId)),
        "category/profile/action Save snapshot is restart-safe", failures);

    var dragCategoryA = new CategoryDefinition { Name = "Drag A", SortOrder = 0 };
    var dragCategoryB = new CategoryDefinition { Name = "Drag B", SortOrder = 1 };
    var dragProfileA1 = new ProfileDefinition
    {
        CategoryId = dragCategoryA.Id, Name = "A1", SortOrder = 0,
        Actions =
        [
            Action(ActionTypeIds.Delay, new JsonObject { [ActionParameterNames.DelaySeconds] = 1 }),
            Action(ActionTypeIds.Delay, new JsonObject { [ActionParameterNames.DelaySeconds] = 2 })
        ]
    };
    dragProfileA1.Actions[0].SortOrder = 0;
    dragProfileA1.Actions[1].SortOrder = 1;
    var dragProfileA2 = new ProfileDefinition { CategoryId = dragCategoryA.Id, Name = "A2", SortOrder = 1 };
    var dragProfileB1 = new ProfileDefinition { CategoryId = dragCategoryB.Id, Name = "B1", SortOrder = 0 };
    var dragCatalog = new SwitchBoardCatalog
    {
        Categories = [dragCategoryA, dragCategoryB],
        Profiles = [dragProfileA1, dragProfileA2, dragProfileB1]
    };
    var dragCatalogService = new TestCatalogService();
    var dragMain = new MainWindowViewModel(dragCatalogService, new TestDialogService(), dragCatalog,
        new TestThemeManager(), testLocalization, new TestSettingsRepository(),
        new UserSettings { ThemeId = ThemeIds.Graphite, LanguageId = "en" }, runner,
        new ProfileRestoreRunner(registry, sessionRepository), sessionRepository,
        new TestCompletionBehavior(), new TestDisplayManager(new("", "", "", 1, 1, 1, 32, 0, 0, 0, 0)),
        new TestCustomThemeEditorService());

    await dragMain.ApplyReorderAsync(new(ReorderItemKind.Category, dragMain.Categories[0],
        dragMain.Categories[1], 2));
    Check(dragMain.Categories.Select(item => item.Id).SequenceEqual([dragCategoryB.Id, dragCategoryA.Id]) &&
          dragCatalogService.Saved.Categories.OrderBy(item => item.SortOrder).Select(item => item.Id)
              .SequenceEqual([dragCategoryB.Id, dragCategoryA.Id]) && !dragMain.HasUnsavedChanges,
        "category drag reorder is immediately persisted", failures);

    dragMain.SelectedCategory = dragMain.Categories.Single(item => item.Id == dragCategoryA.Id);
    var draggedA1 = dragMain.Profiles.Single(item => item.Id == dragProfileA1.Id);
    await dragMain.ApplyReorderAsync(new(ReorderItemKind.Profile, draggedA1,
        dragMain.Profiles.Single(item => item.Id == dragProfileA2.Id), 2, dragCategoryA.Id));
    Check(dragMain.Profiles.Select(item => item.Id).SequenceEqual([dragProfileA2.Id, dragProfileA1.Id]) &&
          dragCatalogService.Saved.Profiles.Where(item => item.CategoryId == dragCategoryA.Id)
              .OrderBy(item => item.SortOrder).Select(item => item.Id).SequenceEqual([dragProfileA2.Id, dragProfileA1.Id]),
        "profile drag reorder within category is immediately persisted", failures);

    var targetCategory = dragMain.Categories.Single(item => item.Id == dragCategoryB.Id);
    await dragMain.ApplyReorderAsync(new(ReorderItemKind.Profile, draggedA1, targetCategory,
        dragMain.Categories.Count));
    Check(dragMain.SelectedCategory?.Id == dragCategoryB.Id && dragMain.SelectedProfile?.Id == dragProfileA1.Id &&
          dragCatalogService.Saved.Profiles.Single(item => item.Id == dragProfileA1.Id).CategoryId == dragCategoryB.Id &&
          dragCatalogService.Saved.Profiles.Where(item => item.CategoryId == dragCategoryB.Id)
              .OrderBy(item => item.SortOrder).Select(item => item.Id).SequenceEqual([dragProfileB1.Id, dragProfileA1.Id]),
        "profile drag to another category is immediately persisted", failures);

    var draggedAction = dragMain.SelectedProfile!.Actions[0];
    var secondDraggedActionId = dragMain.SelectedProfile.Actions[1].Id;
    await dragMain.ApplyReorderAsync(new(ReorderItemKind.Action, draggedAction,
        dragMain.SelectedProfile.Actions[1], 2, dragMain.SelectedProfile.Id));
    Check(dragMain.SelectedProfile.Actions.Select(item => item.Id).SequenceEqual([secondDraggedActionId, draggedAction.Id]) &&
          dragCatalogService.Saved.Profiles.Single(item => item.Id == dragProfileA1.Id).Actions
              .OrderBy(item => item.SortOrder).Select(item => item.Id).SequenceEqual([secondDraggedActionId, draggedAction.Id]) &&
          dragCatalogService.SaveCount >= 4,
        "action drag reorder is immediately persisted", failures);
    dragMain.UndoCommand.Execute(null);
    Check(dragMain.SelectedProfile!.Actions[0].Id == draggedAction.Id && dragMain.HasUnsavedChanges,
        "drag reorder participates in the existing Undo stack", failures);

    var addCancelCount = editableSettings.CustomThemes.Count;
    var savesBeforeCancelledAdd = themeSettingsRepository.SaveCount;
    themeEditor.EditActions.Enqueue(request =>
    {
        var liveDraft = request.Colors.Clone();
        liveDraft.Background = "#FFFF00FF";
        request.ApplyTemporary?.Invoke(liveDraft);
    });
    themeEditor.Results.Enqueue(null);
    main.AddThemeCommand.Execute(null);
    await WaitUntilAsync(() => main.AddThemeCommand.CanExecute(null));
    Check(editableSettings.CustomThemes.Count == addCancelCount &&
          editableSettings.ThemeId == ThemeIds.Graphite && themeManager.CurrentThemeId == ThemeIds.Graphite &&
          themeManager.TemporaryApplyCount >= 2 && themeSettingsRepository.SaveCount == savesBeforeCancelledAdd,
        "Custom Theme add draft live-applies and Cancel leaves no collection item", failures);

    var whiteTheme = CustomThemeSettings.CreateDefault();
    whiteTheme.Background = "#FFFFFFFF";
    whiteTheme.PrimaryText = "#FFFFFFFF";
    themeEditor.Results.Enqueue(new("Snow", whiteTheme));
    main.AddThemeCommand.Execute(null);
    await WaitUntilAsync(() => editableSettings.CustomThemes.Count == 1);
    var snowOption = main.ThemeOptions.Single(item => item.IsCustom);
    Check(snowOption.DisplayName == "Snow" && snowOption.IsActive && editableSettings.ThemeId == snowOption.Id,
        "Custom Theme create adds collection item and activates it", failures);
    Check(themeSettingsRepository.Saved.CustomThemes.Single().Name == "Snow",
        "Custom Theme create is persisted atomically", failures);

    var restartThemeManager = new TestThemeManager();
    restartThemeManager.ApplyTheme(themeSettingsRepository.Saved.ThemeId,
        themeSettingsRepository.Saved.CustomThemes.Single().Colors);
    var restartedMain = new MainWindowViewModel(testCatalogService, new TestDialogService(), configurationCatalog,
        restartThemeManager, testLocalization, new TestSettingsRepository(), themeSettingsRepository.Saved,
        runner, new ProfileRestoreRunner(registry, sessionRepository), sessionRepository,
        new TestCompletionBehavior(), new TestDisplayManager(new("", "", "", 1, 1, 1, 32, 0, 0, 0, 0)),
        new TestCustomThemeEditorService());
    Check(restartedMain.ThemeOptions.Any(item => item.IsCustom && item.DisplayName == "Snow") &&
          restartedMain.SelectedThemeOption?.DisplayName == "Snow",
        "Custom Theme collection reloads with active selection after restart", failures);

    var graphiteBeforeInactiveEdit = main.ThemeOptions.Single(item => item.Id == ThemeIds.Graphite);
    main.SelectedThemeOption = graphiteBeforeInactiveEdit;
    await WaitUntilAsync(() => editableSettings.ThemeId == ThemeIds.Graphite);
    var persistedSnowBeforeCancel = editableSettings.CustomThemes.Single(item => item.Id == snowOption.Id).Colors.Clone();
    themeEditor.EditActions.Enqueue(request =>
    {
        var liveDraft = request.Colors.Clone();
        liveDraft.Card = "#FFFF00FF";
        request.ApplyTemporary?.Invoke(liveDraft);
    });
    themeEditor.Results.Enqueue(null);
    main.EditThemeCommand.Execute(snowOption.Id);
    await WaitUntilAsync(() => main.EditThemeCommand.CanExecute(snowOption.Id));
    Check(editableSettings.ThemeId == ThemeIds.Graphite && themeManager.CurrentThemeId == ThemeIds.Graphite &&
          editableSettings.CustomThemes.Single(item => item.Id == snowOption.Id).Colors.Card == persistedSnowBeforeCancel.Card,
        "editing an inactive theme live-applies and Cancel restores the previous active theme", failures);

    var blackTheme = whiteTheme.Clone();
    blackTheme.Background = "#FF000000";
    blackTheme.PrimaryText = "#FF000000";
    themeEditor.Results.Enqueue(new("Snow edited", blackTheme));
    main.EditThemeCommand.Execute(snowOption.Id);
    await WaitUntilAsync(() => snowOption.DisplayName == "Snow edited");
    Check(editableSettings.CustomThemes.Single().Colors.Background == "#FF000000" && snowOption.IsActive,
        "Custom Theme edit updates existing item without changing its ID", failures);

    var countBeforeCancelledDuplicate = editableSettings.CustomThemes.Count;
    themeEditor.EditActions.Enqueue(request => request.ApplyTemporary?.Invoke(request.Colors.Clone()));
    themeEditor.Results.Enqueue(null);
    main.DuplicateThemeCommand.Execute(snowOption.Id);
    await WaitUntilAsync(() => main.DuplicateThemeCommand.CanExecute(snowOption.Id));
    Check(editableSettings.CustomThemes.Count == countBeforeCancelledDuplicate &&
          editableSettings.ThemeId == snowOption.Id && themeManager.CurrentThemeId == snowOption.Id,
        "cancelled duplicate draft leaves no orphan and restores active theme", failures);

    themeEditor.Results.Enqueue(new("Snow copy", blackTheme.Clone()));
    main.DuplicateThemeCommand.Execute(snowOption.Id);
    await WaitUntilAsync(() => editableSettings.CustomThemes.Count == 2);
    var copiedOption = main.ThemeOptions.Single(item => item.IsCustom && item.DisplayName == "Snow copy");
    Check(copiedOption.Id != snowOption.Id && copiedOption.IsActive,
        "Custom Theme duplicate receives a new ID and becomes active", failures);

    themeEditor.RenameResults.Enqueue("Renamed copy");
    main.RenameThemeCommand.Execute(copiedOption.Id);
    await WaitUntilAsync(() => copiedOption.DisplayName == "Renamed copy");
    Check(editableSettings.CustomThemes.Any(item => item.Name == "Renamed copy"),
        "Custom Theme rename updates persistence", failures);

    main.DeleteThemeCommand.Execute(copiedOption.Id);
    await WaitUntilAsync(() => editableSettings.CustomThemes.Count == 1);
    Check(main.SelectedThemeOption?.Id == ThemeIds.Graphite && main.SelectedThemeOption.IsActive,
        "deleting active Custom Theme returns to a built-in theme", failures);

    var graphite = main.ThemeOptions.Single(item => item.Id == ThemeIds.Graphite);
    themeEditor.Results.Enqueue(new("Graphite copy", CustomThemeSettings.CreateDefault()));
    main.DuplicateThemeCommand.Execute(graphite.Id);
    await WaitUntilAsync(() => editableSettings.CustomThemes.Count == 2);
    Check(main.SelectedThemeOption?.DisplayName == "Graphite copy" && main.SelectedThemeOption.IsCustom,
        "editing a built-in theme saves and activates a new copy", failures);

    var duplicateNameVm = new CustomThemeEditorViewModel(new(
        CustomThemeEditMode.Add, "Snow edited", CustomThemeSettings.CreateDefault(), ["Snow edited"]), testLocalization);
    Check(!duplicateNameVm.IsNameValid && duplicateNameVm.NameError.Length > 0,
        "identical Custom Theme names are rejected", failures);

    var duplicateSources = new[]
    {
        new CustomThemeDefinition { Name = "First", Colors = CustomThemeSettings.CreateDefault() },
        new CustomThemeDefinition { Name = "Second", Colors = CustomThemeSettings.CreateDefault() },
        new CustomThemeDefinition { Name = "Last", Colors = CustomThemeSettings.CreateDefault() }
    };
    duplicateSources[0].Colors.Background = "#FF110000";
    duplicateSources[0].Colors.PrimaryButtonBackground = "#FFFF1111";
    duplicateSources[1].Colors.Background = "#FF001100";
    duplicateSources[1].Colors.PrimaryButtonBackground = "#FF11FF11";
    duplicateSources[2].Colors.Background = "#FF000011";
    duplicateSources[2].Colors.PrimaryButtonBackground = "#FF1111FF";
    var duplicateSettings = new UserSettings
    {
        ThemeId = duplicateSources[1].Id,
        CustomThemes = duplicateSources.Select(item => item.Clone()).ToList()
    };
    var duplicateManager = new TestThemeManager();
    duplicateManager.ApplyTheme(duplicateSettings.ThemeId, duplicateSettings.CustomThemes[1].Colors);
    var duplicateEditor = new TestCustomThemeEditorService { EchoWhenEmpty = true };
    var duplicateMain = new MainWindowViewModel(new TestCatalogService(), new TestDialogService(), configurationCatalog,
        duplicateManager, testLocalization, new TestSettingsRepository(), duplicateSettings, runner,
        new ProfileRestoreRunner(registry, sessionRepository), sessionRepository, new TestCompletionBehavior(),
        new TestDisplayManager(new("", "", "", 1, 1, 1, 32, 0, 0, 0, 0)), duplicateEditor);
    Check(duplicateMain.ThemeOptions.Single(item => item.Id == duplicateSources[1].Id).IsActive,
        "duplication test starts with the second source active", failures);
    var sourceIds = new[]
    {
        duplicateSources[1].Id, // active second
        duplicateSources[0].Id, // inactive first
        duplicateSources[2].Id, // inactive last
        duplicateSources[0].Id,
        duplicateSources[0].Id
    };
    foreach (var sourceId in sourceIds)
    {
        var sourceSnapshot = duplicateSettings.CustomThemes.Single(item => item.Id == sourceId).Colors.Clone();
        var previousRequestCount = duplicateEditor.Requests.Count;
        duplicateMain.DuplicateThemeCommand.Execute(sourceId);
        await WaitUntilAsync(() => duplicateEditor.Requests.Count == previousRequestCount + 1 &&
                                   duplicateMain.DuplicateThemeCommand.CanExecute(sourceId));
        var request = duplicateEditor.Requests[^1];
        var openedCopy = duplicateSettings.CustomThemes.SingleOrDefault(item => item.Id == request.ThemeId);
        Check(request.ThemeId is not null && request.ThemeId != sourceId && openedCopy is not null &&
              request.Colors.Background == sourceSnapshot.Background &&
              request.Colors.PrimaryButtonBackground == sourceSnapshot.PrimaryButtonBackground &&
              openedCopy.Colors.Background == sourceSnapshot.Background &&
              !ReferenceEquals(openedCopy.Colors, duplicateSettings.CustomThemes.Single(item => item.Id == sourceId).Colors),
            $"duplicate resolves exact source ID and opens exact new ID: {sourceId}", failures);
    }
    Check(duplicateSettings.CustomThemes.Any(item => item.Name == "First — copy") &&
          duplicateSettings.CustomThemes.Any(item => item.Name == "First — copy (2)") &&
          duplicateSettings.CustomThemes.Any(item => item.Name == "First — copy (3)"),
        "multiple copies of one ThemeId receive deterministic unique names", failures);

    using (var notepad = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true }))
    {
        if (notepad is not null)
        {
            await Task.Delay(500);
            var processAction = Action(ActionTypeIds.ProcessSetState, new JsonObject
            {
                [ActionParameterNames.ProcessName] = "notepad",
                [ActionParameterNames.DesiredState] = ProcessDesiredStateIds.Stopped
            });
            var handler = new ProcessSetStateActionHandler();
            var first = await handler.ExecuteAsync(processAction, new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
            await Task.Delay(750);
            var second = await handler.ExecuteAsync(processAction, new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
            Check(first.IsSuccessful && !first.IsSkipped, "process.setState stops Notepad", failures);
            Check(second.IsSuccessful && second.IsSkipped, "process.setState skips absent process", failures);
        }
        else
        {
            failures.Add("Notepad could not start");
        }
    }

    var powershellPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
    var preexistingInfo = new ProcessStartInfo(powershellPath) { UseShellExecute = false };
    preexistingInfo.ArgumentList.Add("-NoProfile");
    preexistingInfo.ArgumentList.Add("-Command");
    preexistingInfo.ArgumentList.Add("Start-Sleep -Seconds 30");
    using (var preexisting = Process.Start(preexistingInfo))
    {
        if (preexisting is not null)
        {
            await Task.Delay(350);
            var skipExisting = await new ProgramRunActionHandler().ExecuteAsync(
                Action(ActionTypeIds.ProgramRun, new JsonObject
                {
                    [ActionParameterNames.Target] = powershellPath,
                    [ActionParameterNames.StartOnlyIfNotAlreadyRunning] = true
                }), new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
            Check(skipExisting.IsSkipped, "program.run already-running detection skips duplicate", failures);
            var programAction = Action(ActionTypeIds.ProgramRun, new JsonObject
            {
                [ActionParameterNames.Target] = powershellPath,
                [ActionParameterNames.Arguments] = "-NoProfile -Command \"Start-Sleep -Seconds 30\"",
                [ActionParameterNames.StartOnlyIfNotAlreadyRunning] = false
            });
            programAction.RestoreBehavior = ActionRestoreBehavior.CloseIfStartedBySwitchBoard;
            var programProfile = new ProfileDefinition
            {
                CategoryId = Guid.NewGuid(), Name = "Program restore test", Actions = [programAction]
            };
            var programSession = await runner.RunAsync(programProfile);
            var programPending = await sessionRepository.GetLatestPendingAsync(programProfile.Id);
            var launchedPid = programPending?.Actions[0].PreviousState?["processId"]?.GetValue<int>() ?? 0;
            Check(programSession.Status == ExecutionSessionStatus.Completed && launchedPid > 0 && launchedPid != preexisting.Id && !preexisting.HasExited,
                "program.run records only the instance started by SwitchBoard", failures);
            if (programPending is not null)
            {
                // Reload the session from JSON to prove Restore does not depend on in-memory process objects.
                var persistedProgramPending = await sessionRepository.LoadAsync(programPending.SessionId);
                Check(persistedProgramPending?.PendingRestoreCount == 1,
                    "program.run pending Restore survives session reload", failures);
                await new ProfileRestoreRunner(registry, sessionRepository).RunAsync(persistedProgramPending!);
                await Task.Delay(250);
                var launchedStillAlive = true;
                try { using var launched = Process.GetProcessById(launchedPid); launchedStillAlive = !launched.HasExited; }
                catch (ArgumentException) { launchedStillAlive = false; }
                Check(!launchedStillAlive && !preexisting.HasExited,
                    "program.run Restore closes exact PID and preserves pre-existing instance", failures);
            }
            if (!preexisting.HasExited) preexisting.Kill();
        }
    }

    var edgePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        "Microsoft", "Edge", "Application", "msedge.exe");
    if (File.Exists(edgePath))
    {
        var edgeBaseline = Process.GetProcessesByName("msedge");
        var edgeBaselineIds = edgeBaseline.Select(process => process.Id).ToHashSet();
        foreach (var process in edgeBaseline) process.Dispose();
        var edgeData = Path.Combine(testRoot, "edge-profile");
        var edgeAction = Action(ActionTypeIds.ProgramRun, new JsonObject
        {
            [ActionParameterNames.Target] = edgePath,
            [ActionParameterNames.Arguments] = $"--user-data-dir=\"{edgeData}\" --no-first-run about:blank",
            [ActionParameterNames.InstanceBehavior] = InstanceBehaviorIds.StartAnother
        });
        edgeAction.RestoreBehavior = ActionRestoreBehavior.CloseIfStartedBySwitchBoard;
        var edgeProfile = new ProfileDefinition
        {
            CategoryId = Guid.NewGuid(), Name = "Microsoft Edge restore test", Actions = [edgeAction]
        };
        var edgeSession = await runner.RunAsync(edgeProfile);
        var edgePending = await sessionRepository.GetLatestPendingAsync(edgeProfile.Id);
        var edgeState = edgePending?.Actions.Single().PreviousState;
        var edgeTracked = edgeState?["launchedProcesses"] as JsonArray;
        Check(edgeSession.Status == ExecutionSessionStatus.Completed &&
              edgeState?["startedBySwitchBoard"]?.GetValue<bool>() == true && edgeTracked?.Count > 1,
            "program.run identifies Microsoft Edge multi-process launch", failures);
        if (edgePending is not null)
        {
            var reloaded = await sessionRepository.LoadAsync(edgePending.SessionId);
            var edgeRestoreResult = await new ProfileRestoreRunner(registry, sessionRepository).RunAsync(reloaded!);
            await Task.Delay(500);
            var afterEdge = Process.GetProcessesByName("msedge");
            var afterIds = afterEdge.Select(process => process.Id).ToHashSet();
            foreach (var process in afterEdge) process.Dispose();
            var trackedIds = edgeTracked?.OfType<JsonObject>()
                .Select(item => item["processId"]?.GetValue<int>() ?? 0).Where(id => id > 0).ToHashSet() ?? [];
            var persistedBaselineIds = (edgeState?["preExistingProcesses"] as JsonArray)?.OfType<JsonObject>()
                .Select(item => item["processId"]?.GetValue<int>() ?? 0).Where(id => id > 0).ToHashSet() ?? [];
            Check(edgeRestoreResult.PendingRestoreCount == 0 && !trackedIds.Overlaps(afterIds),
                "program.run Restore verifies all tracked Microsoft Edge processes exited", failures);
            Check(!trackedIds.Overlaps(persistedBaselineIds) && edgeBaselineIds.SetEquals(persistedBaselineIds),
                "program.run Restore target set excludes Microsoft Edge processes present before profile", failures);
        }
    }
    else Console.WriteLine("SKIP Microsoft Edge restore test: msedge.exe was not found.");

    var treeHelperPath = Environment.ProcessPath;
    var treeChildPidPath = Path.Combine(testRoot, "program-run-child.pid");
    if (!string.IsNullOrWhiteSpace(treeHelperPath) &&
        string.Equals(Path.GetExtension(treeHelperPath), ".exe", StringComparison.OrdinalIgnoreCase))
    {
        var treeAction = Action(ActionTypeIds.ProgramRun, new JsonObject
        {
            [ActionParameterNames.Target] = treeHelperPath,
            [ActionParameterNames.Arguments] = $"--program-run-tree-helper \"{treeChildPidPath}\"",
            [ActionParameterNames.StartOnlyIfNotAlreadyRunning] = false
        });
        treeAction.RestoreBehavior = ActionRestoreBehavior.CloseIfStartedBySwitchBoard;
        var treeProfile = new ProfileDefinition
        {
            CategoryId = Guid.NewGuid(), Name = "Program process tree restore test", Actions = [treeAction]
        };

        var treeSession = await runner.RunAsync(treeProfile);
        var treePending = await sessionRepository.GetLatestPendingAsync(treeProfile.Id);
        await Task.Delay(1500);
        var treeState = treePending?.Actions[0].PreviousState;
        var rootPid = treeState?["processId"]?.GetValue<int>() ?? 0;
        var childPid = File.Exists(treeChildPidPath) &&
                       int.TryParse(await File.ReadAllTextAsync(treeChildPidPath), out var parsedChildPid)
            ? parsedChildPid
            : 0;
        var trackedProcesses = treeState?["launchedProcesses"] as JsonArray;
        Check(treeSession.Status == ExecutionSessionStatus.Completed && rootPid > 0 && childPid > 0 &&
              trackedProcesses?.OfType<JsonObject>().Any(item =>
                  item["processId"]?.GetValue<int>() == childPid) == true,
            "program.run persists launcher and descendant process identities", failures);

        var rootExitedBeforeRestore = rootPid > 0;
        try
        {
            using var rootProcess = Process.GetProcessById(rootPid);
            rootExitedBeforeRestore = rootProcess.HasExited;
        }
        catch (ArgumentException) { rootExitedBeforeRestore = true; }
        Check(rootExitedBeforeRestore, "program.run test launcher exits before Restore", failures);

        if (treePending is not null)
            await new ProfileRestoreRunner(registry, sessionRepository).RunAsync(treePending);
        await Task.Delay(300);

        var childStillAlive = childPid > 0;
        try
        {
            using var childProcess = Process.GetProcessById(childPid);
            childStillAlive = !childProcess.HasExited;
        }
        catch (ArgumentException) { childStillAlive = false; }
        Check(!childStillAlive,
            "program.run Restore closes saved descendant after launcher has exited", failures);

        foreach (var processId in new[] { rootPid, childPid }.Where(value => value > 0))
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) { }
        }
    }
    else
    {
        failures.Add("Runtime test executable path is unavailable for program.run process-tree test");
    }

    var powershellInfo = new ProcessStartInfo(powershellPath) { UseShellExecute = false };
    powershellInfo.ArgumentList.Add("-NoProfile");
    powershellInfo.ArgumentList.Add("-Command");
    powershellInfo.ArgumentList.Add("Start-Sleep -Seconds 30");
    using (var powershell = Process.Start(powershellInfo))
    {
        if (powershell is not null)
        {
            await Task.Delay(350);
            var processAction = Action(ActionTypeIds.ProcessSetState, new JsonObject
            {
                [ActionParameterNames.ProcessName] = "powershell",
                [ActionParameterNames.ExecutablePath] = powershellPath,
                [ActionParameterNames.DesiredState] = ProcessDesiredStateIds.Stopped
            });
            processAction.RuntimeProcessIdHint = powershell.Id;
            var powershellProfile = new ProfileDefinition
            {
                CategoryId = Guid.NewGuid(), Name = "PowerShell process runtime test",
                Actions = [processAction, Action(ActionTypeIds.Delay, new JsonObject { [ActionParameterNames.DelaySeconds] = 0 })]
            };
            powershellProfile.Actions[0].SortOrder = 0;
            powershellProfile.Actions[1].SortOrder = 1;
            var powershellSession = await runner.RunAsync(powershellProfile);
            Check(powershellSession.Status == ExecutionSessionStatus.Completed &&
                  powershellSession.Journal[0].Status == ActionJournalStatus.Success &&
                  powershellSession.Journal[1].Status == ActionJournalStatus.Success && powershell.HasExited,
                "process.setState PowerShell kill remains Success and profile continues", failures);
        }
    }

    var restoreScriptMarker = Path.Combine(testRoot, "restore-script-marker.txt");
    var mainRestoreScript = Path.Combine(testRoot, "main-for-restore.ps1");
    var cleanupScript = Path.Combine(testRoot, "cleanup.ps1");
    await File.WriteAllTextAsync(mainRestoreScript, $"Set-Content -LiteralPath '{restoreScriptMarker.Replace("'", "''")}' -Value created\nexit 0\n");
    await File.WriteAllTextAsync(cleanupScript, $"Remove-Item -LiteralPath '{restoreScriptMarker.Replace("'", "''")}' -Force -ErrorAction SilentlyContinue\nexit 0\n");
    var scriptWithRestore = Action(ActionTypeIds.ScriptRun, new JsonObject
    {
        [ActionParameterNames.ScriptPath] = mainRestoreScript,
        [ActionParameterNames.ScriptType] = ScriptTypeIds.PowerShell,
        [ActionParameterNames.WaitForExit] = true,
        [ActionParameterNames.RestoreScriptPath] = cleanupScript,
        [ActionParameterNames.RestoreScriptType] = ScriptTypeIds.PowerShell,
        [ActionParameterNames.RestoreScriptWaitForExit] = true
    });
    scriptWithRestore.RestoreBehavior = ActionRestoreBehavior.RunRestoreScript;
    var scriptRestoreProfile = new ProfileDefinition
    {
        CategoryId = Guid.NewGuid(), Name = "Restore script test", Actions = [scriptWithRestore]
    };
    await runner.RunAsync(scriptRestoreProfile);
    var scriptPending = await sessionRepository.GetLatestPendingAsync(scriptRestoreProfile.Id);
    Check(File.Exists(restoreScriptMarker) && scriptPending?.PendingRestoreCount == 1,
        "script.run saves explicit restore script configuration", failures);
    if (scriptPending is not null) await new ProfileRestoreRunner(registry, sessionRepository).RunAsync(scriptPending);
    Check(!File.Exists(restoreScriptMarker), "Restore Script executes and verifies exit code", failures);

    var notepadRestorePath = Path.Combine(Environment.SystemDirectory, "notepad.exe");
    using (var restorableNotepad = Process.Start(new ProcessStartInfo(notepadRestorePath) { UseShellExecute = true }))
    {
        if (restorableNotepad is not null)
        {
            await Task.Delay(500);
            try { notepadRestorePath = restorableNotepad.MainModule?.FileName ?? notepadRestorePath; }
            catch (Exception) { }
            var processRestoreAction = Action(ActionTypeIds.ProcessSetState, new JsonObject
            {
                [ActionParameterNames.ProcessName] = "notepad",
                [ActionParameterNames.ExecutablePath] = notepadRestorePath,
                [ActionParameterNames.DesiredState] = ProcessDesiredStateIds.Stopped
            });
            processRestoreAction.RuntimeProcessIdHint = restorableNotepad.Id;
            processRestoreAction.RestoreBehavior = ActionRestoreBehavior.RestartIfWasRunning;
            var processRestoreProfile = new ProfileDefinition
            {
                CategoryId = Guid.NewGuid(), Name = "Process restart test", Actions = [processRestoreAction]
            };
            await runner.RunAsync(processRestoreProfile);
            var processPending = await sessionRepository.GetLatestPendingAsync(processRestoreProfile.Id);
            Check(restorableNotepad.HasExited && processPending?.PendingRestoreCount == 1,
                "process.setState captures executable before stopping", failures);
            if (processPending is not null) await new ProfileRestoreRunner(registry, sessionRepository).RunAsync(processPending);
            await Task.Delay(500);
            var restarted = Process.GetProcessesByName("notepad");
            Check(restarted.Length > 0, "process.setState Restore restarts EXE when it was running", failures);
            foreach (var process in restarted) { try { process.Kill(); } catch { } finally { process.Dispose(); } }
        }
    }

    var plans = await powerManager.GetPlansAsync();
    var originalPlan = await powerManager.GetActivePlanAsync();
    Check(plans.Count > 0 && plans.Any(plan => plan.Id == originalPlan), "power plan discovery and active plan", failures);
    foreach (var plan in plans) Console.WriteLine($"POWER {plan.DisplayName} | {plan.GuidText} | Active={plan.IsActive}");
    Check(plans.All(plan => !string.IsNullOrWhiteSpace(plan.DisplayName) &&
                            !string.Equals(plan.DisplayName, plan.GuidText, StringComparison.OrdinalIgnoreCase)),
        "power plan friendly names", failures);
    var capturedPower = await new PowerSetPlanActionHandler(powerManager).CaptureStateAsync(
        Action(ActionTypeIds.PowerSetPlan, []), new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
    Check(Guid.TryParse(capturedPower?["previousPowerPlanGuid"]?.GetValue<string>(), out var capturedPowerId) &&
          capturedPowerId == originalPlan, "power.setPlan CaptureState reads active plan", failures);
    var alternate = plans.FirstOrDefault(plan => plan.Id != originalPlan);
    if (alternate is not null)
    {
        var changedSuccessfully = false;
        try
        {
            var powerResult = await new PowerSetPlanActionHandler(powerManager).ExecuteAsync(
                Action(ActionTypeIds.PowerSetPlan, new JsonObject { [ActionParameterNames.PowerPlanGuid] = alternate.Id.ToString("D") }),
                new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
            changedSuccessfully = powerResult.IsSuccessful && await powerManager.GetActivePlanAsync() == alternate.Id;
            if (powerResult.IsSuccessful)
            {
                Check(changedSuccessfully, "power.setPlan changes and verifies active plan", failures);
            }
            else
            {
                Console.WriteLine($"LIMIT power plan change not permitted: {powerResult.Message}");
            }
        }
        finally
        {
            if (changedSuccessfully)
            {
                await powerManager.SetActivePlanAsync(originalPlan);
                Check(await powerManager.GetActivePlanAsync() == originalPlan, "power plan restored after test", failures);
            }
        }
    }
    else
    {
        Console.WriteLine("SKIP power.setPlan change: only one power plan is available.");
    }

    var services = await serviceManager.GetServicesAsync();
    Check(services.Count > 0, "Windows service discovery", failures);
    var runningService = services.FirstOrDefault(service => service.Status == "Running");
    if (runningService is not null)
    {
        var serviceHandler = new ServiceSetStateActionHandler(serviceManager);
        var capturedService = await serviceHandler.CaptureStateAsync(
            Action(ActionTypeIds.ServiceSetState, new JsonObject { [ActionParameterNames.ServiceName] = runningService.ServiceName }),
            new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
        Check(capturedService?["previousState"]?.GetValue<string>() == ServiceDesiredStateIds.Running,
            "service.setState CaptureState reads current state", failures);
        var skipped = await serviceManager.SetStateAsync(runningService.ServiceName, ServiceDesiredStateIds.Running, TimeSpan.FromSeconds(5));
        Check(skipped.IsSuccessful && skipped.IsSkipped, "service.setState Running -> Start is Skipped", failures);
    }

    var safeService = services.FirstOrDefault(service => string.Equals(service.ServiceName, "WerSvc", StringComparison.OrdinalIgnoreCase));
    if (safeService is not null)
    {
        var initialState = safeService.Status == "Running" ? ServiceDesiredStateIds.Running : ServiceDesiredStateIds.Stopped;
        var changedState = initialState == ServiceDesiredStateIds.Running ? ServiceDesiredStateIds.Stopped : ServiceDesiredStateIds.Running;
        var changed = await serviceManager.SetStateAsync(safeService.ServiceName, changedState, TimeSpan.FromSeconds(15));
        if (changed.IsSuccessful)
        {
            var restored = await serviceManager.SetStateAsync(safeService.ServiceName, initialState, TimeSpan.FromSeconds(15));
            Check(restored.IsSuccessful, "service.setState safe stop/start and restore", failures);
        }
        else
        {
            Console.WriteLine($"LIMIT service stop/start not permitted: {changed.Message}");
        }
    }

    var dusm = services.FirstOrDefault(service => string.Equals(service.ServiceName, "DusmSvc", StringComparison.OrdinalIgnoreCase));
    if (dusm is not null)
    {
        var initialDusmState = await serviceManager.GetStateAsync("DusmSvc");
        var stopDusm = await serviceManager.SetStateAsync("DusmSvc", ServiceDesiredStateIds.Stopped,
            TimeSpan.FromSeconds(15));
        if (stopDusm.IsSuccessful)
        {
            await Task.Delay(1100);
            var stableDusmState = await serviceManager.GetStateAsync("DusmSvc");
            Check(stableDusmState == ServiceDesiredStateIds.Stopped,
                "DusmSvc Stop is verified again after stability delay", failures);
        }
        else
        {
            Check(stopDusm.CurrentState != ServiceDesiredStateIds.Stopped || stopDusm.Win32Error is not null ||
                  stopDusm.WasRestartedByWindows,
                "DusmSvc failed Stop reports actual state or Windows error", failures);
            Console.WriteLine($"LIMIT DusmSvc Stop: {stopDusm.Message}");
        }
        if (initialDusmState == ServiceDesiredStateIds.Running)
        {
            var restoreDusm = await serviceManager.SetStateAsync("DusmSvc", ServiceDesiredStateIds.Running,
                TimeSpan.FromSeconds(15));
            Check(restoreDusm.IsSuccessful, "DusmSvc restored to original Running state", failures);
        }
        else
            Check(await serviceManager.GetStateAsync("DusmSvc") == ServiceDesiredStateIds.Stopped,
                "DusmSvc remains in original Stopped state", failures);
    }

    var advancedAction = new ActionItemViewModel(new ActionDefinition
    {
        Type = ActionTypeIds.ServiceSetState,
        Timeout = TimeSpan.FromSeconds(17),
        FailurePolicy = ActionFailurePolicy.Stop,
        RestoreBehavior = ActionRestoreBehavior.RestorePreviousState,
        Parameters = new JsonObject { [ActionParameterNames.DesiredState] = ServiceDesiredStateIds.Unchanged }
    }, new TestLocalizationService());
    var advancedRoundTrip = advancedAction.ToModel();
    var advancedJson = JsonSerializer.Serialize(advancedRoundTrip);
    var advancedReloaded = JsonSerializer.Deserialize<ActionDefinition>(advancedJson);
    Check(!advancedAction.IsAdvancedOptionsExpanded && advancedReloaded?.Timeout == TimeSpan.FromSeconds(17) &&
          advancedReloaded.FailurePolicy == ActionFailurePolicy.Stop &&
          advancedReloaded.RestoreBehavior == ActionRestoreBehavior.RestorePreviousState &&
          !advancedJson.Contains("AdvancedOptions", StringComparison.OrdinalIgnoreCase),
        "advanced options default collapsed and values persist", failures);
    advancedAction.IsExpanded = true;
    advancedAction.IsAdvancedOptionsExpanded = true;
    advancedAction.IsExpanded = false;
    Check(!advancedAction.IsAdvancedOptionsExpanded && advancedAction.TimeoutSeconds == 17 &&
          advancedAction.FailurePolicyId == "stop",
        "collapsing action resets Advanced visibility without resetting values", failures);
    var validationLocalization = new TestLocalizationService();
    var invalidProgram = new ActionItemViewModel(Action(ActionTypeIds.ProgramRun, []), validationLocalization);
    var delayWithoutRestore = new ActionItemViewModel(Action(ActionTypeIds.Delay,
        new JsonObject { [ActionParameterNames.DelaySeconds] = 2 }), validationLocalization);
    var invalidRestoreScript = new ActionItemViewModel(Action(ActionTypeIds.ScriptRun,
        new JsonObject { [ActionParameterNames.ScriptPath] = successScript }), validationLocalization)
    { RestoreBehaviorId = "restoreScript" };
    Check(!invalidProgram.IsValid && !invalidProgram.SupportsRestore && !delayWithoutRestore.SupportsRestore &&
          !invalidRestoreScript.IsValid,
        "inline validation blocks obvious errors and Delay has no Restore", failures);

    var displays = await displayManager.GetDisplaysAsync();
    foreach (var display in displays)
    {
        Console.WriteLine($"DISPLAY {display.MonitorNumber}: {display.DisplayName} | {display.DeviceId} | {display.CurrentModeText} | Primary={display.IsPrimary} | Modes={display.Modes.Count}");
    }
    Check(displays.Count > 0, "display monitor discovery", failures);
    Check(displays.All(display => display.Modes.Count > 0), "display supported mode discovery", failures);
    if (displays.FirstOrDefault() is { } nativeDisplay)
    {
        var nativeState = await displayManager.GetCurrentStateAsync(nativeDisplay.DeviceId, nativeDisplay.DeviceName);
        try
        {
            await displayManager.ApplyTemporaryAsync(nativeState);
            var nativeVerified = await displayManager.GetCurrentStateAsync(nativeDisplay.DeviceId, nativeDisplay.DeviceName);
            Check(nativeVerified.Width == nativeState.Width && nativeVerified.Height == nativeState.Height &&
                  nativeVerified.RefreshRate == nativeState.RefreshRate,
                "display native apply/verify of current safe mode", failures);
            await displayManager.RestoreAsync(nativeState);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"LIMIT native display change rejected by the current session: {exception.Message}");
        }
    }

    var simulatedPrevious = new DisplayModeState("DISPLAY-TEST", "MONITOR-TEST", "Test monitor", 1920, 1080, 60, 32, 0, 0, 0, 0);
    var simulatedTargetAction = Action(ActionTypeIds.DisplayConfigure, new JsonObject
    {
        [ActionParameterNames.DisplayDeviceName] = simulatedPrevious.DeviceName,
        [ActionParameterNames.DisplayDeviceId] = simulatedPrevious.DeviceId,
        [ActionParameterNames.DisplayName] = simulatedPrevious.DisplayName,
        [ActionParameterNames.DisplayWidth] = 2560,
        [ActionParameterNames.DisplayHeight] = 1440,
        [ActionParameterNames.DisplayRefreshRate] = 144
    });
    var simulatedKeepManager = new TestDisplayManager(simulatedPrevious);
    var simulatedKeep = await new DisplayConfigureActionHandler(simulatedKeepManager, new TestDisplayConfirmationService(true))
        .ExecuteAsync(simulatedTargetAction, new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
    Check(simulatedKeep.IsSuccessful && simulatedKeepManager.State.Width == 2560 && simulatedKeepManager.State.RefreshRate == 144,
        "display.configure verified Keep flow", failures);
    var simulatedRevertManager = new TestDisplayManager(simulatedPrevious);
    var simulatedRevert = await new DisplayConfigureActionHandler(simulatedRevertManager, new TestDisplayConfirmationService(false))
        .ExecuteAsync(simulatedTargetAction, new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
    Check(!simulatedRevert.IsSuccessful && simulatedRevertManager.State == simulatedPrevious,
        "display.configure verified Revert flow", failures);
    var simulatedTimeoutManager = new TestDisplayManager(simulatedPrevious);
    var simulatedTimeout = await new DisplayConfigureActionHandler(simulatedTimeoutManager, new TestDisplayConfirmationService(false, TimeSpan.FromMilliseconds(350)))
        .ExecuteAsync(simulatedTargetAction, new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
    Check(!simulatedTimeout.IsSuccessful && simulatedTimeoutManager.State == simulatedPrevious,
        "display.configure verified timeout Revert flow", failures);
    var simulatedRestoreManager = new TestDisplayManager(simulatedPrevious);
    var simulatedRestoreHandler = new DisplayConfigureActionHandler(simulatedRestoreManager, new TestDisplayConfirmationService(true));
    var simulatedCapturedState = await simulatedRestoreHandler.CaptureStateAsync(simulatedTargetAction,
        new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
    await simulatedRestoreHandler.ExecuteAsync(simulatedTargetAction, new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
    await simulatedRestoreHandler.RestoreAsync(simulatedTargetAction, simulatedCapturedState!,
        new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
    Check(simulatedRestoreManager.State == simulatedPrevious, "display.configure Restore verifies previous mode", failures);
    var testDisplay = displays.FirstOrDefault(display => display.Modes.Any(mode =>
        mode.Width != display.CurrentWidth || mode.Height != display.CurrentHeight || mode.RefreshRate != display.CurrentRefreshRate));
    if (testDisplay is not null)
    {
        var originalDisplayState = await displayManager.GetCurrentStateAsync(testDisplay.DeviceId, testDisplay.DeviceName);
        var alternateMode = testDisplay.Modes
            .Where(mode => mode.Width != originalDisplayState.Width || mode.Height != originalDisplayState.Height || mode.RefreshRate != originalDisplayState.RefreshRate)
            .OrderByDescending(mode => mode.Width == originalDisplayState.Width && mode.Height == originalDisplayState.Height)
            .ThenBy(mode => Math.Abs(mode.RefreshRate - originalDisplayState.RefreshRate))
            .First();
        var displayAction = Action(ActionTypeIds.DisplayConfigure, new JsonObject
        {
            [ActionParameterNames.DisplayDeviceName] = testDisplay.DeviceName,
            [ActionParameterNames.DisplayDeviceId] = testDisplay.DeviceId,
            [ActionParameterNames.DisplayName] = testDisplay.DisplayName,
            [ActionParameterNames.DisplayWidth] = alternateMode.Width,
            [ActionParameterNames.DisplayHeight] = alternateMode.Height,
            [ActionParameterNames.DisplayRefreshRate] = alternateMode.RefreshRate
        });
        try
        {
            var keepResult = await new DisplayConfigureActionHandler(displayManager, new TestDisplayConfirmationService(true))
                .ExecuteAsync(displayAction, new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
            if (keepResult.IsSuccessful)
            {
                var changedDisplayState = await displayManager.GetCurrentStateAsync(testDisplay.DeviceId, testDisplay.DeviceName);
                Check(changedDisplayState.Width == alternateMode.Width && changedDisplayState.Height == alternateMode.Height &&
                      changedDisplayState.RefreshRate == alternateMode.RefreshRate,
                    "display.configure apply and Keep", failures);
            }
            else
            {
                Console.WriteLine($"LIMIT display mode change not permitted: {keepResult.Message}");
            }
        }
        finally
        {
            await displayManager.PersistAsync(originalDisplayState);
        }

        var revertResult = await new DisplayConfigureActionHandler(displayManager, new TestDisplayConfirmationService(false))
            .ExecuteAsync(displayAction, new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
        var revertedState = await displayManager.GetCurrentStateAsync(testDisplay.DeviceId, testDisplay.DeviceName);
        Check(!revertResult.IsSuccessful && revertedState.Width == originalDisplayState.Width &&
              revertedState.Height == originalDisplayState.Height && revertedState.RefreshRate == originalDisplayState.RefreshRate,
            "display.configure Revert restores previous mode", failures);

        var timeoutResult = await new DisplayConfigureActionHandler(displayManager, new TestDisplayConfirmationService(false, TimeSpan.FromMilliseconds(350)))
            .ExecuteAsync(displayAction, new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
        var timeoutState = await displayManager.GetCurrentStateAsync(testDisplay.DeviceId, testDisplay.DeviceName);
        Check(!timeoutResult.IsSuccessful && timeoutState.Width == originalDisplayState.Width &&
              timeoutState.Height == originalDisplayState.Height && timeoutState.RefreshRate == originalDisplayState.RefreshRate,
            "display.configure automatic timeout Revert", failures);
    }
    else
    {
        Console.WriteLine("SKIP display apply/revert: no alternate display mode is exposed by the current session.");
    }
}
finally
{
    Environment.SetEnvironmentVariable("SB_TEST_OUTPUT", null);
    Environment.SetEnvironmentVariable("SB_TEST_BATCH_OUTPUT", null);
    try { Directory.Delete(testRoot, true); } catch { }
}

Console.WriteLine(failures.Count == 0 ? "RUNTIME TESTS PASSED" : $"RUNTIME TEST FAILURES: {string.Join(", ", failures)}");
return failures.Count == 0 ? 0 : 1;

static void CreateTestImages(string directory)
{
    var red = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
        Enumerable.Repeat(new byte[] { 0, 0, 255, 255 }, 4).SelectMany(pixel => pixel).ToArray(), 8);
    var blue = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
        Enumerable.Repeat(new byte[] { 255, 0, 0, 255 }, 4).SelectMany(pixel => pixel).ToArray(), 8);
    red.Freeze();
    blue.Freeze();

    SaveEncoder(new JpegBitmapEncoder(), [red], Path.Combine(directory, "test.jpg"));
    SaveEncoder(new PngBitmapEncoder(), [red], Path.Combine(directory, "test.png"));
    SaveEncoder(new BmpBitmapEncoder(), [red], Path.Combine(directory, "test.bmp"));
    SaveEncoder(new GifBitmapEncoder(), [red, blue], Path.Combine(directory, "test.gif"));
}

static void SaveEncoder(BitmapEncoder encoder, IReadOnlyList<BitmapSource> frames, string path)
{
    foreach (var frame in frames) encoder.Frames.Add(BitmapFrame.Create(frame));
    using var stream = File.Create(path);
    encoder.Save(stream);
}

static double ContrastRatio(Color first, Color second)
{
    static double Linear(byte channel)
    {
        var value = channel / 255d;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    static double Luminance(Color color) =>
        0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);

    var light = Math.Max(Luminance(first), Luminance(second));
    var dark = Math.Min(Luminance(first), Luminance(second));
    return (light + 0.05) / (dark + 0.05);
}

static bool SemanticContrastIsAccessible(System.Windows.Application app)
{
    var pairs = new[]
    {
        ("PrimaryButtonBackground", "PrimaryButtonForeground"),
        ("PrimaryButtonHoverBackground", "PrimaryButtonHoverForeground"),
        ("PrimaryButtonPressedBackground", "PrimaryButtonPressedForeground"),
        ("PrimaryButtonDisabledBackground", "PrimaryButtonDisabledForeground"),
        ("SecondaryButtonBackground", "SecondaryButtonForeground"),
        ("SecondaryButtonHoverBackground", "SecondaryButtonHoverForeground"),
        ("SecondaryButtonPressedBackground", "SecondaryButtonPressedForeground"),
        ("SecondaryButtonDisabledBackground", "SecondaryButtonDisabledForeground"),
        ("MenuBackground", "MenuForeground"),
        ("MenuHoverBackground", "MenuHoverForeground"),
        ("MenuPressedBackground", "MenuPressedForeground"),
        ("MenuDisabledBackground", "MenuDisabledForeground"),
        ("SelectedBrush", "SelectionForeground"),
        ("HoverBrush", "HoverForeground"),
        ("DisabledInputBackground", "DisabledInputForeground"),
        ("BackgroundBrush", "TextPrimaryBrush"),
        ("BackgroundBrush", "TextSecondaryBrush")
    };
    var applicationBackground = RepresentativeColor(app.TryFindResource("BackgroundBrush") as Brush, Colors.Black);
    return pairs.All(pair => app.TryFindResource(pair.Item1) is Brush background &&
                             app.TryFindResource(pair.Item2) is SolidColorBrush foreground &&
                             BrushColors(background).All(color =>
                                 ContrastRatio(Composite(color, applicationBackground), foreground.Color) >= 4.5));
}

static bool RenderedSemanticControlsAreAccessible(System.Windows.Application app)
{
    var secondary = new System.Windows.Controls.Button();
    secondary.Style = app.TryFindResource(typeof(System.Windows.Controls.Button)) as System.Windows.Style;
    var primary = new System.Windows.Controls.Button
    {
        Style = app.TryFindResource("AccentButton") as System.Windows.Style
    };
    var menu = new System.Windows.Controls.ContextMenu();
    var menuItem = new System.Windows.Controls.MenuItem
    {
        Header = "Theme",
        Style = app.TryFindResource(typeof(System.Windows.Controls.MenuItem)) as System.Windows.Style
    };
    menu.Items.Add(menuItem);

    var normal = Matches(secondary, "SecondaryButtonBackground", "SecondaryButtonForeground") &&
                 Matches(primary, "PrimaryButtonBackground", "PrimaryButtonForeground") &&
                 Matches(menuItem, "MenuBackground", "MenuForeground");
    secondary.IsEnabled = false;
    primary.IsEnabled = false;
    menuItem.IsEnabled = false;
    return normal &&
           Matches(secondary, "SecondaryButtonDisabledBackground", "SecondaryButtonDisabledForeground") &&
           Matches(primary, "PrimaryButtonDisabledBackground", "PrimaryButtonDisabledForeground") &&
           Matches(menuItem, "MenuDisabledBackground", "MenuDisabledForeground");

    bool Matches(System.Windows.Controls.Control control, string backgroundKey, string foregroundKey) =>
        control.Background is Brush actualBackground && control.Foreground is SolidColorBrush actualForeground &&
        app.TryFindResource(backgroundKey) is Brush expectedBackground &&
        app.TryFindResource(foregroundKey) is SolidColorBrush expectedForeground &&
        BrushColors(actualBackground).SequenceEqual(BrushColors(expectedBackground)) &&
        actualForeground.Color == expectedForeground.Color;
}

static IReadOnlyList<Color> BrushColors(Brush brush) => brush switch
{
    SolidColorBrush solid => [solid.Color],
    GradientBrush gradient when gradient.GradientStops.Count > 0 => gradient.GradientStops.Select(stop => stop.Color).ToArray(),
    _ => [Colors.Black]
};

static Color RepresentativeColor(Brush? brush, Color fallback)
{
    if (brush is null) return fallback;
    var colors = BrushColors(brush);
    return Color.FromArgb((byte)Math.Round(colors.Average(value => value.A)),
        (byte)Math.Round(colors.Average(value => value.R)),
        (byte)Math.Round(colors.Average(value => value.G)),
        (byte)Math.Round(colors.Average(value => value.B)));
}

static Color Composite(Color foreground, Color background)
{
    if (foreground.A == byte.MaxValue) return foreground;
    var alpha = foreground.A / 255d;
    return Color.FromRgb(
        (byte)Math.Round(foreground.R * alpha + background.R * (1 - alpha)),
        (byte)Math.Round(foreground.G * alpha + background.G * (1 - alpha)),
        (byte)Math.Round(foreground.B * alpha + background.B * (1 - alpha)));
}

static async Task WaitUntilAsync(Func<bool> predicate)
{
    for (var attempt = 0; attempt < 100 && !predicate(); attempt++)
        await Task.Delay(20);
    if (!predicate()) throw new TimeoutException("The asynchronous test condition was not reached.");
}

sealed class TestDisplayConfirmationService(bool result, TimeSpan? delay = null) : IDisplayConfirmationService
{
    public async Task<bool> ConfirmAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (delay is { } wait) await Task.Delay(wait, cancellationToken);
        return result;
    }
}

sealed class TestLocalizationService : ILocalizationService
{
    public IReadOnlyList<LanguageDefinition> AvailableLanguages =>
        [new("en", "English", new Uri("Localization/Strings.en.xaml", UriKind.Relative))];
    public string CurrentLanguageId => "en";
    public string DetectSystemLanguage() => "en";
    public string ApplyLanguage(string? languageId) => languageId ?? "en";
    public string GetString(string resourceKey) => resourceKey;
    public string Format(string resourceKey, params object?[] arguments) => resourceKey == "CustomTheme.CopyName"
        ? $"{arguments[0]} — copy"
        : $"{resourceKey}: {string.Join(", ", arguments)}";
}

sealed class TestCatalogService : IProfileCatalogService
{
    public SwitchBoardCatalog Saved { get; private set; } = SwitchBoardCatalog.Empty();
    public int SaveCount { get; private set; }
    public Task<SwitchBoardCatalog> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Saved);
    public Task SaveAsync(SwitchBoardCatalog catalog, CancellationToken cancellationToken = default)
    {
        SaveCount++;
        Saved = JsonSerializer.Deserialize<SwitchBoardCatalog>(JsonSerializer.Serialize(catalog))!;
        return Task.CompletedTask;
    }
}

sealed class TestSettingsRepository : ISettingsRepository
{
    public UserSettings Saved { get; private set; } = new();
    public int SaveCount { get; private set; }
    public Task<UserSettings> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(Saved))!);
    public Task SaveAsync(UserSettings settings, CancellationToken cancellationToken = default)
    {
        SaveCount++;
        Saved = JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(settings))!;
        return Task.CompletedTask;
    }
}

sealed class TestThemeManager : IThemeManager
{
    public IReadOnlyList<ThemeDefinition> AvailableThemes =>
        [new(ThemeIds.Graphite, "Graphite", new Uri("Themes/GraphiteTheme.xaml", UriKind.Relative))];
    public string CurrentThemeId { get; private set; } = ThemeIds.Graphite;
    public string ApplyTheme(string? themeId, CustomThemeSettings? customTheme = null)
    {
        CurrentThemeId = customTheme is not null && !string.IsNullOrWhiteSpace(themeId) ? themeId : ThemeIds.Graphite;
        return CurrentThemeId;
    }
    public string ApplyTemporary(string draftThemeId, CustomThemeSettings draft)
    {
        CurrentThemeId = draftThemeId;
        LastTemporarySettings = draft.Clone();
        TemporaryApplyCount++;
        return CurrentThemeId;
    }
    public CustomThemeSettings? LastTemporarySettings { get; private set; }
    public int TemporaryApplyCount { get; private set; }
    public CustomThemeSettings CreateEditableCopy(string builtInThemeId) => CustomThemeSettings.CreateDefault();
}

sealed class TestCustomThemeEditorService : ICustomThemeEditorService
{
    public Queue<CustomThemeEditResult?> Results { get; } = [];
    public Queue<Action<CustomThemeEditRequest>> EditActions { get; } = [];
    public Queue<string?> RenameResults { get; } = [];
    public List<CustomThemeEditRequest> Requests { get; } = [];
    public bool EchoWhenEmpty { get; set; }
    public CustomThemeEditResult? Edit(CustomThemeEditRequest request)
    {
        Requests.Add(request with { Colors = request.Colors.Clone() });
        if (EditActions.Count > 0) EditActions.Dequeue()(request);
        return Results.Count > 0 ? Results.Dequeue()
            : EchoWhenEmpty ? new(request.Name, request.Colors.Clone()) : null;
    }
    public string? Rename(string currentName, IReadOnlyCollection<string> unavailableNames) =>
        RenameResults.Count > 0 ? RenameResults.Dequeue() : null;
}

sealed class TestCompletionBehavior : IProfileCompletionBehavior
{
    public void HandleSuccessfulCompletion(ProfileDefinition profile) { }
}

sealed class TestDialogService : IUserDialogService
{
    public bool Confirm(string title, string message) => true;
    public string? SelectFile(string title, string filter, string? initialPath = null) => null;
    public SwitchBoard.Services.Discovery.ProcessCandidate? SelectProcess(string title) => null;
    public SwitchBoard.Services.Discovery.ServiceCandidate? SelectService(string title) => null;
    public SwitchBoard.Services.Discovery.PowerPlanCandidate? SelectPowerPlan(string title) => null;
    public SwitchBoard.Services.Discovery.DisplayCandidate? SelectDisplay(string title) => null;
    public SwitchBoard.Services.Discovery.ProgramCandidate? FindProgram(string title) => null;
    public SwitchBoard.Services.Discovery.AudioDeviceCandidate? SelectAudioDevice(string title, bool input) => null;
    public SwitchBoard.Services.Discovery.DeviceCandidate? SelectDevice(string title) => null;
}

sealed class TestServiceManager(string initialState, bool changeSucceeds,
    string initialStartupType = ServiceStartupTypeIds.Automatic) : IWindowsServiceManager
{
    private string _state = initialState;
    private string _startupType = initialStartupType;
    public WindowsServiceSnapshot Snapshot => new(ToDisplay(_state), StartupDisplay(_startupType));
    public Task<IReadOnlyList<SwitchBoard.Services.Discovery.ServiceCandidate>> GetServicesAsync(
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SwitchBoard.Services.Discovery.ServiceCandidate>>([]);
    public Task<string> GetStateAsync(string serviceName, CancellationToken cancellationToken = default) =>
        Task.FromResult(_state);
    public Task<WindowsServiceSnapshot> GetSnapshotAsync(string serviceName,
        CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);
    public Task<WindowsServiceOperationResult> SetStateAsync(string serviceName, string desiredState, TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var before = ToDisplay(_state);
        if (string.Equals(_state, desiredState, StringComparison.OrdinalIgnoreCase))
            return Task.FromResult(new WindowsServiceOperationResult(true, true, StateBefore: before,
                CurrentState: before, ExpectedState: ToDisplay(desiredState)));
        if (!changeSucceeds)
            return Task.FromResult(new WindowsServiceOperationResult(false, false, "Access denied.", before,
                before, ToDisplay(desiredState), 5));
        _state = desiredState;
        return Task.FromResult(new WindowsServiceOperationResult(true, false, StateBefore: before,
            CurrentState: ToDisplay(_state), ExpectedState: ToDisplay(desiredState)));
    }
    public Task<WindowsServiceConfigurationResult> SetConfigurationAsync(string serviceName, string desiredState,
        string desiredStartupType, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var before = new WindowsServiceSnapshot(ToDisplay(_state), StartupDisplay(_startupType));
        if (!changeSucceeds)
            return Task.FromResult(new WindowsServiceConfigurationResult(false, false, before, before,
                desiredState, desiredStartupType, "Access denied.", 5));
        if (desiredState != ServiceDesiredStateIds.Unchanged) _state = desiredState;
        if (desiredStartupType != ServiceStartupTypeIds.Unchanged) _startupType = desiredStartupType;
        var current = new WindowsServiceSnapshot(ToDisplay(_state), StartupDisplay(_startupType));
        return Task.FromResult(new WindowsServiceConfigurationResult(true, before == current, before, current,
            desiredState, desiredStartupType));
    }
    private static string ToDisplay(string state) =>
        string.Equals(state, ServiceDesiredStateIds.Running, StringComparison.OrdinalIgnoreCase) ? "Running" : "Stopped";
    private static string StartupDisplay(string value) => value switch
    {
        ServiceStartupTypeIds.Automatic => "Automatic",
        ServiceStartupTypeIds.AutomaticDelayed => "Automatic (Delayed Start)",
        ServiceStartupTypeIds.Manual => "Manual",
        ServiceStartupTypeIds.Disabled => "Disabled",
        _ => value
    };
}

sealed class TestDisplayManager(DisplayModeState initialState) : IDisplayManager
{
    public DisplayModeState State { get; private set; } = initialState;
    public Task<IReadOnlyList<SwitchBoard.Services.Discovery.DisplayCandidate>> GetDisplaysAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SwitchBoard.Services.Discovery.DisplayCandidate>>([]);
    public Task<DisplayModeState> GetCurrentStateAsync(string deviceId, string deviceName, CancellationToken cancellationToken = default) =>
        Task.FromResult(State);
    public Task ApplyTemporaryAsync(DisplayModeState state, CancellationToken cancellationToken = default) { State = state; return Task.CompletedTask; }
    public Task PersistAsync(DisplayModeState state, CancellationToken cancellationToken = default) { State = state; return Task.CompletedTask; }
    public Task RestoreAsync(DisplayModeState state, CancellationToken cancellationToken = default) { State = state; return Task.CompletedTask; }
}

sealed class TestReversibleHandler(List<string> restoreOrder, IExecutionSessionRepository repository) : IReversibleActionHandler
{
    public const string TypeId = "test.reversible";
    public string ActionType => TypeId;
    public bool CaptureWasPersistedBeforeExecute { get; private set; } = true;
    public Dictionary<string, int> RestoreAttempts { get; } = [];
    private readonly HashSet<string> _failedOnce = [];

    public Task<JsonObject?> CaptureStateAsync(ActionDefinition action, ActionExecutionContext context,
        CancellationToken cancellationToken) => Task.FromResult<JsonObject?>(new JsonObject
        {
            ["key"] = action.Parameters["key"]?.GetValue<string>(),
            ["failOnce"] = action.Parameters["failOnce"]?.GetValue<bool>() ?? false,
            ["restoreDelayMs"] = action.Parameters["restoreDelayMs"]?.GetValue<int>() ?? 0
        });

    public async Task<ActionExecutionResult> ExecuteAsync(ActionDefinition action, ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var session = await repository.LoadAsync(context.SessionId, cancellationToken);
        var item = session?.Actions.SingleOrDefault(candidate => candidate.ActionId == action.Id);
        CaptureWasPersistedBeforeExecute &= item?.PreviousState is not null && item.ExecutionStatus == PersistentActionExecutionStatus.Running;
        return ActionExecutionResult.Success();
    }

    public async Task<ActionExecutionResult> RestoreAsync(ActionDefinition action, JsonObject restoreState, ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var key = restoreState["key"]?.GetValue<string>() ?? string.Empty;
        restoreOrder.Add(key);
        RestoreAttempts[key] = RestoreAttempts.GetValueOrDefault(key) + 1;
        if ((restoreState["failOnce"]?.GetValue<bool>() ?? false) && _failedOnce.Add(key))
            throw new InvalidOperationException("Simulated first restore failure.");
        var delay = restoreState["restoreDelayMs"]?.GetValue<int>() ?? 0;
        if (delay > 0) await Task.Delay(delay, cancellationToken);
        return ActionExecutionResult.Success("Verified test restore.");
    }
}
