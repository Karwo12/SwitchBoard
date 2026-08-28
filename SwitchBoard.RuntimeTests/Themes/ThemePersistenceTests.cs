using SwitchBoard.Controls;
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
        theme.Colors.Background = "#FF102030";
        theme.Colors.Panel = "#FF203040";
        theme.Colors.Card = "#FF304050";
        theme.Colors.Elevated = "#FF405060";
        theme.Colors.Border = "#FF506070";
        theme.Colors.PrimaryText = "#FF607080";
        theme.Colors.SecondaryText = "#FF708090";
        theme.Colors.Accent = "#FF123456";
        theme.Colors.Hover = "#FF234567";
        theme.Colors.Selection = "#FF345678";
        theme.Colors.PrimaryButtonBackground = "#FF456789";
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
        Assert.Equal("#FF102030", saved.Colors.Background);
        Assert.Equal("#FF203040", saved.Colors.Panel);
        Assert.Equal("#FF304050", saved.Colors.Card);
        Assert.Equal("#FF405060", saved.Colors.Elevated);
        Assert.Equal("#FF506070", saved.Colors.Border);
        Assert.Equal("#FF607080", saved.Colors.PrimaryText);
        Assert.Equal("#FF708090", saved.Colors.SecondaryText);
        Assert.Equal("#FF123456", saved.Colors.Accent);
        Assert.Equal("#FF234567", saved.Colors.Hover);
        Assert.Equal("#FF345678", saved.Colors.Selection);
        Assert.Equal("#FF456789", saved.Colors.PrimaryButtonBackground);
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
    public void ThemeExchange_GifImportAndReplacementReleaseAllAssetHandles()
    {
        using var context = new RuntimeTestContext();
        var paths = new AppDataPaths(Path.Combine(context.Root, "theme-exchange-appdata"));
        Directory.CreateDirectory(paths.CustomThemeDirectory);
        TestHelpers.CreateTestImages(paths.CustomThemeDirectory);

        var exchange = new ThemeExchangeService(paths);
        var package = Path.Combine(context.Root, "animated.sbtheme");
        exchange.Export(new CustomThemeDefinition
        {
            Name = "Animated theme",
            Colors = new CustomThemeSettings { BackgroundAssetFileName = "test.gif" }
        }, package);

        RunOnSta(() =>
        {
            foreach (var staticAsset in new[] { "test.jpg", "test.png", "test.bmp" })
            {
                var staticFrames = ThemeImageLoader.Load(Path.Combine(paths.CustomThemeDirectory, staticAsset));
                Assert.Single(staticFrames);
            }

            var first = exchange.Import(package, []);
            var firstAsset = Path.Combine(paths.CustomThemeDirectory, first.Colors.BackgroundAssetFileName!);
            var firstFrames = ThemeImageLoader.Load(firstAsset);
            Assert.Equal(2, firstFrames.Count);

            // Import the same package immediately, then remove the replaced theme
            // while its decoded frames are still strongly referenced.
            var second = exchange.Import(package, [first]);
            var secondAsset = Path.Combine(paths.CustomThemeDirectory, second.Colors.BackgroundAssetFileName!);
            var secondFrames = ThemeImageLoader.Load(secondAsset);
            Assert.Equal(2, secondFrames.Count);
            exchange.DeleteOwnedAssets(first.Id);
            Assert.False(Directory.Exists(Path.Combine(paths.CustomThemeDirectory, first.Id)));

            // A third import proves that the package can be reused after the immediate
            // replacement and that no stale .import-* directory was left behind.
            var third = exchange.Import(package, [second]);
            var thirdAsset = Path.Combine(paths.CustomThemeDirectory, third.Colors.BackgroundAssetFileName!);
            Assert.True(File.Exists(thirdAsset));

            exchange.DeleteOwnedAssets(second.Id);
            exchange.DeleteOwnedAssets(third.Id);
            Assert.Equal(2, firstFrames.Count);
            Assert.Equal(2, secondFrames.Count);
        });

        AssertNoImportDirectories(paths.CustomThemeDirectory);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ThemeExchange_InvalidAssetCleansTemporaryImportDirectory()
    {
        using var context = new RuntimeTestContext();
        var paths = new AppDataPaths(Path.Combine(context.Root, "invalid-theme-exchange-appdata"));
        Directory.CreateDirectory(paths.CustomThemeDirectory);
        var invalidAsset = Path.Combine(paths.CustomThemeDirectory, "invalid.gif");
        File.WriteAllText(invalidAsset, "not a gif");

        var exchange = new ThemeExchangeService(paths);
        var package = Path.Combine(context.Root, "invalid.sbtheme");
        exchange.Export(new CustomThemeDefinition
        {
            Name = "Invalid theme",
            Colors = new CustomThemeSettings { BackgroundAssetFileName = "invalid.gif" }
        }, package);

        Assert.ThrowsAny<Exception>(() => exchange.Import(package, []));
        AssertNoImportDirectories(paths.CustomThemeDirectory);
        Assert.Empty(Directory.GetDirectories(paths.CustomThemeDirectory, "custom-*", SearchOption.TopDirectoryOnly));
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
            VerifyLiveResourceInheritance(manager);
            VerifyThemePickerWindows(app, manager);
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
            Assert.Same(app.TryFindResource("SurfaceBrush"), app.TryFindResource("CategoriesSurfaceBrush"));
            Assert.Same(app.TryFindResource("SurfaceBrush"), app.TryFindResource("ProfilesSurfaceBrush"));
            Assert.Same(app.TryFindResource("SurfaceBrush"), app.TryFindResource("ProfileEditorSurfaceBrush"));
            Assert.Same(app.TryFindResource("SurfaceBrush"), app.TryFindResource("ActivitySurfaceBrush"));
            Assert.True(TestHelpers.SemanticContrastIsAccessible(app), $"Resource contrast for {theme.Id}");
            Assert.True(TestHelpers.RenderedSemanticControlsAreAccessible(app), $"Rendered controls for {theme.Id}");
            var editable = manager.CreateEditableCopy(theme.Id);
            Assert.All(new[] { editable.Background, editable.Panel, editable.Card, editable.Elevated,
                editable.Border, editable.PrimaryText, editable.SecondaryText, editable.Accent, editable.Hover,
                editable.Selection, editable.PrimaryButtonBackground },
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
        settings.Panel = "#FF304050";
        settings.Card = "#FF405060";
        settings.Elevated = "#FF506070";
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
        Assert.Equal(Color.FromArgb(102, 48, 64, 80), ((SolidColorBrush)app.TryFindResource("ActivitySurfaceBrush")!).Color);
        Assert.Equal(Color.FromArgb(64, 48, 64, 80), ((SolidColorBrush)app.TryFindResource("CategoriesSurfaceBrush")!).Color);
        Assert.Equal(Color.FromArgb(255, 80, 96, 112), ((SolidColorBrush)app.TryFindResource("PopupBackgroundBrush")!).Color);
        Assert.Equal(Color.FromArgb(178, 80, 96, 112), ((SolidColorBrush)app.TryFindResource("SecondaryButtonBackground")!).Color);
        Assert.Equal(170, ((SolidColorBrush)app.TryFindResource("BorderBrush")!).Color.A);
        Assert.Equal(0.37, (double)app.TryFindResource("CustomBackgroundOpacity")!, 3);

        // Older files may still contain the retired per-control overrides. They must
        // remain loadable, while the resolved UI keeps the shared surface contract.
        var legacy = CustomThemeSettings.CreateDefault();
        legacy.Panel = "#FF223344";
        legacy.Elevated = "#FF556677";
        legacy.SecondaryButtonBackground = "#FFABCDEF";
        legacy.MenuBackground = "#FF135724";
        legacy.MenuHoverBackground = "#FF246813";
        legacy.IconForeground = "#FFFF00FF";
        manager.ApplyTheme("legacy-custom", legacy);
        Assert.Equal(Color.FromRgb(34, 51, 68), ((SolidColorBrush)app.TryFindResource("ActivitySurfaceBrush")!).Color);
        Assert.Equal(Color.FromRgb(85, 102, 119), ((SolidColorBrush)app.TryFindResource("PopupBackgroundBrush")!).Color);
        Assert.Equal(Color.FromRgb(85, 102, 119), ((SolidColorBrush)app.TryFindResource("SecondaryButtonBackground")!).Color);

        using var stream = File.OpenRead(Path.Combine(paths.CustomThemeDirectory, "test.gif"));
        var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        Assert.Equal(2, decoder.Frames.Count);
    }

    private static void VerifyEditor(System.Windows.Application app, ThemeManager manager, AppDataPaths paths)
    {
        var extreme = CustomThemeSettings.CreateDefault();
        extreme.Background = "#FF102030";
        extreme.Panel = "#FF203040";
        extreme.Card = "#FF304050";
        extreme.PrimaryText = "#FFF2F4F8";
        extreme.SecondaryText = "#FFAAB2C0";
        manager.ApplyTheme("editor-base", extreme);
        var liveApplyCount = 0;
        var editor = new SwitchBoard.Views.CustomThemeWindow(
            new CustomThemeEditRequest(CustomThemeEditMode.Add, "Extreme", extreme, [], "draft-live",
                settings => { liveApplyCount++; manager.ApplyTemporary("draft-live", settings); }),
            paths, new TestLocalizationService());
        Assert.False(editor.Resources.Contains("EditorBackgroundBrush"));
        AssertBrushColor(editor.Background, Color.FromRgb(16, 32, 48));
        AssertBrushColor(editor.Foreground, Color.FromRgb(242, 244, 248));
        editor.ViewModel.Colors.First(item => item.Key == "primaryText").Color = "#FFE0E8F0";
        editor.ViewModel.Colors.First(item => item.Key == "background").Color = "#FF080E14";
        AssertBrushColor(editor.Background, Color.FromRgb(8, 14, 20));
        AssertBrushColor(editor.Foreground, Color.FromRgb(224, 232, 240));
        Assert.True(liveApplyCount >= 2);
        Assert.Equal("draft-live", manager.CurrentThemeId);
        Assert.Equal(Color.FromRgb(8, 14, 20), ((SolidColorBrush)app.TryFindResource("BackgroundBrush")!).Color);

        editor.ViewModel.SurfaceOpacityPercent = 68;
        editor.ViewModel.CategoriesPanelOpacityPercent = 31;
        editor.ViewModel.ActivityPanelOpacityPercent = 44;
        Assert.Equal(0.68, editor.ViewModel.Settings.SurfaceOpacity, 3);
        Assert.Equal(0.31, editor.ViewModel.Settings.CategoriesPanelOpacity, 3);
        Assert.Equal(0.68, editor.ViewModel.Settings.ProfilesPanelOpacity, 3);
        Assert.Equal(0.44, editor.ViewModel.Settings.ActivityPanelOpacity, 3);
        Assert.Equal(79, ((SolidColorBrush)app.TryFindResource("CategoriesSurfaceBrush")!).Color.A);
        Assert.Equal(173, ((SolidColorBrush)app.TryFindResource("ProfilesSurfaceBrush")!).Color.A);
        Assert.Equal(Color.FromArgb(112, 32, 48, 64), ((SolidColorBrush)app.TryFindResource("ActivitySurfaceBrush")!).Color);
        var name = editor.ViewModel.Name;
        editor.ViewModel.Colors.First(item => item.Key == "accent").Color = "#FFFF0000";
        editor.ViewModel.Reset();
        Assert.Equal(name, editor.ViewModel.Name);
        Assert.Equal(extreme.Accent, editor.ViewModel.Settings.Accent);
        editor.Close();
    }

    private static void VerifyLiveResourceInheritance(ThemeManager manager)
    {
        var category = new System.Windows.Controls.ContentControl();
        var profiles = new System.Windows.Controls.ContentControl();
        var editor = new System.Windows.Controls.ContentControl();
        var activity = new System.Windows.Controls.ContentControl();
        var text = new System.Windows.Controls.TextBlock();
        category.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "CategoriesSurfaceBrush");
        profiles.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "ProfilesSurfaceBrush");
        editor.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "ProfileEditorSurfaceBrush");
        activity.SetResourceReference(System.Windows.Controls.Control.BackgroundProperty, "ActivitySurfaceBrush");
        text.SetResourceReference(System.Windows.Documents.TextElement.ForegroundProperty, "TextPrimaryBrush");

        var host = new System.Windows.Window
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            Content = new System.Windows.Controls.StackPanel
            {
                Children = { category, profiles, editor, activity, text }
            }
        };
        host.Show();
        host.UpdateLayout();

        var draft = CustomThemeSettings.CreateDefault();
        draft.Panel = "#FF7A135C";
        draft.PrimaryText = "#FFFDEAF6";
        draft.CategoriesPanelOpacity = 0.25;
        draft.ProfilesPanelOpacity = 0.50;
        draft.ProfileEditorPanelOpacity = 0.75;
        draft.ActivityPanelOpacity = 0.40;
        manager.ApplyTemporary("live-resources", draft);

        AssertBrushColor(category.Background, Color.FromArgb(64, 122, 19, 92));
        AssertBrushColor(profiles.Background, Color.FromArgb(128, 122, 19, 92));
        AssertBrushColor(editor.Background, Color.FromArgb(191, 122, 19, 92));
        AssertBrushColor(activity.Background, Color.FromArgb(102, 122, 19, 92));
        AssertBrushColor(text.Foreground, Color.FromRgb(253, 234, 246));
        host.Close();
    }

    private static void VerifyThemePickerWindows(System.Windows.Application app, ThemeManager manager)
    {
        var events = new List<Color>();
        var picker = new SwitchBoard.Views.ThemeColorPickerWindow(Colors.White, new TestLocalizationService(),
            color => events.Add(color));
        var nameWindow = new SwitchBoard.Views.ThemeNameWindow("Theme", [], new TestLocalizationService());
        picker.Show();
        nameWindow.Show();
        picker.UpdateLayout();
        nameWindow.UpdateLayout();
        AssertBrushColor(picker.Background, ((SolidColorBrush)app.TryFindResource("BackgroundBrush")!).Color);
        AssertBrushColor(picker.Foreground, ((SolidColorBrush)app.TryFindResource("TextPrimaryBrush")!).Color);
        AssertBrushColor(nameWindow.Background, ((SolidColorBrush)app.TryFindResource("BackgroundBrush")!).Color);
        AssertBrushColor(nameWindow.Foreground, ((SolidColorBrush)app.TryFindResource("TextPrimaryBrush")!).Color);
        ((System.Windows.Controls.Slider)picker.FindName("Red")).Value = 16;
        Assert.Equal(16, events.LastOrDefault().R);

        var updated = CustomThemeSettings.CreateDefault();
        updated.Background = "#FF1E1234";
        updated.PrimaryText = "#FFF7EFFF";
        manager.ApplyTemporary("picker-live", updated);
        picker.UpdateLayout();
        nameWindow.UpdateLayout();
        AssertBrushColor(picker.Background, Color.FromRgb(30, 18, 52));
        AssertBrushColor(picker.Foreground, Color.FromRgb(247, 239, 255));
        AssertBrushColor(nameWindow.Background, Color.FromRgb(30, 18, 52));
        AssertBrushColor(nameWindow.Foreground, Color.FromRgb(247, 239, 255));
        ((System.Windows.Controls.Button)picker.FindName("CancelButton")).RaiseEvent(
            new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Assert.Equal(Colors.White, events.LastOrDefault());
        nameWindow.Close();
    }

    private static void AssertBrushColor(Brush? brush, Color expected) =>
        Assert.Equal(expected, Assert.IsType<SolidColorBrush>(brush).Color);

    private static void AssertNoImportDirectories(string directory) =>
        Assert.Empty(Directory.GetDirectories(directory, ".import-*", SearchOption.TopDirectoryOnly));

    private static void RunOnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception) { error = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new InvalidOperationException("STA theme exchange scenario failed.", error);
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
