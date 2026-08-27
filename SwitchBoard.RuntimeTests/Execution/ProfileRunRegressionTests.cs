using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SwitchBoard.Data;
using SwitchBoard.Models.Actions;
using SwitchBoard.Models.Categories;
using SwitchBoard.Models.Execution;
using SwitchBoard.Models.Profiles;
using SwitchBoard.Services.Execution;
using SwitchBoard.Services.Execution.Handlers;
using SwitchBoard.Services.Profiles;
using SwitchBoard.RuntimeTests.TestInfrastructure;
using SwitchBoard.ViewModels;

namespace SwitchBoard.RuntimeTests.Execution;

public sealed class ProfileRunRegressionTests
{
    [Theory]
    [InlineData("new", 1)]
    [InlineData("new", 2)]
    [InlineData("new", 4)]
    [InlineData("new", 8)]
    [InlineData("new", 16)]
    [InlineData("duplicate", 1)]
    [InlineData("duplicate", 2)]
    [InlineData("duplicate", 4)]
    [InlineData("duplicate", 8)]
    [InlineData("duplicate", 16)]
    [InlineData("import", 1)]
    [InlineData("import", 2)]
    [InlineData("import", 4)]
    [InlineData("import", 8)]
    [InlineData("import", 16)]
    [Trait("Category", "Integration")]
    public async Task NewDuplicateAndImportedProfiles_ReachExecutorAtEveryTestedSize(string sourceKind, int count)
    {
        using var context = new RuntimeTestContext();
        var source = CreateProfile(count);
        var profile = await CreateVariantAsync(sourceKind, source, context.Root);
        var handler = new CountingHandler();
        var runner = new ProfileRunner(new ActionRegistry([handler]), context.SessionRepository);

        var session = await runner.RunAsync(profile);

        Assert.Equal(ExecutionSessionStatus.Completed, session.Status);
        Assert.Equal(count, handler.ExecutionCount);
        Assert.Equal(count, session.Journal.Count);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task RunCommand_EmitsEveryStageAndStartsFirstOfSixteenActions()
    {
        using var context = new RuntimeTestContext();
        var logger = new RecordingLogger();
        var handler = new CountingHandler();
        var registry = new ActionRegistry([handler]);
        var runner = new ProfileRunner(registry, context.SessionRepository, logger);
        using var viewModel = CreateMainViewModel([CreateProfile(16)], runner, registry, context, logger);

        Assert.True(viewModel.RunProfileCommand.CanExecute(null));
        viewModel.TraceRunClicked();
        viewModel.RunProfileCommand.Execute(null);
        await TestHelpers.WaitUntilAsync(() => viewModel.LastExecutionSession is not null && !viewModel.IsProfileRunning,
            TimeSpan.FromSeconds(10));

        Assert.Equal(16, handler.ExecutionCount);
        Assert.Equal(ExecutionSessionStatus.Completed, viewModel.LastExecutionSession!.Status);
        AssertStages(logger, "RUN_CLICKED", "COMMAND_ENTER", "PREFLIGHT_START", "PREFLIGHT_RESULT",
            "EXECUTOR_START", "ACTION_1_START");
        Assert.Equal(16, logger.Messages.Count(message => message.Contains("ACTION_DATA Index=", StringComparison.Ordinal)));
        Assert.Contains(logger.Messages, message => message.Contains("RuntimeModel=SwitchBoard.Models.Actions.ActionDefinition",
            StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DuplicateProfileCommand_CreatesIndependentRunnableProfile()
    {
        using var context = new RuntimeTestContext();
        var handler = new CountingHandler();
        var registry = new ActionRegistry([handler]);
        var source = CreateProfile(6);
        using var viewModel = CreateMainViewModel([source],
            new ProfileRunner(registry, context.SessionRepository), registry, context);
        var sourceViewModel = viewModel.SelectedProfile!;

        viewModel.DuplicateProfileCommand.Execute(sourceViewModel);
        var duplicate = viewModel.SelectedProfile!;

        Assert.NotEqual(sourceViewModel.Id, duplicate.Id);
        Assert.Equal(sourceViewModel.Actions.Count, duplicate.Actions.Count);
        Assert.Empty(sourceViewModel.Actions.Select(action => action.Id)
            .Intersect(duplicate.Actions.Select(action => action.Id)));
        Assert.All(sourceViewModel.Actions.Zip(duplicate.Actions), pair =>
            Assert.NotSame(pair.First.Parameters, pair.Second.Parameters));

        duplicate.Actions[0].Name = "Changed only in duplicate";
        Assert.NotEqual(sourceViewModel.Actions[0].Name, duplicate.Actions[0].Name);
        await viewModel.RunProfileFromTrayAsync(duplicate.Id);

        Assert.Equal(ExecutionSessionStatus.Completed, viewModel.LastExecutionSession!.Status);
        Assert.Equal(6, handler.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ProblemProfileShape_WithOneEnabledAndManyDisabledActions_ReachesExecutor()
    {
        using var context = new RuntimeTestContext();
        var handler = new CountingHandler(ActionTypeIds.ProgramRun);
        var registry = new ActionRegistry([handler]);
        var profile = CreateProblemProfileShape();
        using var viewModel = CreateMainViewModel([profile],
            new ProfileRunner(registry, context.SessionRepository), registry, context);

        Assert.True(viewModel.RunProfileCommand.CanExecute(null));
        await viewModel.RunProfileFromTrayAsync(profile.Id);

        Assert.Equal(1, handler.ExecutionCount);
        Assert.Equal(ExecutionSessionStatus.Completed, viewModel.LastExecutionSession!.Status);
        Assert.Equal(9, viewModel.LastExecutionSession.Journal.Count);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DuplicateAndImport_CreateIndependentDeepCopiesWithFreshNestedIdentity()
    {
        var source = CreateProfileWithNestedAction();
        var sourceJson = JsonSerializer.Serialize(source);
        var exchange = new ProfileExchangeService();
        var duplicate = exchange.CloneForDuplicate(source);
        var directory = Path.Combine(Path.GetTempPath(), $"SwitchBoard-profile-exchange-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "profile.sbprofile");
            await exchange.ExportAsync(source, path);
            var imported = await exchange.ImportAsync(path);

            foreach (var copy in new[] { duplicate, imported })
            {
                Assert.NotEqual(source.Id, copy.Id);
                Assert.Equal(source.Actions.Select(action => action.Type), copy.Actions.Select(action => action.Type));
                Assert.Equal(source.Actions.Select(action => action.SortOrder), copy.Actions.Select(action => action.SortOrder));
                Assert.All(copy.Actions.Zip(source.Actions), pair =>
                {
                    Assert.NotEqual(pair.First.Id, pair.Second.Id);
                    Assert.NotSame(pair.First.Parameters, pair.Second.Parameters);
                });
                var sourceNested = Nested(source.Actions[1]);
                var copyNested = Nested(copy.Actions[1]);
                Assert.NotEqual(sourceNested.Id, copyNested.Id);
                Assert.Equal(sourceNested.Type, copyNested.Type);
                Assert.NotSame(sourceNested.Parameters, copyNested.Parameters);
            }

            duplicate.Actions[0].Parameters["value"] = "changed";
            var duplicateNested = Nested(duplicate.Actions[1]);
            duplicateNested.Parameters["value"] = "nested-changed";
            duplicate.Actions[1].Parameters[ActionParameterNames.ThenActions] =
                new JsonArray(JsonSerializer.SerializeToNode(duplicateNested));

            Assert.Equal(sourceJson, JsonSerializer.Serialize(source));
            Assert.NotEqual(JsonSerializer.Serialize(source), JsonSerializer.Serialize(duplicate));
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { }
        }
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("import")]
    [Trait("Category", "Integration")]
    public async Task NestedActionAfterDuplicateOrImport_ReachesItsHandler(string sourceKind)
    {
        using var context = new RuntimeTestContext();
        var marker = Path.Combine(context.Root, "condition.marker");
        await File.WriteAllTextAsync(marker, "ready");
        var nested = new ActionDefinition
        {
            Type = CountingHandler.TypeId,
            Parameters = new JsonObject { ["value"] = "nested" }
        };
        var source = new ProfileDefinition
        {
            Name = "Nested execution",
            Actions =
            [
                new ActionDefinition
                {
                    Type = ActionTypeIds.ConditionIf,
                    Parameters = new JsonObject
                    {
                        [ActionParameterNames.ConditionType] = ConditionTypeIds.FileExists,
                        [ActionParameterNames.ConditionValue] = marker,
                        // Legacy nested payloads used the default PascalCase contract.
                        [ActionParameterNames.ThenActions] = new JsonArray(JsonSerializer.SerializeToNode(nested)),
                        [ActionParameterNames.ElseActions] = new JsonArray()
                    }
                }
            ]
        };
        var profile = await CreateVariantAsync(sourceKind, source, context.Root);
        var handler = new CountingHandler();
        var registry = new ActionRegistry
        ([
            new ConditionIfActionHandler(new TestServiceManager(ServiceDesiredStateIds.Running, true)),
            handler
        ]);
        var runner = new ProfileRunner(registry, context.SessionRepository);

        var session = await runner.RunAsync(profile);

        Assert.Equal(ExecutionSessionStatus.Completed, session.Status);
        Assert.Equal(1, handler.ExecutionCount);
        Assert.Contains(session.Journal, entry => entry.ParentActionId == profile.Actions[0].Id &&
                                                  entry.ActionType == CountingHandler.TypeId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Import_NormalizesCollidingAndMissingIdentityWithoutChangingTypeParametersOrOrder()
    {
        var source = CreateProfile(4);
        source.Actions[1].Id = source.Actions[0].Id;
        source.Actions[2].Id = Guid.Empty;
        var oldProfileId = source.Id;
        var exchange = new ProfileExchangeService();
        var directory = Path.Combine(Path.GetTempPath(), $"SwitchBoard-profile-import-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "profile.sbprofile");
            await exchange.ExportAsync(source, path);
            var imported = await exchange.ImportAsync(path);

            Assert.NotEqual(oldProfileId, imported.Id);
            Assert.DoesNotContain(imported.Actions, action => action.Id == Guid.Empty);
            Assert.Equal(imported.Actions.Count, imported.Actions.Select(action => action.Id).Distinct().Count());
            Assert.Equal(source.Actions.Select(action => action.Type), imported.Actions.Select(action => action.Type));
            Assert.Equal(source.Actions.Select(action => action.SortOrder), imported.Actions.Select(action => action.SortOrder));
            Assert.Equal(source.Actions.Select(action => action.Parameters.ToJsonString()),
                imported.Actions.Select(action => action.Parameters.ToJsonString()));
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { }
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Duplicate_DoesNotCopyExecutionOrRestoreSessionState()
    {
        using var context = new RuntimeTestContext();
        var source = new ProfileDefinition
        {
            Name = "State source",
            Actions =
            [
                new ActionDefinition
                {
                    Type = TestReversibleHandler.TypeId,
                    RestoreBehavior = ActionRestoreBehavior.RestorePreviousState,
                    Parameters = new JsonObject { ["key"] = "source" }
                }
            ]
        };
        var sourceViewModel = new ProfileItemViewModel(source, new TestLocalizationService());
        sourceViewModel.SetExecutionState(ProfileExecutionState.Executing);
        sourceViewModel.Actions[0].SetExecutionState(ActionExecutionState.Running);
        await context.Runner.RunAsync(source);

        var duplicate = new ProfileExchangeService().CloneForDuplicate(sourceViewModel.ToModel());
        var duplicateViewModel = new ProfileItemViewModel(duplicate, new TestLocalizationService());

        Assert.Equal(ProfileExecutionState.Normal, duplicateViewModel.ExecutionState);
        Assert.Equal(ActionExecutionState.Pending, duplicateViewModel.Actions[0].ExecutionState);
        Assert.NotNull(await context.SessionRepository.GetLatestPendingAsync(source.Id));
        Assert.Null(await context.SessionRepository.GetLatestPendingAsync(duplicate.Id));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task DuplicateActionId_IsReportedAndDoesNotPoisonTheNextRun()
    {
        using var context = new RuntimeTestContext();
        var duplicateId = Guid.NewGuid();
        var malformed = CreateProfile(2);
        malformed.Actions[0].Id = duplicateId;
        malformed.Actions[1].Id = duplicateId;
        var valid = CreateProfile(2);
        var handler = new CountingHandler();
        var registry = new ActionRegistry([handler]);
        var logger = new RecordingLogger();
        var runner = new ProfileRunner(registry, context.SessionRepository, logger);
        using var viewModel = CreateMainViewModel([malformed, valid], runner, registry, context, logger);

        await viewModel.RunProfileFromTrayAsync(malformed.Id);

        Assert.Null(viewModel.LastExecutionSession);
        Assert.Contains("duplicate action identifier", viewModel.ExecutionErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(viewModel.IsProfileRunning);
        Assert.False(runner.IsRunning);
        Assert.Contains(logger.Messages, message => message.Contains("RUN_REJECTED", StringComparison.Ordinal));

        await viewModel.RunProfileFromTrayAsync(valid.Id);

        Assert.Equal(ExecutionSessionStatus.Completed, viewModel.LastExecutionSession!.Status);
        Assert.Equal(2, handler.ExecutionCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Runner_ReleasesGateAndUsesFreshCancellationAfterFailureAndCancel()
    {
        using var context = new RuntimeTestContext();
        var handler = new ControllableHandler { FailNext = true };
        var runner = new ProfileRunner(new ActionRegistry([handler]), context.SessionRepository);
        var profile = CreateProfile(1);

        var failed = await runner.RunAsync(profile);
        Assert.Equal(ExecutionSessionStatus.CompletedWithErrors, failed.Status);
        Assert.False(runner.IsRunning);

        handler.Block = true;
        using var cancellation = new CancellationTokenSource();
        var cancelledRun = runner.RunAsync(profile, cancellationToken: cancellation.Token);
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        var cancelled = await cancelledRun;
        Assert.Equal(ExecutionSessionStatus.Cancelled, cancelled.Status);
        Assert.False(runner.IsRunning);

        handler.Block = false;
        var successful = await runner.RunAsync(profile);
        Assert.Equal(ExecutionSessionStatus.Completed, successful.Status);
        Assert.False(runner.IsRunning);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task DuplicateAndImportedProfiles_PassPreflight()
    {
        var source = CreateProfile(6);
        var exchange = new ProfileExchangeService();
        var duplicate = exchange.CloneForDuplicate(source);
        var directory = Path.Combine(Path.GetTempPath(), $"SwitchBoard-preflight-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "profile.sbprofile");
            await exchange.ExportAsync(source, path);
            var imported = await exchange.ImportAsync(path);
            var service = new ProfilePreflightService();
            var localization = new TestLocalizationService();

            Assert.False(service.Analyze(new ProfileItemViewModel(duplicate, localization), true).HasErrors);
            Assert.False(service.Analyze(new ProfileItemViewModel(imported, localization), true).HasErrors);
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch { }
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void SharedMutableActionOrParameterReference_IsRejectedBeforeExecution()
    {
        var sharedParameters = new JsonObject { ["value"] = "shared" };
        var sharedAction = new ActionDefinition { Type = CountingHandler.TypeId, Parameters = sharedParameters };
        var profileWithSharedAction = new ProfileDefinition { Actions = [sharedAction, sharedAction] };
        var otherAction = new ActionDefinition { Type = CountingHandler.TypeId, Parameters = sharedParameters };
        var profileWithSharedParameters = new ProfileDefinition { Actions = [sharedAction, otherAction] };

        var actionResult = ProfileRuntimeValidator.Validate(profileWithSharedAction);
        var parameterResult = ProfileRuntimeValidator.Validate(profileWithSharedParameters);

        Assert.Contains(actionResult.Errors, error => error.Contains("same mutable action instance", StringComparison.Ordinal));
        Assert.Contains(parameterResult.Errors, error => error.Contains("same mutable Parameters", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void CanExecuteFalse_RecordsTheExactBlockingReason()
    {
        using var context = new RuntimeTestContext();
        var logger = new RecordingLogger();
        var registry = new ActionRegistry([new CountingHandler()]);
        var invalid = new ProfileDefinition
        {
            Name = "Invalid",
            Actions = [new ActionDefinition { Type = ActionTypeIds.ProgramRun, Parameters = new JsonObject() }]
        };
        using var viewModel = CreateMainViewModel([invalid],
            new ProfileRunner(registry, context.SessionRepository, logger), registry, context, logger);

        Assert.False(viewModel.RunProfileCommand.CanExecute(null));
        Assert.Contains(logger.Messages, message =>
            message.Contains("CAN_EXECUTE_FALSE", StringComparison.Ordinal) &&
            message.Contains("Reason=ValidationError", StringComparison.Ordinal));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ActionEditorComboBoxes_SetSelectedValuePathBeforeSelectedValue()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var controls = Path.Combine(root, "Controls");
        var violations = new List<string>();
        foreach (var path in Directory.EnumerateFiles(controls, "*.xaml", SearchOption.AllDirectories))
        {
            var xaml = File.ReadAllText(path);
            foreach (Match match in Regex.Matches(xaml, "<ComboBox\\b[^>]*>"))
            {
                var selectedValue = match.Value.IndexOf("SelectedValue=", StringComparison.Ordinal);
                var selectedValuePath = match.Value.IndexOf("SelectedValuePath=", StringComparison.Ordinal);
                if (selectedValue >= 0 && selectedValuePath >= 0 && selectedValue < selectedValuePath)
                    violations.Add($"{Path.GetRelativePath(root, path)}: {match.Value}");
            }
        }

        Assert.Empty(violations);
    }

    private static async Task<ProfileDefinition> CreateVariantAsync(string sourceKind, ProfileDefinition source,
        string directory)
    {
        var exchange = new ProfileExchangeService();
        if (sourceKind == "new") return source;
        if (sourceKind == "duplicate") return exchange.CloneForDuplicate(source);
        var path = Path.Combine(directory, $"{Guid.NewGuid():N}.sbprofile");
        await exchange.ExportAsync(source, path);
        return await exchange.ImportAsync(path);
    }

    private static ProfileDefinition CreateProfile(int count) => new()
    {
        Name = $"Safe {count}",
        Actions = Enumerable.Range(0, count).Select(index => new ActionDefinition
        {
            Type = CountingHandler.TypeId,
            SortOrder = index,
            Name = $"Action {index + 1}",
            Parameters = new JsonObject { ["value"] = index }
        }).ToList()
    };

    private static ProfileDefinition CreateProfileWithNestedAction()
    {
        var nested = new ActionDefinition
        {
            Type = CountingHandler.TypeId,
            SortOrder = 0,
            Parameters = new JsonObject { ["value"] = "nested" }
        };
        return new ProfileDefinition
        {
            Name = "Nested",
            Actions =
            [
                new ActionDefinition
                {
                    Type = CountingHandler.TypeId, SortOrder = 0,
                    Parameters = new JsonObject { ["value"] = "top" }
                },
                new ActionDefinition
                {
                    Type = ActionTypeIds.ConditionIf, SortOrder = 1,
                    Parameters = new JsonObject
                    {
                        [ActionParameterNames.ConditionType] = "serviceRunning",
                        [ActionParameterNames.ConditionValue] = "Spooler",
                        [ActionParameterNames.ThenActions] = new JsonArray(ActionDefinitionJson.Serialize(nested)),
                        [ActionParameterNames.ElseActions] = new JsonArray()
                    }
                }
            ]
        };
    }

    private static ActionDefinition Nested(ActionDefinition condition) =>
        ActionDefinitionJson.Deserialize(condition.Parameters[ActionParameterNames.ThenActions]!.AsArray()[0])!;

    private static ProfileDefinition CreateProblemProfileShape()
    {
        var types = new[]
        {
            ActionTypeIds.ProgramRun, ActionTypeIds.ProcessConfigure, ActionTypeIds.ProcessConfigure, ActionTypeIds.Delay,
            ActionTypeIds.ConditionIf, ActionTypeIds.WaitProcessStart, ActionTypeIds.ProcessConfigure,
            ActionTypeIds.DisplayConfigure, ActionTypeIds.ProcessConfigure, ActionTypeIds.Comment
        };
        return new ProfileDefinition
        {
            Name = "Large structural smoke",
            Actions = types.Select((type, index) => new ActionDefinition
            {
                Type = type,
                SortOrder = index,
                IsEnabled = index is 0 or 9,
                Parameters = type == ActionTypeIds.ProgramRun
                    ? new JsonObject
                    {
                        [ActionParameterNames.Target] = Path.Combine(Environment.SystemDirectory, "notepad.exe"),
                        [ActionParameterNames.TargetType] = TargetTypeIds.Executable
                    }
                    : type == ActionTypeIds.ConditionIf
                    ? new JsonObject
                    {
                        [ActionParameterNames.ThenActions] = new JsonArray(),
                        [ActionParameterNames.ElseActions] = new JsonArray()
                    }
                    : type == ActionTypeIds.Comment
                        ? new JsonObject { [ActionParameterNames.CommentText] = "Section" }
                        : new JsonObject()
            }).ToList()
        };
    }

    private static MainWindowViewModel CreateMainViewModel(IReadOnlyList<ProfileDefinition> profiles,
        ProfileRunner runner, IActionRegistry registry, RuntimeTestContext context, IAppLogger? logger = null)
    {
        var category = new CategoryDefinition { Name = "Profiles" };
        foreach (var profile in profiles) profile.CategoryId = category.Id;
        var catalog = new SwitchBoardCatalog { Categories = [category], Profiles = profiles.ToList() };
        return new MainWindowViewModel(new TestCatalogService(), new TestDialogService(), catalog,
            new TestThemeManager(), new TestLocalizationService(), new TestSettingsRepository(),
            new UserSettings
            {
                ThemeId = "graphite", LanguageId = "en", LastSelectedProfileId = profiles[0].Id
            }, runner, new ProfileRestoreRunner(registry, context.SessionRepository), context.SessionRepository,
            new TestCompletionBehavior(), new TestDisplayManager(new("", "", "", 1, 1, 1, 32, 0, 0, 0, 0)),
            new TestCustomThemeEditorService(), appDataPaths: new AppDataPaths(context.AppDataRoot), logger: logger);
    }

    private static void AssertStages(RecordingLogger logger, params string[] stages)
    {
        foreach (var stage in stages)
            Assert.Contains(logger.Messages, message => message.Contains(stage, StringComparison.Ordinal));
    }

    private sealed class CountingHandler : IActionHandler
    {
        public const string TypeId = "test.profile-run";
        private readonly string _actionType;

        public CountingHandler(string actionType = TypeId) => _actionType = actionType;

        public string ActionType => _actionType;
        public int ExecutionCount { get; private set; }

        public Task<ActionExecutionResult> ExecuteAsync(ActionDefinition action, ActionExecutionContext context,
            CancellationToken cancellationToken)
        {
            ExecutionCount++;
            return Task.FromResult(ActionExecutionResult.Success());
        }

        public Task<ActionExecutionResult> RestoreAsync(ActionDefinition action, JsonObject restoreState,
            ActionExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(ActionExecutionResult.Skipped());
    }

    private sealed class ControllableHandler : IActionHandler
    {
        public string ActionType => CountingHandler.TypeId;
        public bool FailNext { get; set; }
        public bool Block { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ActionExecutionResult> ExecuteAsync(ActionDefinition action, ActionExecutionContext context,
            CancellationToken cancellationToken)
        {
            if (FailNext)
            {
                FailNext = false;
                return ActionExecutionResult.Failure("Expected failure.");
            }
            if (Block)
            {
                Started.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return ActionExecutionResult.Success();
        }

        public Task<ActionExecutionResult> RestoreAsync(ActionDefinition action, JsonObject restoreState,
            ActionExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(ActionExecutionResult.Skipped());
    }

    private sealed class RecordingLogger : IAppLogger
    {
        public List<string> Messages { get; } = [];
        public void Info(string area, string message) => Messages.Add($"INFO {area}: {message}");
        public void Warning(string area, string message) => Messages.Add($"WARN {area}: {message}");
        public void Error(string area, Exception exception, string? message = null) =>
            Messages.Add($"ERROR {area}: {message} {exception.Message}");
    }
}
