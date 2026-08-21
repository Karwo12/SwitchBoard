using SwitchBoard.RuntimeTests.TestInfrastructure;

namespace SwitchBoard.RuntimeTests.Themes;

[Collection("Windows runtime")]
public sealed class ThemePersistenceTests : RuntimeTestBase
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task Settings_CustomThemeAndLegacyOpacity_RoundTrip()
    {
        using var context = new RuntimeTestContext();
        using var repository = new JsonSettingsRepository(new AppDataPaths(Path.Combine(context.Root, "settings-appdata")));
        var theme = new CustomThemeDefinition { Name = "First custom theme" };
        theme.Colors.Accent = "#FF123456";
        theme.Colors.SecondaryButtonBackground = "#FF223344";
        theme.Colors.MenuBackground = "#FF334455";
        theme.Colors.MenuHoverBackground = "#FF445566";
        theme.Colors.SurfaceOpacity = 0.72;
        theme.Colors.CategoriesPanelOpacity = 0.51;
        theme.Colors.ProfilesPanelOpacity = 0.63;
        theme.Colors.ProfileEditorPanelOpacity = 0.84;
        theme.Colors.ActivityPanelOpacity = 0.47;
        theme.Colors.BackgroundAssetFileName = "background-test.gif";
        await repository.SaveAsync(new UserSettings { ThemeId = theme.Id, LanguageId = "pl", CustomThemes = [theme] });
        var reloaded = await repository.LoadAsync();

        Assert.Equal(theme.Id, reloaded.ThemeId);
        var saved = Assert.Single(reloaded.CustomThemes);
        Assert.Equal("First custom theme", saved.Name);
        Assert.Equal("#FF123456", saved.Colors.Accent);
        Assert.Equal("#FF223344", saved.Colors.SecondaryButtonBackground);
        Assert.Equal("#FF334455", saved.Colors.MenuBackground);
        Assert.Equal("#FF445566", saved.Colors.MenuHoverBackground);
        Assert.Equal(0.72, saved.Colors.SurfaceOpacity, 3);
        Assert.Equal(0.51, saved.Colors.CategoriesPanelOpacity, 3);
        Assert.Equal(0.63, saved.Colors.ProfilesPanelOpacity, 3);
        Assert.Equal(0.84, saved.Colors.ProfileEditorPanelOpacity, 3);
        Assert.Equal(0.47, saved.Colors.ActivityPanelOpacity, 3);
        Assert.Equal("background-test.gif", saved.Colors.BackgroundAssetFileName);
        Assert.NotEqual(default, saved.CreatedAt);
        Assert.NotEqual(default, saved.UpdatedAt);

        var legacy = CustomThemeSettings.CreateDefault();
        legacy.Panel = "#80223344";
        legacy.MigrateSurfaceOpacityFromLegacyAlpha();
        Assert.Equal(128d / 255, legacy.SurfaceOpacity, 3);
        Assert.Equal(legacy.SurfaceOpacity, legacy.CategoriesPanelOpacity);
        Assert.Equal(legacy.SurfaceOpacity, legacy.ProfilesPanelOpacity);
        Assert.Equal(legacy.SurfaceOpacity, legacy.ProfileEditorPanelOpacity);
        Assert.Equal(legacy.SurfaceOpacity, legacy.ActivityPanelOpacity);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Logging_RotatingLoggerCreatesTheExpectedLocalFile()
    {
        using var context = new RuntimeTestContext();
        var paths = new AppDataPaths(Path.Combine(context.Root, "logging-appdata"));
        new RollingFileLogger(paths).Info("Regression", "technical log smoke test");

        Assert.True(File.Exists(Path.Combine(paths.LogsDirectory, "switchboard.log")));
    }

    // WPF exposes one Application per AppDomain. These related checks intentionally share
    // one STA/Application lifetime so they remain deterministic under xUnit.
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public void ThemeRuntime_AccessibilityAssetsAndEditorContractsRemainStable()
    {
        using var context = new RuntimeTestContext();
        RunThemeScenario(context, (app, manager, paths) =>
        {
            VerifyBuiltInThemes(app, manager);
            VerifyContrastMatrix(app, manager);
            VerifyAssetsAndOpacity(app, manager, paths);
            VerifyEditor(app, manager, paths);
            VerifyColorPicker();
        });
    }

    private static void VerifyBuiltInThemes(System.Windows.Application app, ThemeManager manager)
    {
        foreach (var theme in manager.AvailableThemes.Where(item => item.Id != ThemeIds.Custom))
        {
            manager.ApplyTheme(theme.Id);
            var required = new[] { "BackgroundBrush", "SurfaceBrush", "CardSurfaceBrush", "ElevatedSurfaceBrush",
                "BorderBrush", "TextPrimaryBrush", "TextSecondaryBrush", "PrimaryButtonBackground",
                "PrimaryButtonForeground", "PrimaryButtonHoverForeground", "PrimaryButtonPressedForeground",
                "PrimaryButtonDisabledForeground", "SecondaryButtonBackground", "SecondaryButtonForeground",
                "SecondaryButtonDisabledBackground", "SecondaryButtonDisabledForeground", "MenuBackground",
                "MenuForeground", "MenuHoverBackground", "MenuHoverForeground", "MenuDisabledForeground",
                "SelectionForeground", "HoverForeground", "DisabledInputBackground", "DisabledInputForeground",
                "CategoriesSurfaceBrush", "ProfilesSurfaceBrush", "ProfileEditorSurfaceBrush", "ActivitySurfaceBrush",
                "IconPrimary", "IconAccent", "IconMuted" };
            Assert.All(required, key => Assert.IsAssignableFrom<Brush>(app.TryFindResource(key)));
            Assert.True(TestHelpers.SemanticContrastIsAccessible(app), $"Resource contrast for {theme.Id}");
            Assert.True(TestHelpers.RenderedSemanticControlsAreAccessible(app), $"Rendered controls for {theme.Id}");
            var editable = manager.CreateEditableCopy(theme.Id);
            Assert.All(new[] { editable.Background, editable.Panel, editable.Card, editable.Elevated,
                editable.Border, editable.PrimaryText, editable.SecondaryText, editable.Accent, editable.Hover,
                editable.Selection, editable.PrimaryButtonBackground, editable.IconForeground },
                value => Assert.True(CustomThemeColorItemViewModel.TryColor(value, out var parsedColor)));
        }
    }

    private static void VerifyContrastMatrix(System.Windows.Application app, ThemeManager manager)
    {
        foreach (var color in new[] { "#FFFFFFFF", "#FFE8E8EA", "#FF000000", "#FF24262B",
                                       "#FF1473E6", "#FFFFE600", "#FFFFFF66", "#FF07101F" })
        {
            var custom = CustomThemeSettings.CreateDefault();
            custom.Background = color;
            custom.PrimaryText = color;
            custom.SecondaryText = color;
            custom.Elevated = color;
            custom.PrimaryButtonBackground = color;
            custom.SecondaryButtonBackground = color;
            custom.MenuBackground = color;
            custom.MenuHoverBackground = color;
            custom.Hover = color;
            custom.Selection = color;
            custom.Accent = color;
            manager.ApplyTheme($"contrast-{color[3..]}", custom);
            var background = ((SolidColorBrush)app.TryFindResource("AccentBrush")!).Color;
            var foreground = ((SolidColorBrush)app.TryFindResource("AccentForegroundBrush")!).Color;
            Assert.True(TestHelpers.SemanticContrastIsAccessible(app), $"Semantic contrast matrix: {color}");
            Assert.True(TestHelpers.ContrastRatio(background, foreground) >= 4.5, $"Accent contrast: {color}");
        }
    }

    private static void VerifyAssetsAndOpacity(System.Windows.Application app, ThemeManager manager, AppDataPaths paths)
    {
        foreach (var asset in new[] { "test.jpg", "test.png", "test.bmp", "test.gif" })
        {
            var custom = CustomThemeSettings.CreateDefault();
            custom.BackgroundAssetFileName = asset;
            manager.ApplyTheme(ThemeIds.Custom, custom);
            Assert.Equal(Path.Combine(paths.CustomThemeDirectory, asset),
                app.TryFindResource("CustomBackgroundPath") as string, StringComparer.OrdinalIgnoreCase);
        }

        var settings = CustomThemeSettings.CreateDefault();
        settings.Background = "#FF102030";
        settings.Border = "#AAABCDEF";
        settings.SurfaceOpacity = 0.70;
        settings.CategoriesPanelOpacity = 0.25;
        settings.ProfilesPanelOpacity = 0.50;
        settings.ProfileEditorPanelOpacity = 0.85;
        settings.ActivityPanelOpacity = 0.40;
        settings.BackgroundOpacity = 0.37;
        manager.ApplyTheme("surface-opacity", settings);
        Assert.Equal(Color.FromArgb(255, 16, 32, 48), ((SolidColorBrush)app.TryFindResource("BackgroundBrush")!).Color);
        Assert.Equal(178, ((SolidColorBrush)app.TryFindResource("SurfaceBrush")!).Color.A);
        Assert.Equal(178, ((SolidColorBrush)app.TryFindResource("CardSurfaceBrush")!).Color.A);
        Assert.Equal(178, ((SolidColorBrush)app.TryFindResource("ElevatedSurfaceBrush")!).Color.A);
        Assert.Equal(64, ((SolidColorBrush)app.TryFindResource("CategoriesSurfaceBrush")!).Color.A);
        Assert.Equal(128, ((SolidColorBrush)app.TryFindResource("ProfilesSurfaceBrush")!).Color.A);
        Assert.Equal(217, ((SolidColorBrush)app.TryFindResource("ProfileEditorSurfaceBrush")!).Color.A);
        Assert.Equal(102, ((SolidColorBrush)app.TryFindResource("ActivitySurfaceBrush")!).Color.A);
        Assert.Equal(170, ((SolidColorBrush)app.TryFindResource("BorderBrush")!).Color.A);
        Assert.Equal(0.37, (double)app.TryFindResource("CustomBackgroundOpacity")!, 3);

        using var stream = File.OpenRead(Path.Combine(paths.CustomThemeDirectory, "test.gif"));
        var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Assert.Equal(2, decoder.Frames.Count);
    }

    private static void VerifyEditor(System.Windows.Application app, ThemeManager manager, AppDataPaths paths)
    {
        var extreme = CustomThemeSettings.CreateDefault();
        extreme.Background = "#FFFFFFFF";
        extreme.Panel = "#FFFFFFFF";
        extreme.Card = "#FFFFFFFF";
        extreme.PrimaryText = "#FFFFFFFF";
        extreme.SecondaryText = "#FF000000";
        var liveApplyCount = 0;
        var editor = new SwitchBoard.Views.CustomThemeWindow(
            new CustomThemeEditRequest(CustomThemeEditMode.Add, "Extreme", extreme, [], "draft-live",
                settings => { liveApplyCount++; manager.ApplyTemporary("draft-live", settings); }),
            paths, new TestLocalizationService());
        var editorBackground = ((SolidColorBrush)editor.Resources["EditorBackgroundBrush"]).Color;
        var editorText = ((SolidColorBrush)editor.Resources["EditorTextBrush"]).Color;
        var editorInput = ((SolidColorBrush)editor.Resources["EditorInputBrush"]).Color;
        Assert.True(TestHelpers.ContrastRatio(editorBackground, editorText) >= 12);
        Assert.True(TestHelpers.ContrastRatio(editorInput, editorText) >= 10);
        editor.ViewModel.Colors.First(item => item.Key == "primaryText").Color = "#FF000000";
        editor.ViewModel.Colors.First(item => item.Key == "background").Color = "#FF000000";
        Assert.Equal(editorText, ((SolidColorBrush)editor.Resources["EditorTextBrush"]).Color);
        Assert.True(liveApplyCount >= 2);
        Assert.Equal("draft-live", manager.CurrentThemeId);
        Assert.Equal(Colors.Black, ((SolidColorBrush)app.TryFindResource("BackgroundBrush")!).Color);

        editor.ViewModel.SurfaceOpacityPercent = 68;
        editor.ViewModel.CategoriesPanelOpacityPercent = 31;
        editor.ViewModel.ActivityPanelOpacityPercent = 44;
        Assert.Equal(0.68, editor.ViewModel.Settings.SurfaceOpacity, 3);
        Assert.Equal(0.31, editor.ViewModel.Settings.CategoriesPanelOpacity, 3);
        Assert.Equal(0.68, editor.ViewModel.Settings.ProfilesPanelOpacity, 3);
        Assert.Equal(0.44, editor.ViewModel.Settings.ActivityPanelOpacity, 3);
        Assert.Equal(79, ((SolidColorBrush)app.TryFindResource("CategoriesSurfaceBrush")!).Color.A);
        Assert.Equal(173, ((SolidColorBrush)app.TryFindResource("ProfilesSurfaceBrush")!).Color.A);
        Assert.Equal(112, ((SolidColorBrush)app.TryFindResource("ActivitySurfaceBrush")!).Color.A);
        var name = editor.ViewModel.Name;
        editor.ViewModel.Colors.First(item => item.Key == "accent").Color = "#FFFF0000";
        editor.ViewModel.Reset();
        Assert.Equal(name, editor.ViewModel.Name);
        Assert.Equal(extreme.Accent, editor.ViewModel.Settings.Accent);
        editor.Close();
    }

    private static void VerifyColorPicker()
    {
        var events = new List<Color>();
        var picker = new SwitchBoard.Views.ThemeColorPickerWindow(Colors.White, new TestLocalizationService(),
            color => events.Add(color));
        Assert.True(TestHelpers.ContrastRatio(((SolidColorBrush)picker.Background).Color,
            ((SolidColorBrush)picker.Foreground).Color) >= 12);
        ((System.Windows.Controls.Slider)picker.FindName("Red")).Value = 16;
        Assert.Equal(16, events.LastOrDefault().R);
        ((System.Windows.Controls.Button)picker.FindName("CancelButton")).RaiseEvent(
            new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Assert.Equal(Colors.White, events.LastOrDefault());
    }

    private static void RunThemeScenario(RuntimeTestContext context,
        System.Action<System.Windows.Application, ThemeManager, AppDataPaths> scenario)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            var app = new SwitchBoard.App();
            try
            {
                // The test host does not execute the production startup path that normally
                // leaves BaseStyles in the application resource tree.
                app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
                {
                    Source = new Uri("/SwitchBoard;component/Themes/BaseStyles.xaml", UriKind.Relative)
                });
                var paths = new AppDataPaths(Path.Combine(context.Root, "theme-appdata"));
                Directory.CreateDirectory(paths.CustomThemeDirectory);
                TestHelpers.CreateTestImages(paths.CustomThemeDirectory);
                var manager = new ThemeManager(paths);
                scenario(app, manager, paths);
            }
            catch (Exception exception) { error = exception; }
            finally { app.Shutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new InvalidOperationException("STA theme scenario failed.", error);
    }
}
