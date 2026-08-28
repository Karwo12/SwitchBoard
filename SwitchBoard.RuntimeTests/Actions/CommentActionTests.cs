using SwitchBoard.RuntimeTests.TestInfrastructure;
using SwitchBoard.Models;
using SwitchBoard.Services.Actions;

namespace SwitchBoard.RuntimeTests.Actions;

public sealed class CommentActionTests : RuntimeTestBase
{
    [Fact]
    [Trait("Category", "Unit")]
    public void Comment_IsPickerAction_AndRoundTripsOnlyItsText()
    {
        var localization = new TestLocalizationService();
        var descriptor = ActionDescriptorRegistry.PickerDescriptors.Single(item => item.TypeId == ActionTypeIds.Comment);
        var editor = new ActionItemViewModel(new ActionDefinition
        {
            Type = ActionTypeIds.Comment,
            Name = "Ignored action name",
            FailurePolicy = ActionFailurePolicy.Stop,
            RestoreBehavior = ActionRestoreBehavior.RestorePreviousState,
            Timeout = TimeSpan.FromSeconds(10),
            RetryOnFailure = true,
            Parameters = descriptor.CreateDefaultParameters(nested: false)
        }, localization)
        {
            CommentText = "  Before launching the game  "
        };

        Assert.True(editor.IsComment);
        Assert.Equal(ActionTypeIds.Comment, descriptor.TypeId);
        Assert.Equal("Before launching the game", editor.DisplayName);
        Assert.Equal(string.Empty, editor.Summary);
        Assert.Equal(ValidationSeverity.Valid, editor.ValidationLevel);
        Assert.True(editor.IsValid);
        Assert.False(editor.SupportsRestore);
        Assert.False(editor.ShouldMonitorCurrentStatus);
        Assert.IsType<List<LocalizedValueOptionViewModel>>(editor.AvailableRestoreBehaviors);

        var model = editor.ToModel();
        Assert.Null(model.Name);
        Assert.Equal(ActionFailurePolicy.Continue, model.FailurePolicy);
        Assert.Equal(ActionRestoreBehavior.DoNotRestore, model.RestoreBehavior);
        Assert.Null(model.Timeout);
        Assert.False(model.RetryOnFailure);
        Assert.Equal(1, model.MaximumAttempts);
        Assert.Equal(TimeSpan.Zero, model.RetryDelay);
        Assert.Equal("  Before launching the game  ", model.Parameters[ActionParameterNames.CommentText]?.GetValue<string>());

        var reopened = new ActionItemViewModel(model, localization);
        Assert.Equal(editor.DisplayName, reopened.DisplayName);
        Assert.Equal(editor.CommentText, reopened.CommentText);

        var profile = new ProfileDefinition
        {
            Name = "Comment profile",
            Actions = [model]
        };
        var jsonRoundTrip = JsonSerializer.Deserialize<ProfileDefinition>(JsonSerializer.Serialize(profile));
        Assert.NotNull(jsonRoundTrip);
        Assert.Equal("  Before launching the game  ",
            jsonRoundTrip!.Actions.Single().Parameters[ActionParameterNames.CommentText]?.GetValue<string>());
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Comment_IsValidAndDoesNotCountInPreflight()
    {
        var profile = new ProfileItemViewModel(new ProfileDefinition
        {
            Name = "Comments only",
            Actions =
            [
                new ActionDefinition
                {
                    Type = ActionTypeIds.Comment,
                    Parameters = new JsonObject { [ActionParameterNames.CommentText] = string.Empty }
                }
            ]
        }, new TestLocalizationService());

        var result = new ProfilePreflightService().Analyze(profile, profileReferencesAreValid: true);

        Assert.Equal(0, result.ReadyActionCount);
        Assert.Empty(result.Issues);
        Assert.Empty(result.AdministratorActions);
        Assert.True(profile.Actions.Single().IsValid);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Comment_IsSkippedBetweenExecutableActions_AndNeverEntersRuntimeStateOrHistory()
    {
        using var context = new RuntimeTestContext();
        var activity = new ActivityService();
        var runner = new ProfileRunner(context.Registry, context.SessionRepository, activity: activity);
        var first = RuntimeTestContext.Action(ActionTypeIds.Delay,
            new JsonObject { [ActionParameterNames.DelaySeconds] = 0 });
        first.SortOrder = 0;
        var comment = RuntimeTestContext.Action(ActionTypeIds.Comment,
            new JsonObject { [ActionParameterNames.CommentText] = "Middle marker" });
        comment.SortOrder = 1;
        var last = RuntimeTestContext.Action(ActionTypeIds.Delay,
            new JsonObject { [ActionParameterNames.DelaySeconds] = 0 });
        last.SortOrder = 2;
        var profile = new ProfileDefinition { Name = "Runtime comment", Actions = [first, comment, last] };

        var session = await runner.RunAsync(profile);
        var persistent = await context.SessionRepository.LoadAsync(session.Id);
        var history = ProfileExecutionHistoryBuilder.Build(activity.Records).Single();

        Assert.Equal(ExecutionSessionStatus.Completed, session.Status);
        Assert.Equal(2, session.Journal.Count);
        Assert.DoesNotContain(session.Journal, item => item.ActionId == comment.Id || item.ActionType == ActionTypeIds.Comment);
        Assert.Equal(2, persistent!.Actions.Count);
        Assert.DoesNotContain(persistent.Actions, item => item.ActionId == comment.Id);
        Assert.Equal(2, history.Actions.Count);
        Assert.DoesNotContain(history.Actions, item => item.ActionId == comment.Id);
        Assert.DoesNotContain(context.Registry.RegisteredActionTypes, item => item == ActionTypeIds.Comment);
        Assert.False(context.Registry.TryGetHandler(ActionTypeIds.Comment, out _));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CommentOnlyProfile_CompletesWithoutAHandler()
    {
        using var context = new RuntimeTestContext();
        var comment = RuntimeTestContext.Action(ActionTypeIds.Comment,
            new JsonObject { [ActionParameterNames.CommentText] = "No operation" });
        var session = await context.Runner.RunAsync(new ProfileDefinition
        {
            Name = "Comment only",
            Actions = [comment]
        });

        Assert.Equal(ExecutionSessionStatus.Completed, session.Status);
        Assert.Empty(session.Journal);
        var persistent = await context.SessionRepository.LoadAsync(session.Id);
        Assert.NotNull(persistent);
        Assert.Empty(persistent!.Actions);
        Assert.Empty(persistent.GetPendingRestoreEntries());
    }
}
