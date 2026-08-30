using SwitchBoard.RuntimeTests.TestInfrastructure;

namespace SwitchBoard.RuntimeTests.ViewModels;

[Collection("Windows runtime")]
public sealed class ActionSelectionTests : RuntimeTestBase
{
    [Fact]
    [Trait("Category", "Unit")]
    public void SelectingProfileDoesNotSelectItsFirstAction()
    {
        using var fixture = new SelectionFixture();
        var main = fixture.Main;

        Assert.Null(main.SelectedAction);

        var firstProfileAction = main.SelectedProfile!.Actions[1];
        main.SelectedAction = firstProfileAction;
        Assert.Same(firstProfileAction, main.SelectedAction);

        main.SelectedProfile = main.Profiles.Single(profile => profile.Id == fixture.ProfileB.Id);
        Assert.Null(main.SelectedAction);

        main.SelectedCategory = main.Categories.Single(category => category.Id == fixture.CategoryB.Id);
        Assert.Null(main.SelectedAction);
        Assert.Equal(fixture.ProfileC.Id, main.SelectedProfile?.Id);

        main.SelectedCategory = main.Categories.Single(category => category.Id == fixture.CategoryA.Id);
        Assert.Null(main.SelectedAction);
        Assert.Equal(fixture.ProfileA.Id, main.SelectedProfile?.Id);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void SelectedProfileBreadcrumbContainsCategoryAndProfileName()
    {
        using var fixture = new SelectionFixture();
        var main = fixture.Main;

        Assert.Equal("Category A > Profile A", main.SelectedProfileBreadcrumb);

        main.SelectedProfile!.Name = "Renamed profile";
        Assert.Equal("Category A > Renamed profile", main.SelectedProfileBreadcrumb);

        main.Categories.Single(category => category.Id == fixture.CategoryA.Id).Name = "Renamed category";
        Assert.Equal("Renamed category > Renamed profile", main.SelectedProfileBreadcrumb);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ExecutionStateRemainsIndependentFromActionSelection()
    {
        using var fixture = new SelectionFixture();
        var main = fixture.Main;
        var action = main.SelectedProfile!.Actions[1];

        Assert.Null(main.SelectedAction);

        action.SetExecutionState(ActionExecutionState.Running);
        Assert.Null(main.SelectedAction);
        Assert.True(action.IsExecutionRunning);

        action.SetExecutionState(ActionExecutionState.Completed);
        Assert.Null(main.SelectedAction);
        Assert.DoesNotContain(main.SelectedProfile.Actions, candidate => candidate.IsExecutionRunning);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ProcessPickerStoresStableNameAndPathWithoutPidHint()
    {
        using var fixture = new SelectionFixture();
        var action = fixture.Main.SelectedProfile!.Actions[0];
        fixture.Dialog.ProcessSelection = new ProcessCandidate(
            4242,
            "AnyDesk",
            "AnyDesk.exe",
            @"C:\Program Files (x86)\AnyDesk\AnyDesk.exe",
            "AnyDesk",
            "AnyDesk",
            "AnyDesk",
            null);

        fixture.Main.SelectProcessCommand.Execute(action);

        Assert.Equal("AnyDesk", action.ProcessName);
        Assert.Equal(@"C:\Program Files (x86)\AnyDesk\AnyDesk.exe", action.ExecutablePath);
        Assert.DoesNotContain("4242", JsonSerializer.Serialize(action.ToModel()), StringComparison.Ordinal);
    }

    private sealed class SelectionFixture : IDisposable
    {
        public SelectionFixture()
        {
            Context = new RuntimeTestContext();
            CategoryA = new CategoryDefinition { Name = "Category A", SortOrder = 0 };
            CategoryB = new CategoryDefinition { Name = "Category B", SortOrder = 1 };
            ProfileA = new ProfileDefinition
            {
                CategoryId = CategoryA.Id,
                Name = "Profile A",
                SortOrder = 0,
                Actions =
                [
                    RuntimeTestContext.Action(ActionTypeIds.ProcessConfigure, new JsonObject
                    {
                        [ActionParameterNames.ProcessOperation] = ProcessOperationIds.Configure
                    }),
                    RuntimeTestContext.Action(ActionTypeIds.Delay, new JsonObject { [ActionParameterNames.DelaySeconds] = 2 }),
                    RuntimeTestContext.Action(ActionTypeIds.Delay, new JsonObject { [ActionParameterNames.DelaySeconds] = 3 })
                ]
            };
            ProfileB = new ProfileDefinition
            {
                CategoryId = CategoryA.Id,
                Name = "Profile B",
                SortOrder = 1,
                Actions =
                [RuntimeTestContext.Action(ActionTypeIds.Delay,
                    new JsonObject { [ActionParameterNames.DelaySeconds] = 4 })]
            };
            ProfileC = new ProfileDefinition
            {
                CategoryId = CategoryB.Id,
                Name = "Profile C",
                SortOrder = 0,
                Actions =
                [RuntimeTestContext.Action(ActionTypeIds.Delay,
                    new JsonObject { [ActionParameterNames.DelaySeconds] = 5 })]
            };

            foreach (var profile in new[] { ProfileA, ProfileB, ProfileC })
            {
                for (var index = 0; index < profile.Actions.Count; index++)
                    profile.Actions[index].SortOrder = index;
            }

            Catalog = new SwitchBoardCatalog
            {
                Categories = [CategoryA, CategoryB],
                Profiles = [ProfileA, ProfileB, ProfileC]
            };
            Dialog = new TestDialogService();
            Main = new MainWindowViewModel(
                new TestCatalogService(),
                Dialog,
                Catalog,
                new TestThemeManager(),
                new TestLocalizationService(),
                new TestSettingsRepository(),
                new UserSettings { ThemeId = ThemeIds.Graphite, LanguageId = "en" },
                Context.Runner,
                new ProfileRestoreRunner(Context.Registry, Context.SessionRepository),
                Context.SessionRepository,
                new TestCompletionBehavior(),
                new TestDisplayManager(new("", "", "", 1, 1, 1, 32, 0, 0, 0, 0)),
                new TestCustomThemeEditorService());
        }

        public RuntimeTestContext Context { get; }
        public CategoryDefinition CategoryA { get; }
        public CategoryDefinition CategoryB { get; }
        public ProfileDefinition ProfileA { get; }
        public ProfileDefinition ProfileB { get; }
        public ProfileDefinition ProfileC { get; }
        public SwitchBoardCatalog Catalog { get; }
        public TestDialogService Dialog { get; }
        public MainWindowViewModel Main { get; }

        public void Dispose() => Context.Dispose();
    }
}
