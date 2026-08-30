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
        theme.Colors.HoverIntensity = 43.5;
        theme.Colors.CategoriesPanelOpacity = 0.51;
        theme.Colors.ProfilesPanelOpacity = 0.63;
        theme.Colors.ProfileEditorPanelOpacity = 0.84;
        theme.Colors.ActivityPanelOpacity = 0.47;
        theme.Colors.BackgroundAssetFileName = "background-test.gif";
        theme.Colors.ImageFit = BackgroundImageFits.Center;
        theme.Colors.GifAnimationDirection = GifAnimationDirections.PingPong;
        theme.Colors.GifAnimationSpeed = 1.5;
        theme.Colors.VideoPlaybackMode = GifAnimationDirections.Normal;
        theme.Colors.VideoPlaybackSpeed = 0.75;
        theme.Colors.VideoAudioEnabled = true;
        theme.Colors.ImageFlipHorizontal = true;
        theme.Colors.ImageFlipVertical = true;
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
        Assert.Equal(43.5, saved.Colors.HoverIntensity, 3);
        Assert.Equal(0.51, saved.Colors.CategoriesPanelOpacity, 3);
        Assert.Equal(0.63, saved.Colors.ProfilesPanelOpacity, 3);
        Assert.Equal(0.84, saved.Colors.ProfileEditorPanelOpacity, 3);
        Assert.Equal(0.47, saved.Colors.ActivityPanelOpacity, 3);
        Assert.Equal("background-test.gif", saved.Colors.BackgroundAssetFileName);
        Assert.Equal(BackgroundImageFits.Center, saved.Colors.ImageFit);
        Assert.Equal(GifAnimationDirections.PingPong, saved.Colors.GifAnimationDirection);
        Assert.Equal(1.5, saved.Colors.GifAnimationSpeed, 3);
        Assert.Equal(GifAnimationDirections.Normal, saved.Colors.VideoPlaybackMode);
        Assert.Equal(0.75, saved.Colors.VideoPlaybackSpeed, 3);
        Assert.True(saved.Colors.VideoAudioEnabled);
        Assert.True(saved.Colors.ImageFlipHorizontal);
        Assert.True(saved.Colors.ImageFlipVertical);
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

        var olderJson = "{\"Background\":\"#FF102030\",\"Hover\":\"#3372A7FF\"}";
        var olderTheme = System.Text.Json.JsonSerializer.Deserialize<CustomThemeSettings>(olderJson);
        Assert.NotNull(olderTheme);
        Assert.Equal(78, olderTheme!.HoverIntensity);
        Assert.Equal(BackgroundImageFits.Fill, olderTheme.ImageFit);
        Assert.Equal(GifAnimationDirections.Normal, olderTheme.GifAnimationDirection);
        Assert.Equal(1, olderTheme.GifAnimationSpeed);
        Assert.Equal(GifAnimationDirections.Normal, olderTheme.VideoPlaybackMode);
        Assert.Equal(1, olderTheme.VideoPlaybackSpeed);
        Assert.False(olderTheme.VideoAudioEnabled);
        Assert.False(olderTheme.ImageFlipHorizontal);
        Assert.False(olderTheme.ImageFlipVertical);
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
            Colors = new CustomThemeSettings
            {
                BackgroundAssetFileName = "test.gif",
                ImageFit = BackgroundImageFits.Center,
                GifAnimationDirection = GifAnimationDirections.PingPong,
                GifAnimationSpeed = 1.5,
                ImageFlipHorizontal = true,
                ImageFlipVertical = true
            }
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
            Assert.All(firstFrames, frame => Assert.True(frame.Source.IsFrozen));
            Assert.Equal(new byte[] { 0, 0, 255, 255 }, ReadFirstPixel(firstFrames[0].Source));
            Assert.Equal(new byte[] { 255, 0, 0, 255 }, ReadFirstPixel(firstFrames[1].Source));
            Assert.Equal(BackgroundImageFits.Center, first.Colors.ImageFit);
            Assert.Equal(GifAnimationDirections.PingPong, first.Colors.GifAnimationDirection);
            Assert.Equal(1.5, first.Colors.GifAnimationSpeed, 3);
            Assert.True(first.Colors.ImageFlipHorizontal);
            Assert.True(first.Colors.ImageFlipVertical);

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
    public void ThemeExchange_Mp4ImportExportPersistsPlaybackOptions()
    {
        using var context = new RuntimeTestContext();
        var paths = new AppDataPaths(Path.Combine(context.Root, "video-theme-exchange-appdata"));
        Directory.CreateDirectory(paths.CustomThemeDirectory);
        TestHelpers.CreateTestMp4(Path.Combine(paths.CustomThemeDirectory, "background.mp4"));
        var exchange = new ThemeExchangeService(paths);
        var package = Path.Combine(context.Root, "video.sbtheme");
        exchange.Export(new CustomThemeDefinition
        {
            Name = "Video theme",
            Colors = new CustomThemeSettings
            {
                BackgroundAssetFileName = "background.mp4",
                ImageFit = BackgroundImageFits.Center,
                VideoPlaybackMode = GifAnimationDirections.Normal,
                VideoPlaybackSpeed = 1.5,
                VideoAudioEnabled = true,
                ImageFlipHorizontal = true,
                ImageFlipVertical = true
            }
        }, package);

        var imported = exchange.Import(package, []);
        Assert.EndsWith("/background.mp4", imported.Colors.BackgroundAssetFileName, StringComparison.OrdinalIgnoreCase);
        Assert.True(File.Exists(Path.Combine(paths.CustomThemeDirectory, imported.Colors.BackgroundAssetFileName!)));
        Assert.Equal(BackgroundImageFits.Center, imported.Colors.ImageFit);
        Assert.Equal(GifAnimationDirections.Normal, imported.Colors.VideoPlaybackMode);
        Assert.Equal(1.5, imported.Colors.VideoPlaybackSpeed, 3);
        Assert.True(imported.Colors.VideoAudioEnabled);
        Assert.True(imported.Colors.ImageFlipHorizontal);
        Assert.True(imported.Colors.ImageFlipVertical);
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
            var backgroundHost = VerifyBackgroundLifecycle(manager, paths);
            VerifyEditor(app, manager, paths);
            VerifyLiveResourceInheritance(manager);
            VerifyThemePickerControl(app, manager);
            VerifyCardSurfaceControl(app, manager);
            backgroundHost.Close();
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
                "SelectionForeground", "HoverForeground", "InteractiveHoverBrush", "DisabledInputBackground", "DisabledInputForeground",
                "CategoriesSurfaceBrush", "ProfilesSurfaceBrush", "ProfileEditorSurfaceBrush", "ActivitySurfaceBrush",
                "IconPrimary", "IconAccent", "IconMuted" };
            Assert.All(required, key => Assert.IsAssignableFrom<Brush>(app.TryFindResource(key)));
            Assert.Same(app.TryFindResource("SurfaceBrush"), app.TryFindResource("CategoriesSurfaceBrush"));
            Assert.Same(app.TryFindResource("SurfaceBrush"), app.TryFindResource("ProfilesSurfaceBrush"));
            Assert.Same(app.TryFindResource("SurfaceBrush"), app.TryFindResource("ProfileEditorSurfaceBrush"));
            Assert.Same(app.TryFindResource("SurfaceBrush"), app.TryFindResource("ActivitySurfaceBrush"));
            Assert.True(TestHelpers.SemanticContrastIsAccessible(app), $"Resource contrast for {theme.Id}");
            Assert.True(TestHelpers.RenderedSemanticControlsAreAccessible(app), $"Rendered controls for {theme.Id}");
            Assert.Equal(GifAnimationDirections.Normal, app.TryFindResource("CustomBackgroundGifAnimationDirection"));
            Assert.Equal(1d, app.TryFindResource("CustomBackgroundGifAnimationSpeed"));
            Assert.Equal(GifAnimationDirections.Normal, app.TryFindResource("CustomBackgroundVideoPlaybackMode"));
            Assert.Equal(1d, app.TryFindResource("CustomBackgroundVideoPlaybackSpeed"));
            Assert.False((bool)app.TryFindResource("CustomBackgroundVideoAudioEnabled")!);
            Assert.False((bool)app.TryFindResource("CustomBackgroundFlipHorizontal")!);
            Assert.False((bool)app.TryFindResource("CustomBackgroundFlipVertical")!);
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
            var hover = Assert.IsType<SolidColorBrush>(app.TryFindResource("HoverBrush"));
            var interactiveHover = Assert.IsType<SolidColorBrush>(app.TryFindResource("InteractiveHoverBrush"));
            Assert.Equal((byte)Math.Round(hover.Color.A * 0.8 * custom.HoverIntensity / 100d), interactiveHover.Color.A);
            Assert.True(TestHelpers.SemanticContrastIsAccessible(app), $"Semantic contrast matrix: {color}");
            Assert.True(TestHelpers.ContrastRatio(background, foreground) >= 4.5, $"Accent contrast: {color}");
        }
    }

    private static void VerifyAssetsAndOpacity(System.Windows.Application app, ThemeManager manager, AppDataPaths paths)
    {
        foreach (var asset in new[] { "test.jpg", "test.png", "test.bmp", "test.gif", "test.mp4" })
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

    private static System.Windows.Window VerifyBackgroundLifecycle(ThemeManager manager, AppDataPaths paths)
    {
        var countersBefore = BackgroundMediaDiagnostics.Snapshot;
        var videoSettings = CustomThemeSettings.CreateDefault();
        videoSettings.BackgroundAssetFileName = "test.mp4";
        manager.ApplyTheme("background-lifecycle", videoSettings);

        var background = new ThemeBackground();
        var nativeSizeEvents = new List<BackgroundNativeSize>();
        background.NativeSizeChanged += (_, args) => nativeSizeEvents.Add(args.Size);
        background.SetResourceReference(ThemeBackground.SourcePathProperty, "CustomBackgroundPath");
        background.SetResourceReference(ThemeBackground.ImageOpacityProperty, "CustomBackgroundOpacity");
        background.SetResourceReference(ThemeBackground.ImageStretchProperty, "CustomBackgroundStretch");
        background.SetResourceReference(ThemeBackground.GifAnimationDirectionProperty,
            "CustomBackgroundGifAnimationDirection");
        background.SetResourceReference(ThemeBackground.GifAnimationSpeedProperty,
            "CustomBackgroundGifAnimationSpeed");
        background.SetResourceReference(ThemeBackground.VideoPlaybackSpeedProperty,
            "CustomBackgroundVideoPlaybackSpeed");
        background.SetResourceReference(ThemeBackground.VideoAudioEnabledProperty,
            "CustomBackgroundVideoAudioEnabled");
        background.SetResourceReference(ThemeBackground.ImageFlipHorizontalProperty,
            "CustomBackgroundFlipHorizontal");
        background.SetResourceReference(ThemeBackground.ImageFlipVerticalProperty,
            "CustomBackgroundFlipVertical");

        var host = new System.Windows.Window
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            Width = 640,
            Height = 360,
            Content = background
        };

        host.Show();
        DrainDispatcher();
        Assert.Equal(BackgroundAssetKind.Video, background.ActiveAssetKind);
        Assert.Equal(1, background.ActiveRendererCount);
        var video = Assert.IsType<VideoBackgroundPlayer>(background.ActiveVideoRenderer);
        Assert.True(video.IsPlaybackRequested);
        var opensAfterInitialLoad = BackgroundMediaDiagnostics.Snapshot.VideoOpenCount;
        Assert.Equal(countersBefore.VideoOpenCount + 1, opensAfterInitialLoad);
        Assert.Equal(countersBefore.ActiveRenderers + 1, BackgroundMediaDiagnostics.Snapshot.ActiveRenderers);

        // Theme color/opacity/playback changes replace the resource dictionary but
        // must retain the same renderer and must not reopen an unchanged MP4.
        var recoloredVideo = videoSettings.Clone();
        recoloredVideo.Accent = "#FF33AA77";
        recoloredVideo.Card = "#FF152535";
        recoloredVideo.BackgroundOpacity = 0.61;
        recoloredVideo.VideoPlaybackSpeed = 1.5;
        recoloredVideo.ImageFlipHorizontal = true;
        manager.ApplyTemporary("background-lifecycle", recoloredVideo);
        DrainDispatcher();
        Assert.Same(video, background.ActiveVideoRenderer);
        Assert.Equal(opensAfterInitialLoad, BackgroundMediaDiagnostics.Snapshot.VideoOpenCount);

        video.SuspendForInteraction();
        Assert.True(video.IsInteractionSuspended);
        Assert.False(video.IsPlaybackRequested);
        video.ResumeAfterInteraction();
        Assert.False(video.IsInteractionSuspended);
        Assert.True(video.IsPlaybackRequested);
        Assert.Equal(opensAfterInitialLoad, BackgroundMediaDiagnostics.Snapshot.VideoOpenCount);

        // Profile-run pauses use the existing player and must resume it instead of
        // opening a replacement player or attaching new lifecycle handlers.
        background.PauseDuringProfileExecution = true;
        background.IsProfileExecutionActive = true;
        DrainDispatcher();
        Assert.False(video.IsPlaybackRequested);
        Assert.Same(video, background.ActiveVideoRenderer);
        Assert.Equal(opensAfterInitialLoad, BackgroundMediaDiagnostics.Snapshot.VideoOpenCount);
        background.IsProfileExecutionActive = false;
        DrainDispatcher();
        Assert.True(video.IsPlaybackRequested);
        Assert.Same(video, background.ActiveVideoRenderer);
        background.PerformanceMode = BackgroundPerformanceModes.Economy;
        DrainDispatcher();
        Assert.Same(video, background.ActiveVideoRenderer);

        var wheel = new System.Windows.Input.MouseWheelEventArgs(
            System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, -120)
        {
            RoutedEvent = System.Windows.Input.Mouse.PreviewMouseWheelEvent
        };
        host.RaiseEvent(wheel);
        Assert.True(video.IsInteractionSuspended);
        Assert.False(video.IsPlaybackRequested);
        video.ResumeAfterInteraction();

        background.ImageOpacity = 0;
        DrainDispatcher();
        Assert.False(video.IsPlaybackRequested);
        background.ImageOpacity = recoloredVideo.BackgroundOpacity;
        DrainDispatcher();
        Assert.True(video.IsPlaybackRequested);
        Assert.Equal(opensAfterInitialLoad, BackgroundMediaDiagnostics.Snapshot.VideoOpenCount);

        host.WindowState = System.Windows.WindowState.Minimized;
        DrainDispatcher();
        Assert.False(video.IsPlaybackRequested);
        host.WindowState = System.Windows.WindowState.Normal;
        DrainDispatcher();
        Assert.True(video.IsPlaybackRequested);
        host.Hide();
        DrainDispatcher();
        Assert.False(video.IsPlaybackRequested);
        host.Show();
        DrainDispatcher();
        Assert.True(video.IsPlaybackRequested);
        Assert.Equal(opensAfterInitialLoad, BackgroundMediaDiagnostics.Snapshot.VideoOpenCount);

        // The editor sends its preview to application resources/MainWindow. It must
        // not contain or create another media host/player of its own.
        var rendererCountBeforeEditor = BackgroundMediaDiagnostics.Snapshot.ActiveRenderers;
        var editor = new SwitchBoard.Views.CustomThemeWindow(
            new CustomThemeEditRequest(CustomThemeEditMode.EditCustom, "Lifecycle", recoloredVideo, [],
                "background-lifecycle", settings => manager.ApplyTemporary("background-lifecycle", settings)),
            paths, new TestLocalizationService());
        editor.Show();
        editor.UpdateLayout();
        Assert.Empty(FindVisualChildren<ThemeBackground>(editor));
        Assert.Equal(rendererCountBeforeEditor, BackgroundMediaDiagnostics.Snapshot.ActiveRenderers);
        editor.ViewModel.Colors.First(item => item.Key == "accent").Color = "#FF4488CC";
        DrainDispatcher();
        Assert.Equal(rendererCountBeforeEditor, BackgroundMediaDiagnostics.Snapshot.ActiveRenderers);
        Assert.Equal(opensAfterInitialLoad, BackgroundMediaDiagnostics.Snapshot.VideoOpenCount);
        editor.Close();
        DrainDispatcher();

        var gifSettings = CustomThemeSettings.CreateDefault();
        gifSettings.BackgroundAssetFileName = "test.gif";
        manager.ApplyTemporary("background-lifecycle", gifSettings);
        DrainDispatcher();
        Assert.Equal(BackgroundAssetKind.Gif, background.ActiveAssetKind);
        Assert.Equal(1, background.ActiveRendererCount);
        var gif = Assert.IsType<AnimatedBackground>(background.ActiveImageRenderer);
        Assert.Equal(2, gif.DecodedFrameCount);
        Assert.True(gif.AreDecodedFramesFrozen);
        Assert.True(gif.IsAnimationRunning);
        Assert.Equal(new BackgroundNativeSize(Path.GetFullPath(Path.Combine(paths.CustomThemeDirectory, "test.gif")), 2, 2),
            Assert.Single(nativeSizeEvents));
        var gifDecodes = BackgroundMediaDiagnostics.Snapshot.GifDecodeCount;

        var adjustedGif = gifSettings.Clone();
        adjustedGif.Accent = "#FFAA6633";
        adjustedGif.BackgroundOpacity = 0.72;
        adjustedGif.ImageFit = BackgroundImageFits.Center;
        adjustedGif.GifAnimationDirection = GifAnimationDirections.PingPong;
        adjustedGif.GifAnimationSpeed = 2;
        adjustedGif.ImageFlipVertical = true;
        manager.ApplyTemporary("background-lifecycle", adjustedGif);
        DrainDispatcher();
        Assert.Same(gif, background.ActiveImageRenderer);
        Assert.Equal(gifDecodes, BackgroundMediaDiagnostics.Snapshot.GifDecodeCount);
        Assert.Equal(2, gif.DecodedFrameCount);
        Assert.Single(nativeSizeEvents);

        gif.SuspendForInteraction();
        Assert.True(gif.IsInteractionSuspended);
        Assert.False(gif.IsAnimationRunning);
        gif.ResumeAfterInteraction();
        Assert.False(gif.IsInteractionSuspended);
        Assert.True(gif.IsAnimationRunning);

        background.PauseDuringProfileExecution = true;
        background.IsProfileExecutionActive = true;
        DrainDispatcher();
        Assert.False(gif.IsAnimationRunning);
        Assert.Same(gif, background.ActiveImageRenderer);
        Assert.Equal(gifDecodes, BackgroundMediaDiagnostics.Snapshot.GifDecodeCount);
        background.IsProfileExecutionActive = false;
        background.GifFrameRateLimit = GifFrameRateLimits.FramesPerSecond30;
        DrainDispatcher();
        Assert.True(gif.IsAnimationRunning);
        Assert.Same(gif, background.ActiveImageRenderer);
        Assert.Equal(gifDecodes, BackgroundMediaDiagnostics.Snapshot.GifDecodeCount);

        background.ImageOpacity = 0;
        DrainDispatcher();
        Assert.False(gif.IsAnimationRunning);
        background.ImageOpacity = adjustedGif.BackgroundOpacity;
        DrainDispatcher();
        Assert.True(gif.IsAnimationRunning);
        Assert.Equal(gifDecodes, BackgroundMediaDiagnostics.Snapshot.GifDecodeCount);

        host.WindowState = System.Windows.WindowState.Minimized;
        DrainDispatcher();
        Assert.False(gif.IsAnimationRunning);
        Assert.Equal(gifDecodes, BackgroundMediaDiagnostics.Snapshot.GifDecodeCount);
        host.WindowState = System.Windows.WindowState.Normal;
        DrainDispatcher();
        Assert.True(gif.IsAnimationRunning);
        host.Hide();
        DrainDispatcher();
        Assert.False(gif.IsAnimationRunning);
        host.Show();
        DrainDispatcher();
        Assert.True(gif.IsAnimationRunning);
        Assert.Equal(gifDecodes, BackgroundMediaDiagnostics.Snapshot.GifDecodeCount);

        // Exercise repeated type changes. Each transition releases the previous
        // renderer before the next one exists, and removing the asset releases all.
        foreach (var asset in new[] { "test.jpg", "test.gif", "test.mp4", "test.jpg", "test.gif", "test.mp4" })
        {
            var settings = CustomThemeSettings.CreateDefault();
            settings.BackgroundAssetFileName = asset;
            manager.ApplyTemporary("background-lifecycle", settings);
            DrainDispatcher();
            Assert.Equal(1, background.ActiveRendererCount);
            Assert.Equal(countersBefore.ActiveRenderers + 1,
                BackgroundMediaDiagnostics.Snapshot.ActiveRenderers);
            Assert.InRange(BackgroundMediaDiagnostics.Snapshot.ActiveMediaPlayers, 0, 1);
            Assert.InRange(BackgroundMediaDiagnostics.Snapshot.ActiveGifTimers, 0, 1);
        }

        var noBackground = CustomThemeSettings.CreateDefault();
        manager.ApplyTemporary("background-lifecycle", noBackground);
        DrainDispatcher();
        Assert.Equal(BackgroundAssetKind.None, background.ActiveAssetKind);
        Assert.Equal(0, background.ActiveRendererCount);
        Assert.Equal(countersBefore.ActiveRenderers, BackgroundMediaDiagnostics.Snapshot.ActiveRenderers);
        Assert.Equal(countersBefore.ActiveMediaPlayers, BackgroundMediaDiagnostics.Snapshot.ActiveMediaPlayers);
        Assert.Equal(countersBefore.ActiveGifTimers, BackgroundMediaDiagnostics.Snapshot.ActiveGifTimers);
        Assert.Equal(0, gif.DecodedFrameCount);

        return host;
    }

    private static IEnumerable<T> FindVisualChildren<T>(System.Windows.DependencyObject root)
        where T : System.Windows.DependencyObject
    {
        for (var index = 0; index < System.Windows.Media.VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, index);
            if (child is T match) yield return match;
            foreach (var descendant in FindVisualChildren<T>(child)) yield return descendant;
        }
    }

    private static void DrainDispatcher()
    {
        var dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var frame = new System.Windows.Threading.DispatcherFrame();
        _ = dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(() => frame.Continue = false));
        using var watchdog = new Timer(_ =>
        {
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;
            try
            {
                _ = dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Send,
                    new Action(() => frame.Continue = false));
            }
            catch (InvalidOperationException)
            {
                // A concurrent test shutdown already makes the nested frame irrelevant.
            }
        }, null, TimeSpan.FromMilliseconds(500), Timeout.InfiniteTimeSpan);
        System.Windows.Threading.Dispatcher.PushFrame(frame);
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

        var scheduledApplyCount = 0;
        CustomThemeSettings? lastScheduled = null;
        using (var scheduler = new WpfCustomThemeEditorService.ThemePreviewScheduler(
                   settings => { scheduledApplyCount++; lastScheduled = settings; }, extreme,
                   System.Windows.Threading.Dispatcher.CurrentDispatcher))
        {
            var firstQueued = extreme.Clone();
            firstQueued.Accent = "#FF010203";
            scheduler.Queue(firstQueued);
            var latestQueued = firstQueued.Clone();
            latestQueued.Accent = "#FF040506";
            scheduler.Queue(latestQueued);
            Assert.True(scheduler.HasPendingUpdate);
            Assert.Equal(0, scheduledApplyCount);
            scheduler.Flush();
            Assert.Equal(1, scheduledApplyCount);
            Assert.Equal("#FF040506", lastScheduled?.Accent);

            // Source switches stay synchronous so the old player/file handle is
            // released before CustomThemeWindow removes a temporary media asset.
            var changedAsset = latestQueued.Clone();
            changedAsset.BackgroundAssetFileName = "new-background.mp4";
            scheduler.Queue(changedAsset);
            Assert.Equal(2, scheduledApplyCount);
            Assert.False(scheduler.HasPendingUpdate);
        }

        var applyCountBeforeHoverChange = liveApplyCount;
        editor.ViewModel.HoverIntensityPercent = 50;
        Assert.Equal(50, editor.ViewModel.Settings.HoverIntensity);
        Assert.True(liveApplyCount > applyCountBeforeHoverChange);
        var interactiveHover = Assert.IsType<SolidColorBrush>(app.TryFindResource("InteractiveHoverBrush"));
        Assert.Equal((byte)Math.Round(51 * 0.8 * 0.5), interactiveHover.Color.A);

        editor.ViewModel.SetBackground("animated.gif", null);
        Assert.True(editor.ViewModel.HasGifBackground);
        editor.ViewModel.GifAnimationDirection = GifAnimationDirections.Reverse;
        editor.ViewModel.GifAnimationSpeed = 2;
        editor.ViewModel.ImageFit = BackgroundImageFits.Center;
        editor.ViewModel.ImageFlipHorizontal = true;
        editor.ViewModel.ImageFlipVertical = true;
        Assert.Equal(GifAnimationDirections.Reverse, app.TryFindResource("CustomBackgroundGifAnimationDirection"));
        Assert.Equal(2d, app.TryFindResource("CustomBackgroundGifAnimationSpeed"));
        Assert.True((bool)app.TryFindResource("CustomBackgroundFlipHorizontal")!);
        Assert.True((bool)app.TryFindResource("CustomBackgroundFlipVertical")!);
        editor.ViewModel.SetBackground("static.png", null);
        Assert.False(editor.ViewModel.HasGifBackground);

        editor.ViewModel.SetBackground("video.mp4", null);
        Assert.False(editor.ViewModel.HasGifBackground);
        Assert.True(editor.ViewModel.HasVideoBackground);
        editor.ViewModel.VideoPlaybackSpeed = 1.5;
        editor.ViewModel.VideoAudioEnabled = true;
        Assert.Equal(1.5d, app.TryFindResource("CustomBackgroundVideoPlaybackSpeed"));
        Assert.True((bool)app.TryFindResource("CustomBackgroundVideoAudioEnabled")!);
        editor.ViewModel.SetBackground("static.png", null);
        Assert.False(editor.ViewModel.HasVideoBackground);

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
        Assert.Equal(78, editor.ViewModel.Settings.HoverIntensity);
        Assert.Equal(GifAnimationDirections.Normal, editor.ViewModel.Settings.GifAnimationDirection);
        Assert.Equal(1d, editor.ViewModel.Settings.GifAnimationSpeed);
        Assert.Equal(1d, editor.ViewModel.Settings.VideoPlaybackSpeed);
        Assert.False(editor.ViewModel.Settings.VideoAudioEnabled);
        Assert.False(editor.ViewModel.Settings.ImageFlipHorizontal);
        Assert.False(editor.ViewModel.Settings.ImageFlipVertical);
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

    private static void VerifyThemePickerControl(System.Windows.Application app, ThemeManager manager)
    {
        var events = new List<Color>();
        var confirmed = 0;
        var picker = new SwitchBoard.Views.ThemeColorPickerControl(Colors.White, new TestLocalizationService(),
            color => events.Add(color));
        picker.Confirmed += (_, _) => confirmed++;
        var pickerHost = new System.Windows.Window
        {
            ShowInTaskbar = false,
            SizeToContent = System.Windows.SizeToContent.WidthAndHeight,
            Content = picker
        };
        var nameWindow = new SwitchBoard.Views.ThemeNameWindow("Theme", [], new TestLocalizationService());
        pickerHost.Show();
        nameWindow.Show();
        picker.UpdateLayout();
        nameWindow.UpdateLayout();
        AssertBrushColor(picker.Background, ((SolidColorBrush)app.TryFindResource("BackgroundBrush")!).Color);
        AssertBrushColor(picker.Foreground, ((SolidColorBrush)app.TryFindResource("TextPrimaryBrush")!).Color);
        AssertBrushColor(nameWindow.Background, ((SolidColorBrush)app.TryFindResource("BackgroundBrush")!).Color);
        AssertBrushColor(nameWindow.Foreground, ((SolidColorBrush)app.TryFindResource("TextPrimaryBrush")!).Color);
        ((System.Windows.Controls.Slider)picker.FindName("Red")).Value = 16;
        Assert.Equal(16, events.LastOrDefault().R);
        picker.BeginEdit(Colors.White);
        ((System.Windows.Controls.TextBox)picker.FindName("HexValue")).Text = "#80445566";
        ((System.Windows.Controls.Button)picker.FindName("ConfirmButton")).RaiseEvent(
            new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
        Assert.Equal(1, confirmed);
        Assert.Equal(Color.FromArgb(128, 68, 85, 102), events.LastOrDefault());

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
        pickerHost.Close();
        nameWindow.Close();
    }

    private static void VerifyCardSurfaceControl(System.Windows.Application app, ThemeManager manager)
    {
        var card = new CardSurfaceControl
        {
            Style = Assert.IsType<System.Windows.Style>(app.TryFindResource("CardSurfaceStyle")),
            Padding = new System.Windows.Thickness(12),
            Content = new System.Windows.Controls.TextBlock { Text = "Card surface smoke test" }
        };
        var host = new System.Windows.Window
        {
            ShowActivated = false,
            ShowInTaskbar = false,
            Width = 240,
            Height = 100,
            Content = card
        };

        host.Show();
        host.UpdateLayout();
        card.ApplyTemplate();

        var templateRoot = System.Windows.Media.VisualTreeHelper.GetChild(card, 0);
        var backgroundLayer = Assert.IsType<System.Windows.Controls.Border>(
            System.Windows.Media.VisualTreeHelper.GetChild(templateRoot, 0));
        Assert.Equal(1, card.Opacity);
        Assert.Equal(0.24, card.SurfaceOpacity, 3);
        Assert.Equal(0.24, backgroundLayer.Opacity, 3);

        var draft = CustomThemeSettings.CreateDefault();
        draft.Background = "#FF101820";
        draft.Panel = "#FF18232E";
        draft.Card = "#FF1E6ED2";
        draft.SurfaceOpacity = 1;
        manager.ApplyTemporary("card-surface-smoke", draft);
        host.UpdateLayout();

        AssertBrushColor(((System.Windows.Controls.Border)backgroundLayer).Background, Color.FromRgb(30, 110, 210));
        Assert.Equal(1, card.Opacity);
        host.Close();
    }

    private static void AssertBrushColor(Brush? brush, Color expected) =>
        Assert.Equal(expected, Assert.IsType<SolidColorBrush>(brush).Color);

    private static byte[] ReadFirstPixel(BitmapSource source)
    {
        var pixel = new byte[4];
        source.CopyPixels(new System.Windows.Int32Rect(0, 0, 1, 1), pixel, 4, 0);
        return pixel;
    }

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
            finally
            {
                var dispatcher = System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread);
                if (dispatcher is { HasShutdownStarted: false }) dispatcher.InvokeShutdown();
            }
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
            // The scenario creates and closes auxiliary windows without running the
            // normal application loop. Keep the WPF lifetime explicit so an
            // intermediate window cannot begin Application shutdown.
            app.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;
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
            finally
            {
                app.Shutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(10)))
            throw new TimeoutException("STA theme scenario did not complete within 10 seconds.");
        if (error is not null) throw new InvalidOperationException("STA theme scenario failed.", error);
    }
}
