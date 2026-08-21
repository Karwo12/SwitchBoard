using SwitchBoard.RuntimeTests.TestInfrastructure;

namespace SwitchBoard.RuntimeTests.ViewModels;

[Collection("Windows runtime")]
public sealed class UndoAndCatalogTests : RuntimeTestBase
{
    [Fact]
    [Trait("Category", "Unit")]
    public void Undo_TextBoxEdits_AreCoalesced()
    {
        var undo = new UndoService<string>(75, TimeSpan.FromSeconds(2));
        undo.Record("before typing", "field:name", true);
        undo.Record("after N", "field:name", true);
        undo.Record("after No", "field:name", true);

        Assert.Equal(1, undo.Count);
        Assert.True(undo.TryUndo(out var state));
        Assert.Equal("before typing", state);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ActionPicker_UsesTheFourUserFacingCategories()
    {
        using var scenario = new MainWindowScenario();
        var map = scenario.Main.AvailableActionTypes.ToDictionary(item => item.TypeId, item => item.CategoryResourceKey);

        Assert.Equal(ActionTypeIds.All.Count - 1, map.Count);
        Assert.Equal("ActionPicker.Category.Programs", map[ActionTypeIds.ProgramRun]);
        Assert.Equal("ActionPicker.Category.Programs", map[ActionTypeIds.ProcessConfigure]);
        Assert.Equal("ActionPicker.Category.SystemDevices", map[ActionTypeIds.ServiceSetState]);
        Assert.Equal("ActionPicker.Category.SystemDevices", map[ActionTypeIds.AudioConfigure]);
        Assert.Equal("ActionPicker.Category.WaitingTiming", map[ActionTypeIds.WaitWindow]);
        Assert.Equal("ActionPicker.Category.WaitingTiming", map[ActionTypeIds.Delay]);
        Assert.Equal("ActionPicker.Category.Automation", map[ActionTypeIds.ConditionIf]);
        Assert.DoesNotContain("ActionPicker.Category.Windows", map.Values);
        Assert.DoesNotContain("ActionPicker.Category.Multimedia", map.Values);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ActionPicker_SearchFindsActionsByKeyword()
    {
        using var scenario = new MainWindowScenario();
        scenario.Main.ActionPickerSearch = "warunek";

        Assert.Contains(scenario.Main.FilteredActionTypes, item => item.TypeId == ActionTypeIds.ConditionIf);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Undo_CatalogMutationsRestoreItemsAndIds()
    {
        using var scenario = new MainWindowScenario();
        var main = scenario.Main;
        var actionId = scenario.OriginalActionId;
        var initialActions = main.SelectedProfile!.Actions.Count;
        main.AddActionCommand.Execute(null);
        main.UndoCommand.Execute(null);
        Assert.Equal(initialActions, main.SelectedProfile.Actions.Count);

        var initialProfiles = main.Profiles.Count;
        main.AddProfileCommand.Execute(null);
        main.UndoCommand.Execute(null);
        Assert.Equal(initialProfiles, main.Profiles.Count);

        var initialCategories = main.Categories.Count;
        main.AddCategoryCommand.Execute(null);
        main.UndoCommand.Execute(null);
        Assert.Equal(initialCategories, main.Categories.Count);

        var action = main.SelectedProfile.Actions.First(item => item.Id == actionId);
        main.DeleteActionCommand.Execute(action);
        main.UndoCommand.Execute(null);
        Assert.Contains(main.SelectedProfile.Actions, item => item.Id == actionId);

        main.DeleteProfileCommand.Execute(main.SelectedProfile);
        main.UndoCommand.Execute(null);
        Assert.Contains(main.Profiles, item => item.Id == scenario.OriginalProfileId);
        main.DeleteCategoryCommand.Execute(main.SelectedCategory);
        main.UndoCommand.Execute(null);
        Assert.Contains(main.Categories, item => item.Id == scenario.OriginalCategoryId);
        Assert.Contains(main.Profiles, item => item.Id == scenario.OriginalProfileId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Undo_RenamesRestoreProfileCategoryAndActionNames()
    {
        using var scenario = new MainWindowScenario();
        var main = scenario.Main;
        var originalProfileName = main.SelectedProfile!.Name;
        main.SelectedProfile.Name = "Renamed profile";
        main.UndoCommand.Execute(null);
        Assert.Equal(originalProfileName, main.SelectedProfile.Name);

        var originalCategoryName = main.SelectedCategory!.Name;
        main.SelectedCategory.Name = "Renamed category";
        main.UndoCommand.Execute(null);
        Assert.Equal(originalCategoryName, main.SelectedCategory.Name);

        var action = main.SelectedProfile.Actions.First(item => item.Id == scenario.OriginalActionId);
        var originalActionName = action.Name;
        action.Name = "Renamed action";
        main.UndoCommand.Execute(null);
        Assert.Equal(originalActionName, main.SelectedProfile.Actions.First(item => item.Id == scenario.OriginalActionId).Name);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Undo_ActionPropertyChangesRestoreThePreviousValues()
    {
        using var scenario = new MainWindowScenario();
        var main = scenario.Main;
        ChangeAndUndo(main, scenario.OriginalActionId, item => item.Target = "changed.exe",
            item => item.Target == "one.exe");
        ChangeAndUndo(main, scenario.OriginalActionId, item => item.TimeoutSeconds = 42,
            item => item.TimeoutSeconds == 0);
        ChangeAndUndo(main, scenario.OriginalActionId, item => item.FailurePolicyId = "stop",
            item => item.FailurePolicyId == "continue");
        ChangeAndUndo(main, scenario.OriginalActionId, item => item.RestoreBehaviorId = "previous",
            item => item.RestoreBehaviorId == "none");
        ChangeAndUndo(main, scenario.OriginalActionId, item => item.IsEnabled = false,
            item => item.IsEnabled);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Undo_ActionReorderRestoresTheOriginalOrder()
    {
        using var scenario = new MainWindowScenario();
        var main = scenario.Main;
        var first = main.SelectedProfile!.Actions.First(item => item.Id == scenario.OriginalActionId);
        main.MoveActionDownCommand.Execute(first);
        main.UndoCommand.Execute(null);

        Assert.Equal(scenario.OriginalActionId, main.SelectedProfile.Actions[0].Id);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Undo_MultipleStepsAndSaveKeepUndoAvailable()
    {
        using var scenario = new MainWindowScenario();
        var main = scenario.Main;
        main.SelectedProfile!.Name = "First change";
        main.SelectedProfile.Actions.First(item => item.Id == scenario.OriginalActionId).TimeoutSeconds = 9;
        main.UndoCommand.Execute(null);
        main.UndoCommand.Execute(null);
        Assert.Equal("Profile", main.SelectedProfile.Name);
        Assert.Equal(0, main.SelectedProfile.Actions.First(item => item.Id == scenario.OriginalActionId).TimeoutSeconds);
        Assert.False(main.UndoCommand.CanExecute(null));

        main.SelectedProfile.Actions.First(item => item.Id == scenario.OriginalActionId).Target = "saved-change.exe";
        main.SaveCommand.Execute(null);
        await TestHelpers.WaitUntilAsync(() => !main.HasUnsavedChanges);
        main.UndoCommand.Execute(null);
        Assert.Equal("one.exe", main.SelectedProfile.Actions.First(item => item.Id == scenario.OriginalActionId).Target);
        Assert.True(main.HasUnsavedChanges);
        Assert.Contains(scenario.CatalogService.Saved.Categories, item => item.Id == scenario.OriginalCategoryId);
        Assert.Contains(scenario.CatalogService.Saved.Profiles, item => item.Id == scenario.OriginalProfileId &&
            item.Actions.Any(action => action.Id == scenario.OriginalActionId));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CatalogReorder_Category_IsPersistedImmediately()
    {
        using var scenario = new DragScenario();
        await scenario.Main.ApplyReorderAsync(new(ReorderItemKind.Category, scenario.Main.Categories[0],
            scenario.Main.Categories[1], 2));

        Assert.Equal(new[] { scenario.CategoryB.Id, scenario.CategoryA.Id }, scenario.Main.Categories.Select(item => item.Id));
        Assert.Equal(new[] { scenario.CategoryB.Id, scenario.CategoryA.Id },
            scenario.Service.Saved.Categories.OrderBy(item => item.SortOrder).Select(item => item.Id));
        Assert.False(scenario.Main.HasUnsavedChanges);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CatalogReorder_ProfileWithinCategory_IsPersistedImmediately()
    {
        using var scenario = new DragScenario();
        scenario.Main.SelectedCategory = scenario.Main.Categories.Single(item => item.Id == scenario.CategoryA.Id);
        var profileA1 = scenario.Main.Profiles.Single(item => item.Id == scenario.ProfileA1.Id);
        await scenario.Main.ApplyReorderAsync(new(ReorderItemKind.Profile, profileA1,
            scenario.Main.Profiles.Single(item => item.Id == scenario.ProfileA2.Id), 2, scenario.CategoryA.Id));

        Assert.Equal(new[] { scenario.ProfileA2.Id, scenario.ProfileA1.Id },
            scenario.Main.Profiles.Select(item => item.Id));
        Assert.Equal(new[] { scenario.ProfileA2.Id, scenario.ProfileA1.Id },
            scenario.Service.Saved.Profiles.Where(item => item.CategoryId == scenario.CategoryA.Id)
                .OrderBy(item => item.SortOrder).Select(item => item.Id));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CatalogReorder_ProfileToAnotherCategory_SelectsAndPersistsTheTarget()
    {
        using var scenario = new DragScenario();
        scenario.Main.SelectedCategory = scenario.Main.Categories.Single(item => item.Id == scenario.CategoryA.Id);
        var profileA1 = scenario.Main.Profiles.Single(item => item.Id == scenario.ProfileA1.Id);
        var target = scenario.Main.Categories.Single(item => item.Id == scenario.CategoryB.Id);
        await scenario.Main.ApplyReorderAsync(new(ReorderItemKind.Profile, profileA1, target,
            scenario.Main.Categories.Count));

        Assert.Equal(scenario.CategoryB.Id, scenario.Main.SelectedCategory?.Id);
        Assert.Equal(scenario.ProfileA1.Id, scenario.Main.SelectedProfile?.Id);
        Assert.Equal(scenario.CategoryB.Id, scenario.Service.Saved.Profiles.Single(item => item.Id == scenario.ProfileA1.Id).CategoryId);
        Assert.Equal(new[] { scenario.ProfileB1.Id, scenario.ProfileA1.Id },
            scenario.Service.Saved.Profiles.Where(item => item.CategoryId == scenario.CategoryB.Id)
                .OrderBy(item => item.SortOrder).Select(item => item.Id));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task CatalogReorder_Action_IsPersistedAndUndoable()
    {
        using var scenario = new DragScenario();
        await scenario.Main.ApplyReorderAsync(new(ReorderItemKind.Category, scenario.Main.Categories[0],
            scenario.Main.Categories[1], 2));
        scenario.Main.SelectedCategory = scenario.Main.Categories.Single(item => item.Id == scenario.CategoryA.Id);
        var profileA1 = scenario.Main.Profiles.Single(item => item.Id == scenario.ProfileA1.Id);
        await scenario.Main.ApplyReorderAsync(new(ReorderItemKind.Profile, profileA1,
            scenario.Main.Profiles.Single(item => item.Id == scenario.ProfileA2.Id), 2, scenario.CategoryA.Id));
        var target = scenario.Main.Categories.Single(item => item.Id == scenario.CategoryB.Id);
        await scenario.Main.ApplyReorderAsync(new(ReorderItemKind.Profile, profileA1, target,
            scenario.Main.Categories.Count));
        scenario.Main.SelectedCategory = scenario.Main.Categories.Single(item => item.Id == scenario.CategoryB.Id);
        var profile = scenario.Main.SelectedProfile!;
        var first = profile.Actions[0];
        var secondId = profile.Actions[1].Id;
        await scenario.Main.ApplyReorderAsync(new(ReorderItemKind.Action, first, profile.Actions[1], 2, profile.Id));

        Assert.Equal(new[] { secondId, first.Id }, profile.Actions.Select(item => item.Id));
        Assert.Equal(new[] { secondId, first.Id }, scenario.Service.Saved.Profiles.Single(item => item.Id == scenario.ProfileA1.Id)
            .Actions.OrderBy(item => item.SortOrder).Select(item => item.Id));
        Assert.True(scenario.Service.SaveCount >= 4);
        scenario.Main.UndoCommand.Execute(null);
        Assert.Equal(first.Id, scenario.Main.SelectedProfile!.Actions[0].Id);
        Assert.True(scenario.Main.HasUnsavedChanges);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ThemeAdd_CancelLiveDraftLeavesNoCollectionItem()
    {
        using var scenario = new MainWindowScenario();
        var initialCount = scenario.Settings.CustomThemes.Count;
        var saves = scenario.SettingsRepository.SaveCount;
        scenario.ThemeEditor.EditActions.Enqueue(request =>
        {
            var draft = request.Colors.Clone();
            draft.Background = "#FFFF00FF";
            request.ApplyTemporary?.Invoke(draft);
        });
        scenario.ThemeEditor.Results.Enqueue(null);
        scenario.Main.AddThemeCommand.Execute(null);
        await TestHelpers.WaitUntilAsync(() => scenario.Main.AddThemeCommand.CanExecute(null));

        Assert.Equal(initialCount, scenario.Settings.CustomThemes.Count);
        Assert.Equal(ThemeIds.Graphite, scenario.Settings.ThemeId);
        Assert.Equal(ThemeIds.Graphite, scenario.ThemeManager.CurrentThemeId);
        Assert.True(scenario.ThemeManager.TemporaryApplyCount >= 2);
        Assert.Equal(saves, scenario.SettingsRepository.SaveCount);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ThemeAdd_CreatePersistsAndReloadsTheActiveTheme()
    {
        using var scenario = new MainWindowScenario();
        var colors = CustomThemeSettings.CreateDefault();
        colors.Background = "#FFFFFFFF";
        colors.PrimaryText = "#FFFFFFFF";
        scenario.ThemeEditor.Results.Enqueue(new("Snow", colors));
        scenario.Main.AddThemeCommand.Execute(null);
        await TestHelpers.WaitUntilAsync(() => scenario.Settings.CustomThemes.Count == 1);
        var option = scenario.Main.ThemeOptions.Single(item => item.IsCustom);

        Assert.Equal("Snow", option.DisplayName);
        Assert.True(option.IsActive);
        Assert.Equal(option.Id, scenario.Settings.ThemeId);
        Assert.Equal("Snow", scenario.SettingsRepository.Saved.CustomThemes.Single().Name);

        var manager = new TestThemeManager();
        manager.ApplyTheme(scenario.SettingsRepository.Saved.ThemeId,
            scenario.SettingsRepository.Saved.CustomThemes.Single().Colors);
        var restarted = new MainWindowViewModel(scenario.CatalogService, new TestDialogService(), scenario.Catalog,
            manager, scenario.Localization, new TestSettingsRepository(), scenario.SettingsRepository.Saved,
            scenario.Context.Runner, new ProfileRestoreRunner(scenario.Context.Registry, scenario.Context.SessionRepository),
            scenario.Context.SessionRepository, new TestCompletionBehavior(),
            new TestDisplayManager(new("", "", "", 1, 1, 1, 32, 0, 0, 0, 0)), new TestCustomThemeEditorService());
        Assert.Contains(restarted.ThemeOptions, item => item.IsCustom && item.DisplayName == "Snow");
        Assert.Equal("Snow", restarted.SelectedThemeOption?.DisplayName);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ThemeEdit_InactiveTheme_CancelRestoresTheActiveTheme()
    {
        using var scenario = await CreateThemeWithSnowAsync();
        var snow = scenario.Main.ThemeOptions.Single(item => item.IsCustom);
        scenario.Main.SelectedThemeOption = scenario.Main.ThemeOptions.Single(item => item.Id == ThemeIds.Graphite);
        await TestHelpers.WaitUntilAsync(() => scenario.Settings.ThemeId == ThemeIds.Graphite);
        var before = scenario.Settings.CustomThemes.Single(item => item.Id == snow.Id).Colors.Clone();
        scenario.ThemeEditor.EditActions.Enqueue(request =>
        {
            var draft = request.Colors.Clone();
            draft.Card = "#FFFF00FF";
            request.ApplyTemporary?.Invoke(draft);
        });
        scenario.ThemeEditor.Results.Enqueue(null);
        scenario.Main.EditThemeCommand.Execute(snow.Id);
        await TestHelpers.WaitUntilAsync(() => scenario.Main.EditThemeCommand.CanExecute(snow.Id));

        Assert.Equal(ThemeIds.Graphite, scenario.Settings.ThemeId);
        Assert.Equal(ThemeIds.Graphite, scenario.ThemeManager.CurrentThemeId);
        Assert.Equal(before.Card, scenario.Settings.CustomThemes.Single(item => item.Id == snow.Id).Colors.Card);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ThemeEdit_CustomTheme_CommitsWithoutChangingItsId()
    {
        using var scenario = await CreateThemeWithSnowAsync();
        var snow = scenario.Main.ThemeOptions.Single(item => item.IsCustom);
        var colors = CustomThemeSettings.CreateDefault();
        colors.Background = "#FF000000";
        colors.PrimaryText = "#FF000000";
        scenario.ThemeEditor.Results.Enqueue(new("Snow edited", colors));
        scenario.Main.EditThemeCommand.Execute(snow.Id);
        await TestHelpers.WaitUntilAsync(() => snow.DisplayName == "Snow edited");

        Assert.Equal("#FF000000", scenario.Settings.CustomThemes.Single().Colors.Background);
        Assert.True(snow.IsActive);
        Assert.Equal(snow.Id, scenario.Settings.CustomThemes.Single().Id);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ThemeDuplicate_CancelLeavesNoOrphanAndRestoresActiveTheme()
    {
        using var scenario = await CreateThemeWithSnowAsync();
        var snow = scenario.Main.ThemeOptions.Single(item => item.IsCustom);
        var count = scenario.Settings.CustomThemes.Count;
        scenario.ThemeEditor.EditActions.Enqueue(request => request.ApplyTemporary?.Invoke(request.Colors.Clone()));
        scenario.ThemeEditor.Results.Enqueue(null);
        scenario.Main.DuplicateThemeCommand.Execute(snow.Id);
        await TestHelpers.WaitUntilAsync(() => scenario.Main.DuplicateThemeCommand.CanExecute(snow.Id));

        Assert.Equal(count, scenario.Settings.CustomThemes.Count);
        Assert.Equal(snow.Id, scenario.Settings.ThemeId);
        Assert.Equal(snow.Id, scenario.ThemeManager.CurrentThemeId);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ThemeDuplicate_RenameAndDeleteKeepCollectionConsistent()
    {
        using var scenario = await CreateThemeWithSnowAsync();
        var snow = scenario.Main.ThemeOptions.Single(item => item.IsCustom);
        var colors = CustomThemeSettings.CreateDefault();
        colors.Background = "#FF000000";
        scenario.ThemeEditor.Results.Enqueue(new("Snow copy", colors));
        scenario.Main.DuplicateThemeCommand.Execute(snow.Id);
        await TestHelpers.WaitUntilAsync(() => scenario.Settings.CustomThemes.Count == 2);
        var copy = scenario.Main.ThemeOptions.Single(item => item.IsCustom && item.DisplayName == "Snow copy");
        Assert.NotEqual(snow.Id, copy.Id);
        Assert.True(copy.IsActive);

        scenario.ThemeEditor.RenameResults.Enqueue("Renamed copy");
        scenario.Main.RenameThemeCommand.Execute(copy.Id);
        await TestHelpers.WaitUntilAsync(() => copy.DisplayName == "Renamed copy");
        Assert.Contains(scenario.Settings.CustomThemes, item => item.Name == "Renamed copy");
        scenario.Main.DeleteThemeCommand.Execute(copy.Id);
        await TestHelpers.WaitUntilAsync(() => scenario.Settings.CustomThemes.Count == 1);
        Assert.Equal(ThemeIds.Graphite, scenario.Main.SelectedThemeOption?.Id);
        Assert.True(scenario.Main.SelectedThemeOption?.IsActive);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ThemeDuplicate_BuiltInTheme_CreatesAndActivatesACopy()
    {
        using var scenario = new MainWindowScenario();
        scenario.ThemeEditor.Results.Enqueue(new("Graphite copy", CustomThemeSettings.CreateDefault()));
        scenario.Main.DuplicateThemeCommand.Execute(ThemeIds.Graphite);
        await TestHelpers.WaitUntilAsync(() => scenario.Settings.CustomThemes.Count == 1);

        Assert.Equal("Graphite copy", scenario.Main.SelectedThemeOption?.DisplayName);
        Assert.True(scenario.Main.SelectedThemeOption?.IsCustom);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ThemeEditor_DuplicateName_IsRejected()
    {
        var vm = new CustomThemeEditorViewModel(new(
            CustomThemeEditMode.Add, "Snow edited", CustomThemeSettings.CreateDefault(), ["Snow edited"]),
            new TestLocalizationService());

        Assert.False(vm.IsNameValid);
        Assert.NotEmpty(vm.NameError);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ThemeDuplicate_UsesExactSourceAndDeterministicUniqueNames()
    {
        using var context = new RuntimeTestContext();
        var sources = new[]
        {
            new CustomThemeDefinition { Name = "First", Colors = CustomThemeSettings.CreateDefault() },
            new CustomThemeDefinition { Name = "Second", Colors = CustomThemeSettings.CreateDefault() },
            new CustomThemeDefinition { Name = "Last", Colors = CustomThemeSettings.CreateDefault() }
        };
        sources[0].Colors.Background = "#FF110000";
        sources[0].Colors.PrimaryButtonBackground = "#FFFF1111";
        sources[1].Colors.Background = "#FF001100";
        sources[1].Colors.PrimaryButtonBackground = "#FF11FF11";
        sources[2].Colors.Background = "#FF000011";
        sources[2].Colors.PrimaryButtonBackground = "#FF1111FF";
        var settings = new UserSettings { ThemeId = sources[1].Id, CustomThemes = sources.Select(item => item.Clone()).ToList() };
        var manager = new TestThemeManager();
        manager.ApplyTheme(settings.ThemeId, settings.CustomThemes[1].Colors);
        var editor = new TestCustomThemeEditorService { EchoWhenEmpty = true };
        var catalogService = new TestCatalogService();
        var main = new MainWindowViewModel(catalogService, new TestDialogService(), new SwitchBoardCatalog(), manager,
            new TestLocalizationService(), new TestSettingsRepository(), settings, context.Runner,
            new ProfileRestoreRunner(context.Registry, context.SessionRepository), context.SessionRepository,
            new TestCompletionBehavior(), new TestDisplayManager(new("", "", "", 1, 1, 1, 32, 0, 0, 0, 0)), editor);
        Assert.True(main.ThemeOptions.Single(item => item.Id == sources[1].Id).IsActive);

        foreach (var sourceId in new[] { sources[1].Id, sources[0].Id, sources[2].Id, sources[0].Id, sources[0].Id })
        {
            var source = settings.CustomThemes.Single(item => item.Id == sourceId);
            var snapshot = source.Colors.Clone();
            var requests = editor.Requests.Count;
            main.DuplicateThemeCommand.Execute(sourceId);
            await TestHelpers.WaitUntilAsync(() => editor.Requests.Count == requests + 1 &&
                                                     main.DuplicateThemeCommand.CanExecute(sourceId));
            var request = editor.Requests[^1];
            var opened = settings.CustomThemes.SingleOrDefault(item => item.Id == request.ThemeId);
            Assert.NotNull(request.ThemeId);
            Assert.NotEqual(sourceId, request.ThemeId);
            Assert.NotNull(opened);
            Assert.Equal(snapshot.Background, request.Colors.Background);
            Assert.Equal(snapshot.PrimaryButtonBackground, request.Colors.PrimaryButtonBackground);
            Assert.Equal(snapshot.Background, opened!.Colors.Background);
            Assert.NotSame(source.Colors, opened.Colors);
        }
        Assert.Contains(settings.CustomThemes, item => item.Name == "First \u2014 copy");
        Assert.Contains(settings.CustomThemes, item => item.Name == "First \u2014 copy (2)");
        Assert.Contains(settings.CustomThemes, item => item.Name == "First \u2014 copy (3)");
    }

    private static void ChangeAndUndo(MainWindowViewModel main, Guid actionId,
        Action<ActionItemViewModel> change, Func<ActionItemViewModel, bool> restored)
    {
        var action = main.SelectedProfile!.Actions.First(item => item.Id == actionId);
        change(action);
        main.UndoCommand.Execute(null);
        Assert.True(restored(main.SelectedProfile!.Actions.First(item => item.Id == actionId)));
    }

    private static async Task<MainWindowScenario> CreateThemeWithSnowAsync()
    {
        var scenario = new MainWindowScenario();
        var colors = CustomThemeSettings.CreateDefault();
        scenario.ThemeEditor.Results.Enqueue(new("Snow", colors));
        scenario.Main.AddThemeCommand.Execute(null);
        await TestHelpers.WaitUntilAsync(() => scenario.Settings.CustomThemes.Count == 1);
        return scenario;
    }

    private sealed class MainWindowScenario : IDisposable
    {
        public MainWindowScenario()
        {
            Context = new RuntimeTestContext();
            Catalog = new SwitchBoardCatalog
            {
                Categories = [new CategoryDefinition { Name = "Category", SortOrder = 0 }]
            };
            Catalog.Profiles.Add(new ProfileDefinition
            {
                CategoryId = Catalog.Categories[0].Id, Name = "Profile", SortOrder = 0,
                Actions =
                [
                    RuntimeTestContext.Action(ActionTypeIds.ProgramRun, new JsonObject { [ActionParameterNames.Target] = "one.exe" }),
                    RuntimeTestContext.Action(ActionTypeIds.Delay, new JsonObject { [ActionParameterNames.DelaySeconds] = 1 })
                ]
            });
            Catalog.Profiles[0].Actions[0].SortOrder = 0;
            Catalog.Profiles[0].Actions[1].SortOrder = 1;
            OriginalCategoryId = Catalog.Categories[0].Id;
            OriginalProfileId = Catalog.Profiles[0].Id;
            OriginalActionId = Catalog.Profiles[0].Actions[0].Id;
            Localization = new TestLocalizationService();
            CatalogService = new TestCatalogService();
            ThemeEditor = new TestCustomThemeEditorService();
            ThemeManager = new TestThemeManager();
            SettingsRepository = new TestSettingsRepository();
            Settings = new UserSettings { ThemeId = ThemeIds.Graphite, LanguageId = "en" };
            Main = new MainWindowViewModel(CatalogService, new TestDialogService(), Catalog, ThemeManager,
                Localization, SettingsRepository, Settings, Context.Runner,
                new ProfileRestoreRunner(Context.Registry, Context.SessionRepository), Context.SessionRepository,
                new TestCompletionBehavior(), new TestDisplayManager(new("", "", "", 1, 1, 1, 32, 0, 0, 0, 0)), ThemeEditor);
        }

        public RuntimeTestContext Context { get; }
        public SwitchBoardCatalog Catalog { get; }
        public TestCatalogService CatalogService { get; }
        public TestLocalizationService Localization { get; }
        public TestCustomThemeEditorService ThemeEditor { get; }
        public TestThemeManager ThemeManager { get; }
        public TestSettingsRepository SettingsRepository { get; }
        public UserSettings Settings { get; }
        public MainWindowViewModel Main { get; }
        public Guid OriginalCategoryId { get; }
        public Guid OriginalProfileId { get; }
        public Guid OriginalActionId { get; }

        public void Dispose() => Context.Dispose();
    }

    private sealed class DragScenario : IDisposable
    {
        public DragScenario()
        {
            Context = new RuntimeTestContext();
            CategoryA = new CategoryDefinition { Name = "Drag A", SortOrder = 0 };
            CategoryB = new CategoryDefinition { Name = "Drag B", SortOrder = 1 };
            ProfileA1 = new ProfileDefinition
            {
                CategoryId = CategoryA.Id, Name = "A1", SortOrder = 0,
                Actions =
                [
                    RuntimeTestContext.Action(ActionTypeIds.Delay, new JsonObject { [ActionParameterNames.DelaySeconds] = 1 }),
                    RuntimeTestContext.Action(ActionTypeIds.Delay, new JsonObject { [ActionParameterNames.DelaySeconds] = 2 })
                ]
            };
            ProfileA1.Actions[0].SortOrder = 0;
            ProfileA1.Actions[1].SortOrder = 1;
            ProfileA2 = new ProfileDefinition { CategoryId = CategoryA.Id, Name = "A2", SortOrder = 1 };
            ProfileB1 = new ProfileDefinition { CategoryId = CategoryB.Id, Name = "B1", SortOrder = 0 };
            Catalog = new SwitchBoardCatalog
            {
                Categories = [CategoryA, CategoryB], Profiles = [ProfileA1, ProfileA2, ProfileB1]
            };
            Service = new TestCatalogService();
            Main = new MainWindowViewModel(Service, new TestDialogService(), Catalog, new TestThemeManager(),
                new TestLocalizationService(), new TestSettingsRepository(),
                new UserSettings { ThemeId = ThemeIds.Graphite, LanguageId = "en" }, Context.Runner,
                new ProfileRestoreRunner(Context.Registry, Context.SessionRepository), Context.SessionRepository,
                new TestCompletionBehavior(), new TestDisplayManager(new("", "", "", 1, 1, 1, 32, 0, 0, 0, 0)),
                new TestCustomThemeEditorService());
        }

        public RuntimeTestContext Context { get; }
        public CategoryDefinition CategoryA { get; }
        public CategoryDefinition CategoryB { get; }
        public ProfileDefinition ProfileA1 { get; }
        public ProfileDefinition ProfileA2 { get; }
        public ProfileDefinition ProfileB1 { get; }
        public SwitchBoardCatalog Catalog { get; }
        public TestCatalogService Service { get; }
        public MainWindowViewModel Main { get; }
        public void Dispose() => Context.Dispose();
    }
}
