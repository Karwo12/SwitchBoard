using SwitchBoard.RuntimeTests.TestInfrastructure;

namespace SwitchBoard.RuntimeTests.Actions;

[Collection("Windows runtime")]
public sealed class AutomationAndRestoreTests : RuntimeTestBase
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task ProfileRun_NestedProfile_ExecutesInOrderAndJournalsParentAction()
    {
        using var context = new RuntimeTestContext();
        var activity = new ActivityService();
        var profiles = new Dictionary<Guid, ProfileDefinition>();
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
        profileB.Actions[0].SortOrder = 0;
        profileB.Actions[1].SortOrder = 1;
        profiles[profileA.Id] = profileA;
        profiles[profileB.Id] = profileB;
        var runner = CreateAutomationRunner(context, activity, profiles);

        var session = await runner.RunAsync(profileB);
        var messages = activity.Entries.Where(entry => entry.Message.StartsWith("Notification "))
            .Select(entry => entry.Message).ToList();

        Assert.Equal(ExecutionSessionStatus.Completed, session.Status);
        Assert.True(messages.IndexOf("Notification A") < messages.IndexOf("Notification B"));
        Assert.Contains(session.Journal, item => item.ParentActionId == profileB.Actions[0].Id);
        Assert.Contains(activity.Entries, entry => entry.Message == "Profile started: B");
        Assert.Contains(activity.Entries, entry => entry.Message == "Profile started: A");
        Assert.Contains(activity.Entries, entry => entry.Message.StartsWith("Action: "));
        Assert.Contains(activity.Entries, entry => entry.Message == "Profile completed: B");
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task ProfileRun_CyclicProfiles_FailWithoutRecursion()
    {
        using var context = new RuntimeTestContext();
        var activity = new ActivityService();
        var profiles = new Dictionary<Guid, ProfileDefinition>();
        var profileA = new ProfileDefinition { Name = "A", CategoryId = Guid.NewGuid() };
        var profileB = new ProfileDefinition { Name = "B", CategoryId = profileA.CategoryId };
        profileA.Actions.Add(Action(ActionTypeIds.ProfileRun, new JsonObject
            { [ActionParameterNames.ProfileId] = profileB.Id.ToString("D") }));
        profileB.Actions.Add(Action(ActionTypeIds.ProfileRun, new JsonObject
            { [ActionParameterNames.ProfileId] = profileA.Id.ToString("D") }));
        profiles[profileA.Id] = profileA;
        profiles[profileB.Id] = profileB;
        var runner = CreateAutomationRunner(context, activity, profiles);

        var session = await runner.RunAsync(profileA);

        Assert.Equal(ExecutionSessionStatus.CompletedWithErrors, session.Status);
        Assert.Contains(session.Journal, item => item.Status == ActionJournalStatus.Failed);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task Condition_WhenTrue_ExecutesThenBranchOnly()
    {
        using var context = new RuntimeTestContext();
        var activity = new ActivityService();
        var profiles = new Dictionary<Guid, ProfileDefinition>();
        var condition = CreateProcessCondition(ConditionTypeIds.ProcessNotRunning, "missing", "running", "not running");
        var profile = new ProfileDefinition { Name = "Condition true", CategoryId = Guid.NewGuid(), Actions = [condition] };
        profiles[profile.Id] = profile;
        var runner = CreateAutomationRunner(context, activity, profiles);

        var session = await runner.RunAsync(profile);

        Assert.Equal(ExecutionSessionStatus.Completed, session.Status);
        Assert.Contains(session.Journal, item => item.Branch == "then");
        Assert.Contains(activity.Entries, entry => entry.Message == "running");
        Assert.DoesNotContain(activity.Entries, entry => entry.Message == "not running");
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task Condition_WhenFalse_ExecutesOtherwiseBranchOnly()
    {
        using var context = new RuntimeTestContext();
        var activity = new ActivityService();
        var profiles = new Dictionary<Guid, ProfileDefinition>();
        var condition = CreateProcessCondition(ConditionTypeIds.ProcessRunning, "missing", "running", "not running");
        var profile = new ProfileDefinition { Name = "Condition false", CategoryId = Guid.NewGuid(), Actions = [condition] };
        profiles[profile.Id] = profile;
        var runner = CreateAutomationRunner(context, activity, profiles);

        var session = await runner.RunAsync(profile);

        Assert.Equal(ExecutionSessionStatus.Completed, session.Status);
        Assert.Contains(session.Journal, item => item.Branch == "else");
        Assert.Contains(activity.Entries, entry => entry.Message == "not running");
        Assert.DoesNotContain(activity.Entries, entry => entry.Message == "running");
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task Condition_NestedProgramRun_ExecutesTheSelectedAction()
    {
        using var context = new RuntimeTestContext();
        var activity = new ActivityService();
        var profiles = new Dictionary<Guid, ProfileDefinition>();
        var output = Path.Combine(context.Root, "if-program.txt");
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        Assert.True(File.Exists(powershell), "Windows PowerShell is required for the nested program test.");
        var nested = Action(ActionTypeIds.ProgramRun, new JsonObject
        {
            [ActionParameterNames.Target] = powershell,
            [ActionParameterNames.Arguments] = $"-NoProfile -Command \"Set-Content -LiteralPath '{output}' -Value nested; Start-Sleep -Seconds 15\"",
            [ActionParameterNames.InstanceBehavior] = InstanceBehaviorIds.StartAnother
        });
        nested.RestoreBehavior = ActionRestoreBehavior.CloseIfStartedBySwitchBoard;
        var condition = Action(ActionTypeIds.ConditionIf, new JsonObject
        {
            [ActionParameterNames.ConditionType] = ConditionTypeIds.FileNotExists,
            [ActionParameterNames.ConditionValue] = output,
            [ActionParameterNames.ThenActions] = new JsonArray(JsonSerializer.SerializeToNode(nested)),
            [ActionParameterNames.ElseActions] = new JsonArray()
        });
        var profile = new ProfileDefinition { Name = "Nested program condition", CategoryId = Guid.NewGuid(), Actions = [condition] };
        profiles[profile.Id] = profile;
        var runner = CreateAutomationRunner(context, activity, profiles);

        ExecutionSession? session = null;
        try
        {
            session = await runner.RunAsync(profile);
            var persisted = await context.SessionRepository.LoadAsync(session.Id);
            await TestHelpers.WaitUntilAsync(() => File.Exists(output), TimeSpan.FromSeconds(15),
                timeoutDetails: () => DescribeNestedProgramTimeout(session!, persisted, nested.Id));

            Assert.Equal(ExecutionSessionStatus.Completed, session.Status);
            Assert.True(File.Exists(output));
        }
        finally
        {
            var pending = await context.SessionRepository.GetLatestPendingAsync(profile.Id);
            if (pending is not null)
            {
                var restored = await new ProfileRestoreRunner(context.Registry, context.SessionRepository)
                    .RunAsync(pending);
                Assert.Equal(PersistentSessionStatus.Restored, restored.Status);
                Assert.Empty(restored.GetPendingRestoreEntries());
            }
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task ProfileRunner_Retry_SucceedsOnTheConfiguredFinalAttempt()
    {
        using var context = new RuntimeTestContext();
        var flaky = new TestFlakyHandler();
        var registry = new ActionRegistry([flaky]);
        var runner = new ProfileRunner(registry, context.SessionRepository);
        var profile = new ProfileDefinition { Name = "Retry", CategoryId = Guid.NewGuid(), Actions =
        [new ActionDefinition { Type = TestFlakyHandler.TypeId, RetryOnFailure = true, MaximumAttempts = 3,
            RetryDelay = TimeSpan.FromMilliseconds(20), FailurePolicy = ActionFailurePolicy.Stop }] };

        var session = await runner.RunAsync(profile);

        Assert.Equal(ExecutionSessionStatus.Completed, session.Status);
        Assert.Equal(3, flaky.Attempts);
        Assert.Equal(3, session.Journal.Single().AttemptCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task ProfileRunner_RetryExhaustion_FailsAfterMaximumAttempts()
    {
        using var context = new RuntimeTestContext();
        var flaky = new TestFlakyHandler();
        var registry = new ActionRegistry([flaky]);
        var runner = new ProfileRunner(registry, context.SessionRepository);
        var profile = new ProfileDefinition { Name = "Retry exhausted", CategoryId = Guid.NewGuid(), Actions =
        [new ActionDefinition { Type = TestFlakyHandler.TypeId, RetryOnFailure = true, MaximumAttempts = 3,
            RetryDelay = TimeSpan.FromMilliseconds(10), FailurePolicy = ActionFailurePolicy.Stop,
            Parameters = new JsonObject { ["failAlways"] = true } }] };

        var session = await runner.RunAsync(profile);

        Assert.Equal(ExecutionSessionStatus.Failed, session.Status);
        Assert.Equal(3, flaky.Attempts);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task ProfileRunner_WaitTimeout_IsReportedAsFailure()
    {
        using var context = new RuntimeTestContext();
        var registry = new ActionRegistry([
            new WaitProcessActionHandler(ActionTypeIds.WaitProcessStart)
        ]);
        var runner = new ProfileRunner(registry, context.SessionRepository);
        var profile = new ProfileDefinition { Name = "Timeout", CategoryId = Guid.NewGuid(), Actions =
        [new ActionDefinition { Type = ActionTypeIds.WaitProcessStart, Timeout = TimeSpan.FromMilliseconds(250),
            FailurePolicy = ActionFailurePolicy.Stop, Parameters = new JsonObject
            { [ActionParameterNames.ProcessName] = $"missing-{Guid.NewGuid():N}" } }] };

        var session = await runner.RunAsync(profile);

        Assert.Equal(ExecutionSessionStatus.Failed, session.Status);
        Assert.Contains("timed out", session.Journal.Single().ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ActivityService_BufferIsBoundedAndCanBeCleared()
    {
        var activity = new ActivityService();
        for (var index = 0; index < 600; index++) activity.Add(ActivityLevel.Info, $"event {index}");

        Assert.Equal(300, activity.Entries.Count);
        Assert.Equal("event 300", activity.Entries[0].Message);
        activity.Clear();
        Assert.Empty(activity.Entries);
    }

    [EnvironmentFact("AudioEndpoint")]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task AudioManager_DefaultOutputVolume_CanBeChangedAndRestored()
    {
        using var context = new RuntimeTestContext();
        var audioManager = new WindowsAudioManager();
        IReadOnlyList<AudioDeviceCandidate> devices;
        try { devices = await audioManager.GetDevicesAsync(); }
        catch (Exception) { return; }
        if (devices.Count == 0) return;
        Assert.All(devices, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Id));
            Assert.False(string.IsNullOrWhiteSpace(item.FriendlyName));
        });
        var output = devices.FirstOrDefault(item => !item.IsInput && item.IsDefaultMultimedia);
        if (output is null) return;

        var original = await audioManager.GetMasterVolumeAsync();
        var target = original.Volume > 0.02f ? original.Volume - 0.01f : original.Volume + 0.01f;
        await audioManager.SetMasterVolumeAsync(target, original.Muted);
        try
        {
            var changed = await audioManager.GetMasterVolumeAsync();
            Assert.InRange(Math.Abs(changed.Volume - target), 0, 0.02f);
        }
        finally
        {
            await audioManager.SetMasterVolumeAsync(original.Volume, original.Muted);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task WindowsDeviceManager_ReturnsStableIdsAndProtectsCriticalClasses()
    {
        using var context = new RuntimeTestContext();
        IReadOnlyList<DeviceCandidate> devices;
        try { devices = await new WindowsDeviceManager().GetDevicesAsync(); }
        catch (Exception) { return; }

        Assert.NotEmpty(devices);
        Assert.All(devices, item => Assert.False(string.IsNullOrWhiteSpace(item.InstanceId)));
        Assert.All(devices.Where(item => item.DeviceClass is "System" or "DiskDrive" or "Display"),
            item => Assert.True(item.IsCritical));
    }

    private static ActionDefinition CreateProcessCondition(string conditionType, string value,
        string thenMessage, string elseMessage)
    {
        var thenAction = Action(ActionTypeIds.NotificationShow, new JsonObject
        {
            [ActionParameterNames.NotificationMessage] = thenMessage,
            [ActionParameterNames.NotificationLevel] = NotificationLevelIds.Info
        });
        var elseAction = Action(ActionTypeIds.NotificationShow, new JsonObject
        {
            [ActionParameterNames.NotificationMessage] = elseMessage,
            [ActionParameterNames.NotificationLevel] = NotificationLevelIds.Info
        });
        return Action(ActionTypeIds.ConditionIf, new JsonObject
        {
            [ActionParameterNames.ConditionType] = conditionType,
            [ActionParameterNames.ConditionValue] = value,
            [ActionParameterNames.ThenActions] = new JsonArray(JsonSerializer.SerializeToNode(thenAction)),
            [ActionParameterNames.ElseActions] = new JsonArray(JsonSerializer.SerializeToNode(elseAction))
        });
    }

    private static ProfileRunner CreateAutomationRunner(RuntimeTestContext context, ActivityService activity,
        Dictionary<Guid, ProfileDefinition> profiles)
    {
        var registry = new ActionRegistry([
            new ProfileRunActionHandler(), new NotificationShowActionHandler(activity),
            new ConditionIfActionHandler(context.ServiceManager),
            new WaitProcessActionHandler(ActionTypeIds.WaitProcessStart), new ProgramRunActionHandler()
        ]);
        return new ProfileRunner(registry, context.SessionRepository,
            profileResolver: id => profiles.GetValueOrDefault(id), activity: activity);
    }

    private static string DescribeNestedProgramTimeout(ExecutionSession session,
        PersistentExecutionSession? persisted, Guid nestedActionId)
    {
        var persistedAction = persisted?.Actions.LastOrDefault(item => item.ActionId == nestedActionId);
        var processIds = persistedAction?.PreviousState?["launchedProcesses"] is JsonArray launched
            ? launched.OfType<JsonObject>()
                .Select(item => item["processId"]?.GetValue<int>() ?? 0)
                .Where(processId => processId > 0)
                .ToArray()
            : [];
        var activeProcessIds = processIds.Where(IsProcessRunning).ToArray();
        var journal = string.Join(
            "; ",
            session.Journal.Select(item =>
                $"{item.ActionType}:{item.Status}:{item.ErrorMessage ?? "no error"}"));

        return $"ExecutionStatus={session.Status}; " +
               $"PersistentStatus={persisted?.Status.ToString() ?? "unavailable"}; " +
               $"PowerShellStarted={processIds.Length > 0}; " +
               $"PowerShellActivePids=[{string.Join(",", activeProcessIds)}]; " +
               $"Journal=[{journal}]";
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
    }
}
