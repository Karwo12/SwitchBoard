using SwitchBoard.Data;
using SwitchBoard.Models.Categories;
using SwitchBoard.RuntimeTests.TestInfrastructure;
using SwitchBoard.Services;
using SwitchBoard.Services.Execution;
using SwitchBoard.Themes;

namespace SwitchBoard.RuntimeTests.ViewModels;

public sealed class ThemeOrderTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task ThemeReorder_UpAndDown_IsSavedAndReloadedWithoutChangingActiveTheme()
    {
        using var scenario = CreateScenario();
        var active = scenario.Main.SelectedThemeOption!;
        var first = scenario.Main.ThemeOptions[0];
        var last = scenario.Main.ThemeOptions[^1];

        await scenario.Main.ApplyReorderAsync(new(ReorderItemKind.Theme, first, last,
            scenario.Main.ThemeOptions.Count));
        Assert.Equal(first, scenario.Main.ThemeOptions[^1]);
        Assert.Same(active, scenario.Main.SelectedThemeOption);
        Assert.Equal(active.Id, scenario.Settings.ThemeId);

        await scenario.Main.ApplyReorderAsync(new(ReorderItemKind.Theme, first, scenario.Main.ThemeOptions[0], 0));
        Assert.Equal(first, scenario.Main.ThemeOptions[0]);
        Assert.Same(active, scenario.Main.SelectedThemeOption);
        Assert.Equal(active.Id, scenario.Settings.ThemeId);
        Assert.Equal(scenario.Main.ThemeOptions.Select(item => item.Id), scenario.Settings.ThemeOrder);
        Assert.Equal(scenario.Settings.ThemeOrder, scenario.SettingsRepository.Saved.ThemeOrder);

        using var reloaded = CreateMain(scenario.SettingsRepository.Saved,
            scenario.ThemeManager.AvailableThemes);
        Assert.Equal(scenario.Main.ThemeOptions.Select(item => item.Id), reloaded.Main.ThemeOptions.Select(item => item.Id));
        Assert.Equal(active.Id, reloaded.Main.SelectedThemeOption?.Id);
        Assert.Equal(active.Id, reloaded.Settings.ThemeId);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ThemeOrder_MissingMetadataUsesHistoricalDefaultAndAppendsNewThemes()
    {
        using var scenario = CreateScenario(themeOrder: []);
        var defaultIds = scenario.ThemeManager.AvailableThemes.Select(theme => theme.Id)
            .Concat(scenario.Settings.CustomThemes.Select(theme => theme.Id));

        Assert.Equal(defaultIds, scenario.Main.ThemeOptions.Select(option => option.Id));
        Assert.Equal(defaultIds, scenario.Settings.ThemeOrder);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ThemeAddAndDelete_KeepExistingOrderAndRemoveDeletedId()
    {
        using var scenario = CreateScenario();
        var before = scenario.Main.ThemeOptions.Select(item => item.Id).ToList();
        var colors = CustomThemeSettings.CreateDefault();
        scenario.ThemeEditor.Results.Enqueue(new("Added", colors));
        scenario.Main.AddThemeCommand.Execute(null);
        await TestHelpers.WaitUntilAsync(() => scenario.Settings.CustomThemes.Count == 2);

        var added = scenario.Main.ThemeOptions.Single(item => item.DisplayName == "Added");
        Assert.Equal(before, scenario.Main.ThemeOptions.Take(before.Count).Select(item => item.Id));
        Assert.Equal(added.Id, scenario.Main.ThemeOptions[^1].Id);
        Assert.Equal(scenario.Main.ThemeOptions.Select(item => item.Id), scenario.SettingsRepository.Saved.ThemeOrder);

        var existingCustom = scenario.Main.ThemeOptions.First(item => item.IsCustom && item.Id != added.Id);
        scenario.Main.DeleteThemeCommand.Execute(existingCustom.Id);
        await TestHelpers.WaitUntilAsync(() => !scenario.Main.ThemeOptions.Any(item => item.Id == existingCustom.Id));

        Assert.DoesNotContain(existingCustom.Id, scenario.Settings.ThemeOrder);
        Assert.Equal(scenario.Main.ThemeOptions.Select(item => item.Id), scenario.Settings.ThemeOrder);
        Assert.Equal(scenario.Settings.ThemeOrder, scenario.SettingsRepository.Saved.ThemeOrder);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ThemeOrder_NormalizesLegacyDuplicateIdentityAndOrderEntries()
    {
        var duplicateId = CustomThemeDefinition.CreateId();
        var first = new CustomThemeDefinition { Id = duplicateId, Name = "First" };
        var second = new CustomThemeDefinition { Id = duplicateId, Name = "Second" };
        using var scenario = CreateScenario([first, second], [ThemeIds.Dark, duplicateId, duplicateId]);

        Assert.Equal(scenario.Main.ThemeOptions.Count,
            scenario.Main.ThemeOptions.Select(option => option.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(scenario.Main.ThemeOptions.Select(option => option.Id), scenario.Settings.ThemeOrder);
        Assert.Equal(scenario.Settings.ThemeOrder.Count,
            scenario.Settings.ThemeOrder.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        scenario.ThemeEditor.Results.Enqueue(new("Copy", CustomThemeSettings.CreateDefault()));
        scenario.Main.DuplicateThemeCommand.Execute(first.Id);
        await TestHelpers.WaitUntilAsync(() => scenario.Settings.CustomThemes.Count >= 3);
        Assert.Equal(scenario.Main.ThemeOptions.Count,
            scenario.Main.ThemeOptions.Select(option => option.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(scenario.Main.ThemeOptions.Select(option => option.Id), scenario.Settings.ThemeOrder);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ImportedThemeGetsFreshIdentityAndIsAppendedAfterPersistedOrder()
    {
        using var context = new RuntimeTestContext();
        var paths = new AppDataPaths(Path.Combine(context.Root, "theme-import-appdata"));
        var exchange = new ThemeExchangeService(paths);
        var package = Path.Combine(context.Root, "imported.sbtheme");
        var exported = new CustomThemeDefinition { Name = "Imported" };
        exchange.Export(exported, package);

        var imported = exchange.Import(package, [exported]);
        Assert.NotEqual(exported.Id, imported.Id);

        var customThemes = new[] { exported, imported };
        using var scenario = CreateScenario(customThemes,
            [ThemeIds.Dark, ThemeIds.Graphite, ThemeIds.Light, exported.Id]);

        Assert.Equal([ThemeIds.Dark, ThemeIds.Graphite, ThemeIds.Light, exported.Id, imported.Id],
            scenario.Main.ThemeOptions.Select(option => option.Id));
        Assert.Equal(scenario.Main.ThemeOptions.Select(option => option.Id), scenario.Settings.ThemeOrder);
        Assert.Equal(scenario.Settings.ThemeOrder.Count,
            scenario.Settings.ThemeOrder.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    private static ThemeScenario CreateScenario(IReadOnlyList<CustomThemeDefinition>? customThemes = null,
        IReadOnlyList<string>? themeOrder = null)
    {
        customThemes ??= [new CustomThemeDefinition { Name = "Snow" }];
        var themes = new[]
        {
            new ThemeDefinition(ThemeIds.Graphite, "Graphite", new Uri("Themes/GraphiteTheme.xaml", UriKind.Relative)),
            new ThemeDefinition(ThemeIds.Dark, "Dark", new Uri("Themes/DarkTheme.xaml", UriKind.Relative)),
            new ThemeDefinition(ThemeIds.Light, "Light", new Uri("Themes/LightTheme.xaml", UriKind.Relative))
        };
        var settings = new UserSettings
        {
            ThemeId = ThemeIds.Graphite,
            LanguageId = "en",
            CustomThemes = customThemes.Select(theme => theme.Clone()).ToList(),
            ThemeOrder = themeOrder?.ToList() ?? themes.Select(theme => theme.Id)
                .Concat(customThemes.Select(theme => theme.Id)).ToList()
        };
        return CreateMain(settings, themes);
    }

    private static ThemeScenario CreateMain(UserSettings settings, IReadOnlyList<ThemeDefinition> themes)
    {
        var context = new RuntimeTestContext();
        var manager = new TestThemeManager(themes);
        manager.ApplyTheme(settings.ThemeId, settings.CustomThemes.FirstOrDefault(theme => theme.Id == settings.ThemeId)?.Colors);
        var repository = new TestSettingsRepository();
        var editor = new TestCustomThemeEditorService();
        var main = new MainWindowViewModel(new TestCatalogService(), new TestDialogService(),
            new SwitchBoardCatalog { Categories = [new CategoryDefinition { Name = "Category" }] }, manager,
            new TestLocalizationService(), repository, settings, context.Runner,
            new ProfileRestoreRunner(context.Registry, context.SessionRepository), context.SessionRepository,
            new TestCompletionBehavior(), new TestDisplayManager(new("", "", "", 1, 1, 1, 32, 0, 0, 0, 0)), editor);
        return new ThemeScenario(context, manager, repository, editor, settings, main);
    }

    private sealed class ThemeScenario(RuntimeTestContext context, TestThemeManager themeManager,
        TestSettingsRepository settingsRepository, TestCustomThemeEditorService themeEditor,
        UserSettings settings, MainWindowViewModel main) : IDisposable
    {
        public RuntimeTestContext Context { get; } = context;
        public TestThemeManager ThemeManager { get; } = themeManager;
        public TestSettingsRepository SettingsRepository { get; } = settingsRepository;
        public TestCustomThemeEditorService ThemeEditor { get; } = themeEditor;
        public UserSettings Settings { get; } = settings;
        public MainWindowViewModel Main { get; } = main;

        public void Dispose()
        {
            Main.Dispose();
            Context.Dispose();
        }
    }
}
