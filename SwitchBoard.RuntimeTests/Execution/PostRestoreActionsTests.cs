using System.Text.Json;
using System.Text.Json.Nodes;
using SwitchBoard.Data;
using SwitchBoard.Models.Actions;
using SwitchBoard.Models.Categories;
using SwitchBoard.Models.Execution;
using SwitchBoard.Models.Profiles;
using SwitchBoard.RuntimeTests.TestInfrastructure;
using SwitchBoard.Services.Activity;
using SwitchBoard.Services.Execution;
using SwitchBoard.Services.Persistence;
using SwitchBoard.Services.Profiles;
using SwitchBoard.Services.Windows;
using SwitchBoard.Themes;
using SwitchBoard.ViewModels;

namespace SwitchBoard.RuntimeTests.Execution;

public sealed class PostRestoreActionsTests
{
    [Fact]
    [Trait("Category", "Regression")]
    public void OlderProfileWithoutPostRestoreActions_UsesAnEmptyCollection()
    {
        var id = Guid.NewGuid();
        var profile = JsonSerializer.Deserialize<ProfileDefinition>(
            $$"""{"id":"{{id}}","name":"Legacy","actions":[]}""",
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(profile);
        Assert.Empty(profile!.PostRestoreActions);
        var viewModel = new ProfileItemViewModel(profile, new TestLocalizationService());
        Assert.Empty(viewModel.PostRestoreActions);
        Assert.Empty(viewModel.EditorActions);
    }

    [Fact]
    [Trait("Category", "Regression")]
    public async Task NormalProfileRun_ExecutesOnlyRegularActions()
    {
        using var context = new RuntimeTestContext();
        var calls = new List<string>();
        var handler = new PostActionHandler(calls);
        var runner = new ProfileRunner(new ActionRegistry([handler]), context.SessionRepository);
        var profile = new ProfileDefinition
        {
            Name = "Regular only",
            Actions = [PostAction("regular")],
            PostRestoreActions = [PostAction("post")]
        };

        var result = await runner.RunAsync(profile);

        Assert.Equal(ExecutionSessionStatus.Completed, result.Status);
        Assert.Equal(["post:regular"], calls);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Restore_RunsPostActionsOnlyAfterBaseRestore()
    {
        using var scenario = new RestoreScenario(["first"]);
        await scenario.RestoreAsync();

        Assert.Equal(["restore:base", "post:first"], scenario.Calls);
        Assert.Equal(ExecutionSessionStatus.Completed, scenario.Main.LastExecutionSession?.Status);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Restore_PostActionsFollowTheirOwnSortOrder()
    {
        using var scenario = new RestoreScenario(["third", "first", "second"]);
        scenario.Main.SelectedProfile!.PostRestoreActions[0].SortOrder = 2;
        scenario.Main.SelectedProfile.PostRestoreActions[1].SortOrder = 0;
        scenario.Main.SelectedProfile.PostRestoreActions[2].SortOrder = 1;

        await scenario.RestoreAsync();

        Assert.Equal(["restore:base", "post:first", "post:second", "post:third"], scenario.Calls);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Restore_BaseFailure_SkipsPostRestoreActions()
    {
        using var scenario = new RestoreScenario(["post"], failBaseRestore: true);
        await scenario.RestoreAsync();

        Assert.Equal(["restore:base"], scenario.Calls);
        Assert.Null(scenario.Main.LastExecutionSession);
        Assert.Equal(0, scenario.Completion.RestoreCount);
        Assert.Equal("Execution.Status.CompletedWithErrors", scenario.Main.ExecutionStatusText);
    }

    [Fact]
    [Trait("Category", "Regression")]
    public async Task CatalogPersistence_RoundTripsPostRestoreActions()
    {
        using var context = new RuntimeTestContext();
        var paths = new AppDataPaths(Path.Combine(context.Root, "post-restore-catalog"));
        using var repository = new JsonCatalogRepository(paths);
        var profile = new ProfileDefinition
        {
            Name = "Persisted",
            PostRestoreActions = [PostAction("after")]
        };
        var catalog = new SwitchBoardCatalog { Profiles = [profile] };

        await repository.SaveAsync(catalog);
        var loaded = await repository.LoadAsync();

        var action = Assert.Single(Assert.Single(loaded.Profiles).PostRestoreActions);
        Assert.Equal("after", action.Parameters?["key"]?.GetValue<string>());
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Restore_PostActionFailure_IsShownInHistoryAndPreventsClose()
    {
        using var scenario = new RestoreScenario(["broken"], failPostAction: "broken", closeAfterRestore: true);
        await scenario.RestoreAsync();

        Assert.Equal(["restore:base", "post:broken"], scenario.Calls);
        Assert.Equal(0, scenario.Completion.RestoreCount);
        Assert.Equal("Execution.Status.Failed", scenario.Main.ExecutionStatusText);
        Assert.Contains(ProfileExecutionHistoryBuilder.Build(scenario.Activity.Records), summary =>
            summary.IsPostRestore && summary.Result == ProfileExecutionResult.Error);
        Assert.Contains(scenario.Activity.Entries, entry => entry.Level == ActivityLevel.Error &&
            entry.Message.StartsWith("Activity.PostRestoreFailed:", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Restore_AppliesCloseSettingOnlyAfterSuccessfulPostActions()
    {
        using var scenario = new RestoreScenario(["after"], closeAfterRestore: true);
        await scenario.RestoreAsync();

        Assert.Equal(["restore:base", "post:after", "close"], scenario.Calls);
        Assert.Equal(1, scenario.Completion.RestoreCount);
    }

    [Fact]
    [Trait("Category", "Regression")]
    public async Task Restore_OfPostRestoreAction_DoesNotRunThePostRestoreListAgain()
    {
        using var scenario = new RestoreScenario(["temporary"], postActionIsReversible: true);
        await scenario.RestoreAsync();
        Assert.True(scenario.Main.HasPendingRestore);

        await scenario.Main.RestoreProfileFromTrayAsync();
        await TestHelpers.WaitUntilAsync(() => !scenario.Main.IsRestoreRunning, TimeSpan.FromSeconds(5));

        Assert.Equal(["restore:base", "post:temporary", "restore-post:temporary"], scenario.Calls);
    }

    [Fact]
    [Trait("Category", "Regression")]
    public async Task Restore_ExecutesShortcutProgramRunPostActionThroughWindowsShell()
    {
        ProcessStartInfo? started = null;
        var shortcutHandler = new ProgramRunActionHandler(processStarter: startInfo =>
        {
            started = startInfo;
            return Process.GetCurrentProcess();
        });
        var shortcutAction = new ActionDefinition
        {
            Id = Guid.NewGuid(),
            Type = ActionTypeIds.ProgramRun,
            Name = "Shortcut after restore",
            Parameters = new JsonObject
            {
                [ActionParameterNames.Target] = Path.Combine(Path.GetTempPath(), "post-restore-shortcut.lnk"),
                [ActionParameterNames.InstanceBehavior] = InstanceBehaviorIds.StartAnother
            }
        };
        using var scenario = new RestoreScenario([], additionalPostAction: shortcutAction,
            additionalHandler: shortcutHandler);

        await scenario.RestoreAsync();

        Assert.NotNull(started);
        Assert.True(started!.UseShellExecute);
        Assert.EndsWith(".lnk", started.FileName, StringComparison.OrdinalIgnoreCase);
    }

    private static ActionDefinition PostAction(string key) => new()
    {
        Id = Guid.NewGuid(),
        Type = PostActionHandler.TypeId,
        Name = key,
        Parameters = new JsonObject { ["key"] = key }
    };

    private sealed class RestoreScenario : IDisposable
    {
        private readonly RuntimeTestContext _context = new();
        private readonly BaseRestoreHandler _baseHandler;
        private readonly PostActionHandler _postHandler;

        public RestoreScenario(IEnumerable<string> postKeys, bool failBaseRestore = false,
            string? failPostAction = null, bool closeAfterRestore = false, bool postActionIsReversible = false,
            ActionDefinition? additionalPostAction = null, IActionHandler? additionalHandler = null)
        {
            _baseHandler = new BaseRestoreHandler(Calls, failBaseRestore);
            _postHandler = new PostActionHandler(Calls, failPostAction);
            var handlers = new List<IActionHandler> { _baseHandler, _postHandler };
            if (additionalHandler is not null) handlers.Add(additionalHandler);
            var registry = new ActionRegistry(handlers);
            var runner = new ProfileRunner(registry, _context.SessionRepository, activity: Activity,
                localization: new TestLocalizationService());
            Profile = new ProfileDefinition
            {
                Name = "Restore scenario",
                CloseSwitchBoardAfterSuccessfulRestore = closeAfterRestore,
                Actions =
                [
                    new ActionDefinition
                    {
                        Id = Guid.NewGuid(), Type = BaseRestoreHandler.TypeId, Name = "base",
                        RestoreBehavior = ActionRestoreBehavior.RestorePreviousState,
                        Parameters = new JsonObject { ["key"] = "base" }
                    }
                ],
                PostRestoreActions = postKeys.Select(PostAction).ToList()
            };
            if (additionalPostAction is not null) Profile.PostRestoreActions.Add(additionalPostAction);
            for (var index = 0; index < Profile.PostRestoreActions.Count; index++)
            {
                Profile.PostRestoreActions[index].SortOrder = index;
                Profile.PostRestoreActions[index].RestoreBehavior = postActionIsReversible
                    ? ActionRestoreBehavior.RestorePreviousState
                    : ActionRestoreBehavior.DoNotRestore;
            }

            InitialSession = runner.RunAsync(Profile).GetAwaiter().GetResult();
            Calls.Clear();
            Completion = new RecordingCompletionBehavior(Calls);
            var category = new CategoryDefinition { Name = "Tests" };
            Profile.CategoryId = category.Id;
            Main = new MainWindowViewModel(new TestCatalogService(), new TestDialogService(),
                new SwitchBoardCatalog { Categories = [category], Profiles = [Profile] }, new TestThemeManager(),
                new TestLocalizationService(), new TestSettingsRepository(), new UserSettings
                {
                    ThemeId = ThemeIds.Graphite,
                    LanguageId = "en",
                    LastSelectedProfileId = Profile.Id
                }, runner, new ProfileRestoreRunner(registry, _context.SessionRepository, activity: Activity,
                    localization: new TestLocalizationService()), _context.SessionRepository, Completion,
                new TestDisplayManager(new("", "", "", 1, 1, 1, 32, 0, 0, 0, 0)),
                new TestCustomThemeEditorService(), activityService: Activity,
                appDataPaths: new AppDataPaths(_context.AppDataRoot));
        }

        public List<string> Calls { get; } = [];
        public ActivityService Activity { get; } = new();
        public ProfileDefinition Profile { get; }
        public ExecutionSession InitialSession { get; }
        public RecordingCompletionBehavior Completion { get; }
        public MainWindowViewModel Main { get; }

        public async Task RestoreAsync()
        {
            await TestHelpers.WaitUntilAsync(() => Main.HasPendingRestore, TimeSpan.FromSeconds(5));
            await Main.RestoreProfileFromTrayAsync();
            await TestHelpers.WaitUntilAsync(() => !Main.IsRestoreRunning, TimeSpan.FromSeconds(5));
        }

        public void Dispose()
        {
            Main.Dispose();
            _context.Dispose();
        }
    }

    private sealed class BaseRestoreHandler(List<string> calls, bool failRestore) : IReversibleActionHandler
    {
        public const string TypeId = "test.post-restore-base";
        public string ActionType => TypeId;

        public Task<JsonObject?> CaptureStateAsync(ActionDefinition action, ActionExecutionContext context,
            CancellationToken cancellationToken) => Task.FromResult<JsonObject?>(new JsonObject
            {
                ["key"] = action.Parameters["key"]?.GetValue<string>()
            });

        public Task<ActionExecutionResult> ExecuteAsync(ActionDefinition action, ActionExecutionContext context,
            CancellationToken cancellationToken) => Task.FromResult(ActionExecutionResult.Success());

        public Task<ActionExecutionResult> RestoreAsync(ActionDefinition action, JsonObject restoreState,
            ActionExecutionContext context, CancellationToken cancellationToken)
        {
            calls.Add($"restore:{restoreState["key"]?.GetValue<string>()}");
            return Task.FromResult(failRestore
                ? ActionExecutionResult.Failure("Base restore failed.")
                : ActionExecutionResult.Success());
        }
    }

    private sealed class PostActionHandler(List<string> calls, string? failKey = null) : IReversibleActionHandler
    {
        public const string TypeId = "test.post-restore";
        public string ActionType => TypeId;

        public Task<ActionExecutionResult> ExecuteAsync(ActionDefinition action, ActionExecutionContext context,
            CancellationToken cancellationToken)
        {
            var key = action.Parameters["key"]?.GetValue<string>() ?? string.Empty;
            calls.Add($"post:{key}");
            return Task.FromResult(string.Equals(key, failKey, StringComparison.Ordinal)
                ? ActionExecutionResult.Failure("Post action failed.")
                : ActionExecutionResult.Success());
        }

        public Task<JsonObject?> CaptureStateAsync(ActionDefinition action, ActionExecutionContext context,
            CancellationToken cancellationToken) => Task.FromResult<JsonObject?>(new JsonObject
            {
                ["key"] = action.Parameters["key"]?.GetValue<string>()
            });

        public Task<ActionExecutionResult> RestoreAsync(ActionDefinition action, JsonObject restoreState,
            ActionExecutionContext context, CancellationToken cancellationToken)
        {
            calls.Add($"restore-post:{restoreState["key"]?.GetValue<string>()}");
            return Task.FromResult(ActionExecutionResult.Success());
        }
    }

    private sealed class RecordingCompletionBehavior(List<string> calls) : IProfileCompletionBehavior
    {
        public int RestoreCount { get; private set; }
        public void HandleSuccessfulCompletion(ProfileDefinition profile) { }
        public void HandleSuccessfulRestore(ProfileDefinition profile)
        {
            RestoreCount++;
            calls.Add("close");
        }
    }
}
