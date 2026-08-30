using SwitchBoard.Services.Activity;
using SwitchBoard.RuntimeTests.TestInfrastructure;
using SwitchBoard.ViewModels;

namespace SwitchBoard.RuntimeTests.Activity;

public sealed class ActivityHistoryTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void ProfileExecutionHistory_GroupsOneSessionAndKeepsRestoreWithIt()
    {
        var sessionId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var actionOne = Guid.NewGuid();
        var actionTwo = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow.AddSeconds(-12);
        var service = new ActivityService();

        service.Record(new PersistentActivityRecord
        {
            Timestamp = start,
            SessionId = sessionId,
            ProfileId = profileId,
            ProfileName = "CS2",
            EventType = ActivityEventTypes.ProfileStarted,
            Level = ActivityLevel.Info,
            Message = "Profile started: CS2"
        });
        service.Record(ActionRecord(sessionId, profileId, actionOne, "Display", "success", start.AddSeconds(3)));
        service.Record(ActionRecord(sessionId, profileId, actionTwo, "Process", "failed", start.AddSeconds(7), ActivityLevel.Error,
            ActivityEventTypes.Failed));
        service.Record(new PersistentActivityRecord
        {
            Timestamp = start.AddSeconds(8),
            SessionId = sessionId,
            ProfileId = profileId,
            ProfileName = "CS2",
            EventType = ActivityEventTypes.ProfileCompleted,
            Result = "warning",
            Level = ActivityLevel.Warning,
            Message = "Profile completed with errors: CS2"
        });
        service.Record(new PersistentActivityRecord
        {
            Timestamp = start.AddSeconds(10),
            SessionId = sessionId,
            ProfileId = profileId,
            ProfileName = "CS2",
            ActionId = actionOne,
            ActionType = "display.configure",
            FriendlyName = "Display",
            EventType = ActivityEventTypes.Restore,
            RestoreStatus = SystemChangeStatuses.Restored,
            Level = ActivityLevel.Success,
            Message = "Action restored: Display"
        });

        var history = ProfileExecutionHistoryBuilder.Build(service.Records);

        var entry = Assert.Single(history);
        Assert.Equal("CS2", entry.ProfileName);
        Assert.Equal(ProfileExecutionResult.Error, entry.Result);
        Assert.Equal(2, entry.Actions.Count);
        Assert.Equal(1, entry.SuccessfulActionCount);
        Assert.True(entry.IsRestored);
        Assert.False(entry.HasRestoreFailure);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ProfileExecutionHistory_UsesOptionalProfileAndActionTiming()
    {
        var sessionId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var actionId = Guid.NewGuid();
        var start = new DateTimeOffset(2026, 1, 2, 12, 0, 0, TimeSpan.Zero);
        var actionStart = start.AddSeconds(1.8);
        var actionEnd = start.AddSeconds(4.8);
        var service = new ActivityService();

        service.Record(new PersistentActivityRecord
        {
            Timestamp = start,
            SessionId = sessionId,
            ProfileId = profileId,
            ProfileName = "Timed",
            EventType = ActivityEventTypes.ProfileStarted,
            Level = ActivityLevel.Info,
            Message = "Profile started: Timed"
        });
        service.Record(new PersistentActivityRecord
        {
            Timestamp = actionEnd,
            StartedAt = actionStart,
            CompletedAt = actionEnd,
            SessionId = sessionId,
            ProfileId = profileId,
            ProfileName = "Timed",
            ActionId = actionId,
            FriendlyName = "Delay",
            EventType = ActivityEventTypes.Verify,
            Level = ActivityLevel.Success,
            Result = "success",
            Message = "Action completed: Delay"
        });
        service.Record(new PersistentActivityRecord
        {
            Timestamp = start.AddSeconds(5.2),
            SessionId = sessionId,
            ProfileId = profileId,
            ProfileName = "Timed",
            EventType = ActivityEventTypes.ProfileCompleted,
            Level = ActivityLevel.Success,
            Result = "success",
            Message = "Profile completed: Timed"
        });

        var entry = Assert.Single(ProfileExecutionHistoryBuilder.Build(service.Records));
        var action = Assert.Single(entry.Actions);

        Assert.Equal(actionStart, action.StartedAt);
        Assert.Equal(actionEnd, action.CompletedAt);
        Assert.Equal(start.AddSeconds(5.2), entry.CompletedAt);
        var actionDurationText = new ProfileExecutionActionViewModel(action,
            new TestLocalizationService()).DurationText;
        Assert.StartsWith("Activity.DurationSeconds:", actionDurationText, StringComparison.Ordinal);
        Assert.InRange((action.CompletedAt!.Value - action.StartedAt!.Value).TotalSeconds, 2.99, 3.01);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ProfileExecutionHistory_OldRecordsKeepUnknownDurations()
    {
        var action = new ProfileExecutionActionSummary(Guid.NewGuid(), Guid.NewGuid(), "Delay",
            "Action completed: Delay", ActivityLevel.Success, "success", DateTimeOffset.UtcNow);
        var summary = new ProfileExecutionSummary(Guid.NewGuid(), action.ProfileId, "Legacy",
            DateTimeOffset.UtcNow, null, ProfileExecutionResult.Success, [action], false, false);

        var viewModel = new ProfileExecutionViewModel(summary, new TestLocalizationService());

        Assert.Equal("Activity.DurationUnknown", viewModel.DurationText);
        Assert.Equal("Activity.DurationUnknown", Assert.Single(viewModel.Actions).DurationText);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ProfileExecutionView_ToggleExpandedCommandChangesExpansionState()
    {
        var summary = new ProfileExecutionSummary(Guid.NewGuid(), null, "Profile",
            DateTimeOffset.UtcNow, null, ProfileExecutionResult.Success, [], false, false);
        var viewModel = new ProfileExecutionViewModel(summary, new TestLocalizationService());

        Assert.False(viewModel.IsExpanded);
        viewModel.ToggleExpandedCommand.Execute(null);
        Assert.True(viewModel.IsExpanded);
        viewModel.ToggleExpandedCommand.Execute(null);
        Assert.False(viewModel.IsExpanded);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ActivityService_SystemChangesExcludeOrdinaryProfileEvents()
    {
        var service = new ActivityService();
        var sessionId = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        service.Record(new PersistentActivityRecord
        {
            SessionId = sessionId,
            ProfileId = profileId,
            ProfileName = "Profile",
            EventType = ActivityEventTypes.ProfileStarted,
            Level = ActivityLevel.Info,
            Message = "Profile started: Profile"
        });
        service.Record(ActionRecord(sessionId, profileId, Guid.NewGuid(), "Delay", "success", DateTimeOffset.UtcNow));

        Assert.Empty(service.SystemChanges);
        Assert.Contains(service.Entries, entry => entry.Message.Contains("Profile started", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ActivityView_VirtualizesLongListsAndReservesScrollbarViewport()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Views", "MainWindow.xaml"));
        var xaml = File.ReadAllText(path);
        var activityPath = Path.Combine(Path.GetDirectoryName(path)!, "Panels", "ActivityPanel.xaml");
        var activityXaml = File.ReadAllText(activityPath);

        Assert.Contains("<panels:ActivityPanel", xaml, StringComparison.Ordinal);
        Assert.Equal(2, activityXaml.Split("HorizontalScrollBarVisibility=\"Disabled\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, activityXaml.Split("CanContentScroll=\"True\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, activityXaml.Split("VirtualizationMode=\"Recycling\"", StringSplitOptions.None).Length - 1);
        Assert.Equal(2, activityXaml.Split("ScrollUnit=\"Pixel\"", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("ActivityDisplayEntries", activityXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("ActivityTab0ButtonStyle", activityXaml, StringComparison.Ordinal);
        Assert.Contains("{DynamicResource ActivityTab1ButtonStyle}", activityXaml, StringComparison.Ordinal);
        Assert.Contains("{DynamicResource ActivityTab2ButtonStyle}", activityXaml, StringComparison.Ordinal);
        Assert.Contains("{DynamicResource ActivityRowSurfaceStyle}", activityXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("CommandParameter=\"0\"", activityXaml, StringComparison.Ordinal);
        Assert.Contains("CommandParameter=\"1\"", activityXaml, StringComparison.Ordinal);
        Assert.Contains("CommandParameter=\"2\"", activityXaml, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Regression")]
    public void Retention_RewritesMixedOldHistoryAndKeepsOnlyPendingSystemChange()
    {
        using var context = new RuntimeTestContext();
        var paths = new AppDataPaths(Path.Combine(context.Root, "retention-mixed"));
        var activity = new ActivityService(paths, retentionDays: HistoryRetentionOptions.Unlimited);
        var old = DateTimeOffset.UtcNow.AddDays(-45);
        activity.Record(new PersistentActivityRecord
        {
            Timestamp = old,
            EventType = ActivityEventTypes.Activity, Level = ActivityLevel.Info, Message = "Old ordinary activity"
        });
        activity.Record(new PersistentActivityRecord
        {
            Timestamp = old,
            SessionId = Guid.NewGuid(),
            ActionId = Guid.NewGuid(),
            ActionType = "test", FriendlyName = "Pending change",
            EventType = ActivityEventTypes.Verify, Level = ActivityLevel.Warning,
            RestoreStatus = SystemChangeStatuses.Pending, Message = "Old pending system change"
        });
        activity.Record(new PersistentActivityRecord
        {
            Timestamp = old,
            EventType = ActivityEventTypes.Activity, Level = ActivityLevel.Info, Message = "Old resolved activity"
        });

        activity.SetRetentionDays(HistoryRetentionOptions.ThirtyDays);

        Assert.Single(activity.Records);
        Assert.Equal(SystemChangeStatuses.Pending, activity.Records[0].RestoreStatus);
        var reloaded = new ActivityService(paths, retentionDays: HistoryRetentionOptions.ThirtyDays);
        Assert.Single(reloaded.Records);
        Assert.Equal(SystemChangeStatuses.Pending, reloaded.Records[0].RestoreStatus);
    }

    [Fact]
    [Trait("Category", "Regression")]
    public void ClearPersistentHistory_RemovesResolvedEntriesButKeepsPendingSystemChanges()
    {
        using var context = new RuntimeTestContext();
        var paths = new AppDataPaths(Path.Combine(context.Root, "clear-history"));
        var activity = new ActivityService(paths);
        activity.Add(ActivityLevel.Success, "Completed profile");
        activity.Record(new PersistentActivityRecord
        {
            SessionId = Guid.NewGuid(), ActionId = Guid.NewGuid(), ActionType = "test",
            FriendlyName = "Pending change", EventType = ActivityEventTypes.Verify,
            Level = ActivityLevel.Warning, RestoreStatus = SystemChangeStatuses.Pending,
            Message = "Pending system change"
        });

        activity.ClearPersistentHistory();

        Assert.Single(activity.Records);
        Assert.Equal(SystemChangeStatuses.Pending, activity.Records[0].RestoreStatus);
        Assert.Single(new ActivityService(paths).Records);
    }

    private static PersistentActivityRecord ActionRecord(Guid sessionId, Guid profileId, Guid actionId,
        string name, string result, DateTimeOffset timestamp,
        ActivityLevel level = ActivityLevel.Success, string eventType = ActivityEventTypes.Verify) => new()
    {
        Timestamp = timestamp,
        SessionId = sessionId,
        ProfileId = profileId,
        ProfileName = "CS2",
        ActionId = actionId,
        ActionType = "test",
        FriendlyName = name,
        EventType = eventType,
        Result = result,
        Level = level,
        Message = $"{result}: {name}"
    };
}
