using SwitchBoard.RuntimeTests.TestInfrastructure;

namespace SwitchBoard.RuntimeTests.ViewModels;

public sealed class ActionExecutionStateTests : RuntimeTestBase
{
    [Fact]
    [Trait("Category", "Unit")]
    public void ExecutionProgress_SnapshotsStatusBeforeJournalEntryIsMutated()
    {
        var action = Action(ActionTypeIds.Delay, []);
        var journal = new ActionJournalEntry
        {
            ActionId = action.Id, ActionType = action.Type, Status = ActionJournalStatus.Running
        };
        var progress = new ProfileExecutionProgress(1, 1, action, journal);

        journal.Status = ActionJournalStatus.Success;

        Assert.Equal(ActionJournalStatus.Running, progress.Status);
        Assert.Equal(action.Id, progress.ActionId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ActionCards_UseStableIdsWhenDuplicateTypesTransition()
    {
        var localization = new TestLocalizationService();
        var models = Enumerable.Range(1, 3)
            .Select(index => Action(ActionTypeIds.Delay,
                new JsonObject { [ActionParameterNames.DelaySeconds] = 0 }))
            .ToArray();
        var cards = models.Select(model => new ActionItemViewModel(model, localization)).ToArray();
        var cardsById = cards.ToDictionary(card => card.Id);

        cardsById[models[0].Id].SetExecutionState(ActionExecutionState.Running);
        Assert.Equal(ActionExecutionState.Running, cards[0].ExecutionState);
        Assert.DoesNotContain(cards.Skip(1), card => card.IsExecutionRunning);

        cardsById[models[0].Id].SetExecutionState(ActionExecutionState.Completed);
        cardsById[models[1].Id].SetExecutionState(ActionExecutionState.Running);
        Assert.Equal(ActionExecutionState.Completed, cards[0].ExecutionState);
        Assert.Equal(ActionExecutionState.Running, cards[1].ExecutionState);
        Assert.Equal(ActionExecutionState.Pending, cards[2].ExecutionState);

        cardsById[models[1].Id].SetExecutionState(ActionExecutionState.Completed);
        cardsById[models[2].Id].SetExecutionState(ActionExecutionState.Running);
        Assert.Equal(ActionExecutionState.Running, cards[2].ExecutionState);
        Assert.DoesNotContain(cards.Take(2), card => card.IsExecutionRunning);

        cardsById[models[2].Id].SetExecutionState(ActionExecutionState.Completed);
        Assert.DoesNotContain(cards, card => card.IsExecutionRunning);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ActionCard_ErrorStateBelongsToTheFailedInstanceAndClearsRunningState()
    {
        var localization = new TestLocalizationService();
        var firstModel = Action(ActionTypeIds.Delay, []);
        var secondModel = Action(ActionTypeIds.Delay, []);
        var first = new ActionItemViewModel(firstModel, localization);
        var second = new ActionItemViewModel(secondModel, localization);

        first.SetExecutionState(ActionExecutionState.Running);
        first.SetExecutionState(ActionExecutionState.Error);

        Assert.True(first.HasExecutionError);
        Assert.False(first.IsExecutionRunning);
        Assert.False(second.HasExecutionError);
        Assert.False(second.IsExecutionRunning);

        first.ClearExecutionError();
        Assert.Equal(ActionExecutionState.Pending, first.ExecutionState);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task ProfileRunner_ReportsRunningAndCompletedForEachActionByStableId()
    {
        using var context = new RuntimeTestContext();
        var profile = new ProfileDefinition
        {
            Name = "Execution progress",
            Actions = Enumerable.Range(1, 3).Select(index =>
            {
                var action = Action(TestReversibleHandler.TypeId,
                    new JsonObject { ["key"] = $"action-{index}" });
                action.SortOrder = index;
                return action;
            }).ToList()
        };
        var progress = new RecordingProgress<ProfileExecutionProgress>();

        var session = await context.Runner.RunAsync(profile, progress);

        Assert.Equal(ExecutionSessionStatus.Completed, session.Status);
        Assert.Equal(profile.Actions.Select(action => action.Id),
            progress.Items.Where(item => item.Status == ActionJournalStatus.Running)
                .Select(item => item.ActionId));
        Assert.Equal(profile.Actions.Select(action => action.Id),
            progress.Items.Where(item => item.Status == ActionJournalStatus.Success)
                .Select(item => item.ActionId));
        Assert.NotEqual(ActionJournalStatus.Running, progress.Items.Last().Status);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task ProfileRestore_ReportsTheActuallyRestoredActionInReverseRuntimeOrder()
    {
        using var context = new RuntimeTestContext();
        var profile = new ProfileDefinition
        {
            Name = "Restore progress",
            Actions = Enumerable.Range(1, 3).Select(index =>
            {
                var action = Action(TestReversibleHandler.TypeId,
                    new JsonObject { ["key"] = $"restore-{index}" });
                action.SortOrder = index;
                action.RestoreBehavior = ActionRestoreBehavior.RestorePreviousState;
                return action;
            }).ToList()
        };
        await context.Runner.RunAsync(profile);
        var pending = await context.SessionRepository.GetLatestPendingAsync(profile.Id);
        Assert.NotNull(pending);
        var progress = new RecordingProgress<ProfileRestoreProgress>();

        var restored = await new ProfileRestoreRunner(context.Registry, context.SessionRepository)
            .RunAsync(pending!, progress);

        Assert.Equal(PersistentSessionStatus.Restored, restored.Status);
        Assert.Equal(profile.Actions.AsEnumerable().Reverse().Select(action => action.Id),
            progress.Items.Where(item => item.Status == PersistentActionRestoreStatus.Restoring)
                .Select(item => item.Action.ActionId));
        Assert.Equal(profile.Actions.AsEnumerable().Reverse().Select(action => action.Id),
            progress.Items.Where(item => item.Status == PersistentActionRestoreStatus.Restored)
                .Select(item => item.Action.ActionId));
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task ProfileRunner_ReportsErrorForTheFailedActionAndNoLongerReportsItAsRunning()
    {
        using var context = new RuntimeTestContext();
        var flaky = new TestFlakyHandler();
        var registry = new ActionRegistry([flaky]);
        var runner = new ProfileRunner(registry, context.SessionRepository);
        var action = Action(TestFlakyHandler.TypeId, new JsonObject { ["failAlways"] = true },
            ActionFailurePolicy.Stop);
        var profile = new ProfileDefinition { Name = "Execution error", Actions = [action] };
        var progress = new RecordingProgress<ProfileExecutionProgress>();

        var session = await runner.RunAsync(profile, progress);

        Assert.Equal(ExecutionSessionStatus.Failed, session.Status);
        Assert.Equal(action.Id, progress.Items.Single(item => item.Status == ActionJournalStatus.Running).ActionId);
        Assert.Equal(action.Id, progress.Items.Single(item => item.Status == ActionJournalStatus.Failed).ActionId);
        Assert.Equal(ActionJournalStatus.Failed, progress.Items.Last().Status);
    }

    private sealed class RecordingProgress<T> : IProgress<T>
    {
        public List<T> Items { get; } = [];
        public void Report(T value) => Items.Add(value);
    }
}
