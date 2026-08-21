using SwitchBoard.RuntimeTests.TestInfrastructure;

namespace SwitchBoard.RuntimeTests.Actions;

[Collection("Windows runtime")]
public sealed class PersistenceAndRestoreTests : RuntimeTestBase
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task ProfileRestore_PersistsCaptureAndRestoresInReverseOrder()
    {
        using var context = new RuntimeTestContext();
        var profile = CreateReversibleProfile("Persistent restore test", "first", "second");
        var session = await context.Runner.RunAsync(profile);
        using var restartedRepository = new JsonExecutionSessionRepository(new AppDataPaths(Path.Combine(context.Root, "appdata")));
        var pending = await restartedRepository.GetLatestPendingAsync(profile.Id);

        Assert.Equal(ExecutionSessionStatus.Completed, session.Status);
        Assert.Equal(2, pending?.PendingRestoreCount);
        Assert.True(context.ReversibleHandler.CaptureWasPersistedBeforeExecute);
        Assert.NotNull(pending);

        var restoreActivity = new ActivityService();
        await new ProfileRestoreRunner(context.Registry, context.SessionRepository, activity: restoreActivity).RunAsync(pending!);
        var restored = await context.SessionRepository.LoadAsync(pending!.SessionId);
        Assert.Equal(new[] { "second", "first" }, context.RestoreOrder);
        Assert.Equal(PersistentSessionStatus.Restored, restored?.Status);
        Assert.Equal(0, restored?.PendingRestoreCount);
        Assert.Contains(restoreActivity.Entries, entry => entry.Message == "Restoring profile: Persistent restore test");
        Assert.Equal(2, restoreActivity.Entries.Count(entry => entry.Message.StartsWith("Restoring action: ")));
        Assert.Equal(2, restoreActivity.Entries.Count(entry => entry.Message.StartsWith("Action restored: ")));
        Assert.Contains(restoreActivity.Entries, entry => entry.Message == "Profile restored: Persistent restore test");
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task ProfileRestore_PartialFailure_RetriesOnlyTheFailedAction()
    {
        using var context = new RuntimeTestContext();
        var profile = CreateReversibleProfile("Partial restore test", "partial-first", "partial-second");
        profile.Actions[0].Parameters!["failOnce"] = true;
        await context.Runner.RunAsync(profile);
        var pending = await context.SessionRepository.GetLatestPendingAsync(profile.Id);
        Assert.NotNull(pending);

        var restoreRunner = new ProfileRestoreRunner(context.Registry, context.SessionRepository);
        var partial = await restoreRunner.RunAsync(pending!);
        Assert.Equal(PersistentSessionStatus.PartiallyRestored, partial.Status);
        Assert.Equal(1, partial.PendingRestoreCount);
        partial = await restoreRunner.RunAsync(partial);
        Assert.Equal(PersistentSessionStatus.Restored, partial.Status);
        Assert.Equal(1, context.ReversibleHandler.RestoreAttempts["partial-second"]);
        Assert.Equal(2, context.ReversibleHandler.RestoreAttempts["partial-first"]);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task ServiceRestore_CapturesAndRestoresStateAndStartupType()
    {
        using var context = new RuntimeTestContext();
        var root = Path.Combine(context.Root, "service-combined");
        var paths = new AppDataPaths(root);
        var manager = new TestServiceManager(ServiceDesiredStateIds.Running, changeSucceeds: true);
        using var repository = new JsonExecutionSessionRepository(paths);
        var activity = new ActivityService(paths);
        var registry = new ActionRegistry([new ServiceSetStateActionHandler(manager)]);
        var action = CreateCombinedServiceAction();
        var profile = new ProfileDefinition
        {
            CategoryId = Guid.NewGuid(), Name = "Combined service configuration", Actions = [action]
        };

        await new ProfileRunner(registry, repository, activity: activity).RunAsync(profile);
        var pending = await repository.GetLatestPendingAsync(profile.Id);
        var saved = pending?.Actions.Single();
        Assert.Equal(new WindowsServiceSnapshot("Stopped", "Disabled"), manager.Snapshot);
        Assert.Equal(ServiceDesiredStateIds.Running, saved?.PreviousState?["previousState"]?.GetValue<string>());
        Assert.Equal(ServiceStartupTypeIds.Automatic, saved?.PreviousState?["previousStartupType"]?.GetValue<string>());
        Assert.True(saved?.RequiresRestore == true);
        Assert.Equal(1, pending?.PendingRestoreCount);

        var reloadedActivity = new ActivityService(paths);
        Assert.Single(reloadedActivity.SystemChanges);
        Assert.Equal(SystemChangeStatuses.Pending, reloadedActivity.SystemChanges[0].Status);
        Assert.NotEmpty(reloadedActivity.HistoryEntries);
        Assert.NotEmpty(Directory.EnumerateFiles(paths.LogsDirectory, "activity-*.jsonl"));

        Assert.NotNull(pending);
        await new ProfileRestoreRunner(registry, repository, activity: activity).RunAsync(pending!);
        Assert.Equal(new WindowsServiceSnapshot("Running", "Automatic"), manager.Snapshot);
        Assert.Equal(SystemChangeStatuses.Restored, activity.SystemChanges.Single().Status);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ServiceRestore_Discard_PersistsTheDiscardedSystemChange()
    {
        using var context = new RuntimeTestContext();
        var root = Path.Combine(context.Root, "service-discard");
        var paths = new AppDataPaths(root);
        var manager = new TestServiceManager(ServiceDesiredStateIds.Running, changeSucceeds: true);
        using var repository = new JsonExecutionSessionRepository(paths);
        var activity = new ActivityService(paths);
        var registry = new ActionRegistry([new ServiceSetStateActionHandler(manager)]);
        var profile = new ProfileDefinition
        {
            CategoryId = Guid.NewGuid(), Name = "Combined service discard", Actions = [CreateCombinedServiceAction()]
        };

        await new ProfileRunner(registry, repository, activity: activity).RunAsync(profile);
        var pending = await repository.GetLatestPendingAsync(profile.Id);
        Assert.NotNull(pending);
        foreach (var item in pending!.GetPendingRestoreEntries())
        {
            activity.Record(new PersistentActivityRecord
            {
                SessionId = pending.SessionId, ProfileId = pending.ProfileId, ProfileName = pending.ProfileName,
                ActionId = item.ActionId, ActionType = item.ActionType, FriendlyName = item.ActionName ?? item.ActionType,
                EventType = ActivityEventTypes.Discard, Level = ActivityLevel.Warning,
                StateBefore = item.PreviousState?.DeepClone().AsObject(), StateAfter = item.StateAfter?.DeepClone().AsObject(),
                RestoreStatus = SystemChangeStatuses.Discarded, Result = "discarded",
                Message = "Restore discarded by persistence test."
            });
        }
        pending.Status = PersistentSessionStatus.Discarded;
        await repository.SaveAsync(pending);

        var afterRestart = new ActivityService(paths);
        Assert.Equal(new WindowsServiceSnapshot("Stopped", "Disabled"), manager.Snapshot);
        Assert.Contains(afterRestart.SystemChanges, change => change.SessionId == pending.SessionId &&
                                                              change.Status == SystemChangeStatuses.Discarded);
        await manager.SetConfigurationAsync("Spooler", ServiceDesiredStateIds.Running,
            ServiceStartupTypeIds.Automatic, TimeSpan.FromSeconds(1));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ActivityRetention_RemovesResolvedHistory()
    {
        using var context = new RuntimeTestContext();
        var paths = new AppDataPaths(Path.Combine(context.Root, "activity-retention-resolved"));
        var activity = new ActivityService(paths);
        activity.Record(new PersistentActivityRecord
        {
            Timestamp = DateTimeOffset.UtcNow.AddDays(-100), SessionId = Guid.NewGuid(), ProfileId = Guid.NewGuid(),
            ActionId = Guid.NewGuid(), ActionType = ActionTypeIds.PowerSetPlan, FriendlyName = "Old resolved change",
            EventType = ActivityEventTypes.Restore, Level = ActivityLevel.Success,
            RestoreStatus = SystemChangeStatuses.Restored, Message = "Old change restored."
        });
        _ = new ActivityService(paths);

        Assert.Empty(Directory.EnumerateFiles(paths.LogsDirectory, "activity-*.jsonl"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ActivityRetention_PreservesPendingHistory()
    {
        using var context = new RuntimeTestContext();
        var paths = new AppDataPaths(Path.Combine(context.Root, "activity-retention-pending"));
        var activity = new ActivityService(paths);
        activity.Record(new PersistentActivityRecord
        {
            Timestamp = DateTimeOffset.UtcNow.AddDays(-100), SessionId = Guid.NewGuid(), ProfileId = Guid.NewGuid(),
            ActionId = Guid.NewGuid(), ActionType = ActionTypeIds.PowerSetPlan, FriendlyName = "Old pending change",
            EventType = ActivityEventTypes.Verify, Level = ActivityLevel.Success,
            StateBefore = new JsonObject { ["previousPowerPlanGuid"] = Guid.NewGuid().ToString() },
            RestoreStatus = SystemChangeStatuses.Pending, Message = "Old change still pending."
        });
        _ = new ActivityService(paths);

        Assert.NotEmpty(Directory.EnumerateFiles(paths.LogsDirectory, "activity-*.jsonl"));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ServiceFailureWithoutObservedChange_DoesNotCreatePendingRestore()
    {
        using var context = new RuntimeTestContext();
        var manager = new TestServiceManager(ServiceDesiredStateIds.Running, changeSucceeds: false);
        using var repository = new JsonExecutionSessionRepository(new AppDataPaths(Path.Combine(context.Root, "service-journal")));
        var registry = new ActionRegistry([new ServiceSetStateActionHandler(manager)]);
        var action = Action(ActionTypeIds.ServiceSetState, new JsonObject
        {
            [ActionParameterNames.ServiceName] = "TestSvc",
            [ActionParameterNames.ServiceDisplayName] = "Test service",
            [ActionParameterNames.DesiredState] = ServiceDesiredStateIds.Stopped
        });
        action.RestoreBehavior = ActionRestoreBehavior.RestorePreviousState;
        var profile = new ProfileDefinition
        {
            CategoryId = Guid.NewGuid(), Name = "Service no-change failure", Actions = [action]
        };

        var execution = await new ProfileRunner(registry, repository).RunAsync(profile);
        var saved = await repository.LoadAsync(execution.Id);

        Assert.True(saved?.Actions.Single().ExecutionAttempted);
        Assert.True(saved?.Actions.Single().ExecutionVerified);
        Assert.False(saved?.Actions.Single().RequiresRestore);
        Assert.Equal(0, saved?.PendingRestoreCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ProfileRestore_Cancellation_PreservesCompletedRestores()
    {
        using var context = new RuntimeTestContext();
        var profile = CreateReversibleProfile("Cancelled restore test", "cancel-first", "cancel-slow", "cancel-last");
        profile.Actions[1].Parameters!["restoreDelayMs"] = 2000;
        await context.Runner.RunAsync(profile);
        var pending = await context.SessionRepository.GetLatestPendingAsync(profile.Id);
        Assert.NotNull(pending);

        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(350));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ProfileRestoreRunner(context.Registry, context.SessionRepository).RunAsync(pending!,
                cancellationToken: cancellation.Token));
        var after = await context.SessionRepository.LoadAsync(pending!.SessionId);

        Assert.Equal(PersistentSessionStatus.RestoreCancelled, after?.Status);
        Assert.Equal(2, after?.PendingRestoreCount);
        Assert.True(after?.Actions.Single(item => item.PreviousState?["key"]?.GetValue<string>() == "cancel-last").IsRestored);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SessionRecovery_Discard_PreservesAuditAndSuppressesFutureRestore()
    {
        using var context = new RuntimeTestContext();
        var session = new PersistentExecutionSession
        {
            ProfileId = Guid.NewGuid(), ProfileName = "Interrupted", Status = PersistentSessionStatus.Executing,
            Actions = [new PersistentSessionAction
            {
                ActionId = Guid.NewGuid(), ActionType = TestReversibleHandler.TypeId, RequiresRestore = true,
                PreviousState = new JsonObject { ["key"] = "recovery" }, RestoreStatus = PersistentActionRestoreStatus.Pending
            }]
        };
        await context.SessionRepository.SaveAsync(session);
        await context.SessionRepository.MaintainAsync(TimeSpan.FromDays(30));
        var recovered = await context.SessionRepository.LoadAsync(session.SessionId);
        Assert.Equal(PersistentSessionStatus.RecoveryRequired, recovered?.Status);
        Assert.Equal(1, recovered?.PendingRestoreCount);

        var discarded = recovered!.DiscardPendingRestore();
        discarded[0].RestoreMessage = "discarded by test";
        await context.SessionRepository.SaveAsync(recovered);
        var reloaded = await context.SessionRepository.LoadAsync(recovered.SessionId);
        Assert.Single(discarded);
        Assert.Equal(PersistentSessionStatus.Discarded, reloaded?.Status);
        Assert.Equal(0, reloaded?.PendingRestoreCount);
        Assert.Null(await context.SessionRepository.GetLatestPendingAsync(recovered.ProfileId));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void SessionPendingRestoreCounter_ContainsOnlyVerifiedChangedActions()
    {
        var session = new PersistentExecutionSession
        {
            ProfileId = Guid.NewGuid(), ProfileName = "Restore counter selector", Status = PersistentSessionStatus.RestorePending,
            Actions =
            [
                new PersistentSessionAction { ActionType = ActionTypeIds.ProgramRun, ActionName = "Microsoft Edge", RequiresRestore = true, ExecutionAttempted = true, ExecutionVerified = true },
                new PersistentSessionAction { ActionType = ActionTypeIds.ProcessSetState, ActionName = "Notatnik", RequiresRestore = true, ExecutionAttempted = true, ExecutionVerified = true },
                new PersistentSessionAction { ActionType = ActionTypeIds.ServiceSetState, ActionName = "Bufor wydruku", RequiresRestore = true, ExecutionAttempted = true, ExecutionVerified = true,
                    Parameters = new JsonObject { [ActionParameterNames.ServiceName] = "Spooler", [ActionParameterNames.ServiceDisplayName] = "Bufor wydruku" } },
                new PersistentSessionAction { ActionType = ActionTypeIds.ServiceSetState, ActionName = "Skipped service", RequiresRestore = false, ExecutionAttempted = true, ExecutionVerified = true }
            ]
        };

        Assert.Equal(3, session.PendingRestoreCount);
        Assert.Equal(new[] { "Microsoft Edge", "Notatnik", "Bufor wydruku" },
            session.GetPendingRestoreEntries().Select(item => item.ActionName));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task SessionMaintenance_RemovesOnlyOldFullyRestoredSessions()
    {
        using var context = new RuntimeTestContext();
        var session = new PersistentExecutionSession
        {
            ProfileId = Guid.NewGuid(), ProfileName = "Old restored", Status = PersistentSessionStatus.Restored
        };
        await context.SessionRepository.SaveAsync(session);
        var path = Path.Combine(context.AppDataRoot, "sessions", $"{session.SessionId:N}.json");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        json["updatedAt"] = DateTimeOffset.UtcNow.AddDays(-40).ToString("O");
        await File.WriteAllTextAsync(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        await context.SessionRepository.MaintainAsync(TimeSpan.FromDays(30));

        Assert.False(File.Exists(path));
    }

    private static ProfileDefinition CreateReversibleProfile(string name, params string[] keys)
    {
        var profile = new ProfileDefinition
        {
            CategoryId = Guid.NewGuid(), Name = name,
            Actions = keys.Select(key => Action(TestReversibleHandler.TypeId, new JsonObject { ["key"] = key })).ToList()
        };
        for (var index = 0; index < profile.Actions.Count; index++)
        {
            profile.Actions[index].SortOrder = index;
            profile.Actions[index].RestoreBehavior = ActionRestoreBehavior.RestorePreviousState;
        }
        return profile;
    }

    private static ActionDefinition CreateCombinedServiceAction()
    {
        var action = Action(ActionTypeIds.ServiceSetState, new JsonObject
        {
            [ActionParameterNames.ServiceName] = "Spooler",
            [ActionParameterNames.ServiceDisplayName] = "Print Spooler",
            [ActionParameterNames.DesiredState] = ServiceDesiredStateIds.Stopped,
            [ActionParameterNames.ServiceStartupType] = ServiceStartupTypeIds.Disabled
        });
        action.Name = "Print Spooler";
        action.RestoreBehavior = ActionRestoreBehavior.RestorePreviousState;
        return action;
    }
}
