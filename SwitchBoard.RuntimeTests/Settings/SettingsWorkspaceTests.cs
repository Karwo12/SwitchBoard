using SwitchBoard.RuntimeTests.TestInfrastructure;

namespace SwitchBoard.RuntimeTests.Settings;

[Collection("Windows runtime")]
public sealed class SettingsWorkspaceTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void UserSettings_UseSafeDefaultsForNewWorkspaceOptions()
    {
        var settings = new UserSettings();

        Assert.False(settings.LaunchAtStartup);
        Assert.False(settings.StartMinimizedToTray);
        Assert.Equal("close", settings.CloseBehavior);
        Assert.True(settings.PauseAnimatedBackgroundWhenMinimized);
        Assert.False(settings.PauseAnimatedBackgroundWhenInactive);
        Assert.False(settings.PauseAnimatedBackgroundDuringProfileExecution);
        Assert.Equal(BackgroundPerformanceModes.FullQuality, settings.BackgroundPerformanceMode);
        Assert.Equal(GifFrameRateLimits.Native, settings.GifFrameRateLimit);
        Assert.Equal(Mp4RendererPreferences.Automatic, settings.Mp4RendererPreference);
        Assert.False(settings.AutomaticBackupEnabled);
        Assert.Equal(5, settings.AutomaticBackupCount);
        Assert.False(settings.CreateBackupOnExit);
        Assert.Equal(HistoryRetentionOptions.NinetyDays, settings.HistoryRetentionDays);
        Assert.False(settings.CheckForUpdatesAtStartup);
        Assert.Null(settings.LastKnownLatestVersion);
        Assert.Equal(TimeSpan.FromSeconds(1d / 30d),
            GifFrameRateLimits.Apply(GifFrameRateLimits.FramesPerSecond30, TimeSpan.FromMilliseconds(1)));
        Assert.False(settings.RememberLastView);
        Assert.Equal("Home", settings.LastMainView);
        Assert.True(settings.WarnBeforeClosingWithUnsavedChanges);
        Assert.Equal("standard", settings.InterfaceDensity);
        Assert.True(settings.ShowCardDetails);
        Assert.False(settings.AutoFitWindowToBackground);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task NewWorkspaceSettings_RoundTripThroughJsonRepository()
    {
        var root = Path.Combine(Path.GetTempPath(), $"SwitchBoard-settings-roundtrip-{Guid.NewGuid():N}");
        try
        {
            var paths = new AppDataPaths(root);
            var original = new UserSettings
            {
                CloseBehavior = "tray",
                StartMinimizedToTray = true,
                PauseAnimatedBackgroundWhenInactive = true,
                PauseAnimatedBackgroundDuringProfileExecution = true,
                BackgroundPerformanceMode = BackgroundPerformanceModes.Economy,
                GifFrameRateLimit = GifFrameRateLimits.FramesPerSecond30,
                Mp4RendererPreference = Mp4RendererPreferences.LibVlc,
                AutomaticBackupEnabled = true,
                AutomaticBackupCount = 7,
                CreateBackupOnExit = true,
                HistoryRetentionDays = HistoryRetentionOptions.ThreeHundredSixtyFiveDays,
                CheckForUpdatesAtStartup = true,
                LastKnownLatestVersion = "9.8.7",
                LastKnownReleaseUrl = "https://example.test/release",
                LastUpdateCheckUtc = new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero),
                LastUpdateCheckStatus = "UpToDate",
                AutoFitWindowToBackground = true
            };

            using (var repository = new JsonSettingsRepository(paths))
            {
                await repository.SaveAsync(original);
            }

            using var reloadedRepository = new JsonSettingsRepository(paths);
            var reloaded = await reloadedRepository.LoadAsync();

            Assert.Equal("tray", reloaded.CloseBehavior);
            Assert.True(reloaded.StartMinimizedToTray);
            Assert.True(reloaded.PauseAnimatedBackgroundWhenInactive);
            Assert.True(reloaded.PauseAnimatedBackgroundDuringProfileExecution);
            Assert.Equal(BackgroundPerformanceModes.Economy, reloaded.BackgroundPerformanceMode);
            Assert.Equal(GifFrameRateLimits.FramesPerSecond30, reloaded.GifFrameRateLimit);
            Assert.Equal(Mp4RendererPreferences.LibVlc, reloaded.Mp4RendererPreference);
            Assert.True(reloaded.AutomaticBackupEnabled);
            Assert.Equal(7, reloaded.AutomaticBackupCount);
            Assert.True(reloaded.CreateBackupOnExit);
            Assert.Equal(HistoryRetentionOptions.ThreeHundredSixtyFiveDays, reloaded.HistoryRetentionDays);
            Assert.True(reloaded.CheckForUpdatesAtStartup);
            Assert.Equal("9.8.7", reloaded.LastKnownLatestVersion);
            Assert.Equal("https://example.test/release", reloaded.LastKnownReleaseUrl);
            Assert.Equal(new DateTimeOffset(2026, 8, 28, 12, 0, 0, TimeSpan.Zero), reloaded.LastUpdateCheckUtc);
            Assert.Equal("UpToDate", reloaded.LastUpdateCheckStatus);
            Assert.True(reloaded.AutoFitWindowToBackground);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task LegacySettings_LoadWithNewWorkspaceDefaults()
    {
        var root = Path.Combine(Path.GetTempPath(), $"SwitchBoard-legacy-settings-{Guid.NewGuid():N}");
        try
        {
            var paths = new AppDataPaths(root);
            Directory.CreateDirectory(paths.RootDirectory);
            await File.WriteAllTextAsync(paths.SettingsFilePath,
                "{\"schemaVersion\":10,\"themeId\":\"graphite-glass\",\"languageId\":\"en\"}");

            using var repository = new JsonSettingsRepository(paths);
            var settings = await repository.LoadAsync();

            Assert.Equal(10, settings.SchemaVersion);
            Assert.False(settings.RememberLastView);
            Assert.False(settings.StartMinimizedToTray);
            Assert.True(settings.PauseAnimatedBackgroundWhenMinimized);
            Assert.False(settings.PauseAnimatedBackgroundWhenInactive);
            Assert.Equal(BackgroundPerformanceModes.FullQuality, settings.BackgroundPerformanceMode);
            Assert.Equal(GifFrameRateLimits.Native, settings.GifFrameRateLimit);
            Assert.Equal(Mp4RendererPreferences.Automatic, settings.Mp4RendererPreference);
            Assert.Equal(HistoryRetentionOptions.NinetyDays, settings.HistoryRetentionDays);
            Assert.False(settings.CheckForUpdatesAtStartup);
            Assert.True(settings.WarnBeforeClosingWithUnsavedChanges);
            Assert.Equal("standard", settings.InterfaceDensity);
            Assert.True(settings.ShowCardDetails);
            Assert.False(settings.AutoFitWindowToBackground);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    [Trait("Category", "Regression")]
    public async Task NewerSettingsSchema_IsRejectedWithoutReplacingItWithDefaults()
    {
        var root = Path.Combine(Path.GetTempPath(), $"SwitchBoard-newer-settings-{Guid.NewGuid():N}");
        try
        {
            var paths = new AppDataPaths(root);
            Directory.CreateDirectory(paths.RootDirectory);
            const string incompatible = "{\"schemaVersion\":999,\"themeId\":\"future-theme\"}";
            await File.WriteAllTextAsync(paths.SettingsFilePath, incompatible);

            using var repository = new JsonSettingsRepository(paths);
            await Assert.ThrowsAsync<InvalidDataException>(() => repository.LoadAsync());
            Assert.Equal(incompatible, await File.ReadAllTextAsync(paths.SettingsFilePath));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    [Trait("Category", "Regression")]
    public async Task MalformedSettings_AreRejectedWithoutReplacingTheFile()
    {
        var root = Path.Combine(Path.GetTempPath(), $"SwitchBoard-malformed-settings-{Guid.NewGuid():N}");
        try
        {
            var paths = new AppDataPaths(root);
            Directory.CreateDirectory(paths.RootDirectory);
            const string malformed = "{not-json";
            await File.WriteAllTextAsync(paths.SettingsFilePath, malformed);

            using var repository = new JsonSettingsRepository(paths);
            await Assert.ThrowsAsync<InvalidDataException>(() => repository.LoadAsync());
            Assert.Equal(malformed, await File.ReadAllTextAsync(paths.SettingsFilePath));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task MainView_RemembersLastViewOnlyWhenEnabled()
    {
        using var context = new RuntimeTestContext();
        var settings = new UserSettings { ThemeId = ThemeIds.Graphite, LanguageId = "en" };
        var catalogService = new TestCatalogService();
        var repository = new TestSettingsRepository();
        var localization = new TestLocalizationService();
        var catalog = new SwitchBoardCatalog();
        using var main = new MainWindowViewModel(
            catalogService, new TestDialogService(), catalog, new TestThemeManager(), localization,
            repository, settings, context.Runner,
            new ProfileRestoreRunner(context.Registry, context.SessionRepository), context.SessionRepository,
            new TestCompletionBehavior(), new TestDisplayManager(new("", "", "", 1, 1, 1, 32, 0, 0, 0, 0)),
            new TestCustomThemeEditorService());

        main.ActiveMainView = MainViewMode.Activity;
        await main.FlushPendingSettingsSaveAsync();
        Assert.Equal("Home", settings.LastMainView);

        main.RememberLastView = true;
        main.ActiveMainView = MainViewMode.Settings;
        await main.FlushPendingSettingsSaveAsync();

        Assert.Equal("Settings", settings.LastMainView);
        Assert.Equal(MainViewMode.Settings, main.InitialMainView);
        Assert.Equal(settings.LastMainView, repository.Saved.LastMainView);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Backup_RoundTripsRootOrderSettingsAndThemeDefinitions()
    {
        var root = Path.Combine(Path.GetTempPath(), $"SwitchBoard-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var category = new CategoryDefinition { Name = "Work", SortOrder = 0 };
            var rootProfile = new ProfileDefinition { Name = "Root", CategoryId = Guid.Empty, SortOrder = 0 };
            var childProfile = new ProfileDefinition { Name = "Child", CategoryId = category.Id, SortOrder = 0 };
            var catalog = new SwitchBoardCatalog
            {
                Categories = [category],
                Profiles = [rootProfile, childProfile],
                RootNavigationOrder =
                [
                    new() { Kind = RootNavigationItemKind.Profile, Id = rootProfile.Id },
                    new() { Kind = RootNavigationItemKind.Category, Id = category.Id }
                ]
            };
            var settings = new UserSettings
            {
                ThemeId = "custom-blue",
                RememberLastView = true,
                LastMainView = "Activity",
                InterfaceDensity = "compact",
                ShowCardDetails = false,
                AutoFitWindowToBackground = true,
                CustomThemes =
                [
                    new CustomThemeDefinition
                    {
                        Id = "custom-blue",
                        Name = "Blue",
                        Colors = new CustomThemeSettings
                        {
                            Accent = "#FF123456",
                            BackgroundAssetFileName = "outside.png",
                            PreviewBackgroundPath = Path.Combine(root, "outside.png")
                        }
                    }
                ]
            };
            var backupPath = Path.Combine(root, "workspace.sbbackup");
            var service = new SwitchBoardBackupService();

            await service.ExportAsync(catalog, settings, backupPath);
            var imported = await service.ImportAsync(backupPath);

            Assert.Equal(catalog.RootNavigationOrder!.Select(item => item.Id),
                imported.Catalog.RootNavigationOrder!.Select(item => item.Id));
            Assert.Equal(new[] { category.Id }, imported.Catalog.Profiles
                .Where(profile => profile.CategoryId == category.Id)
                .Select(profile => profile.CategoryId).Distinct());
            Assert.Equal("Activity", imported.Settings.LastMainView);
            Assert.Equal("compact", imported.Settings.InterfaceDensity);
            Assert.False(imported.Settings.ShowCardDetails);
            Assert.True(imported.Settings.AutoFitWindowToBackground);
            var importedTheme = Assert.Single(imported.Settings.CustomThemes);
            Assert.Equal("#FF123456", importedTheme.Colors.Accent);
            Assert.Null(importedTheme.Colors.BackgroundAssetFileName);
            Assert.Null(importedTheme.Colors.PreviewBackgroundPath);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void SettingsAndActionViews_ExposeCompactWorkspaceAndPreserveRuntimeStates()
    {
        var mainXaml = File.ReadAllText(FindSourceFile("Views", "MainWindow.xaml"));
        var baseStyles = File.ReadAllText(FindSourceFile("Themes", "BaseStyles.xaml"));
        var themeWindowXaml = File.ReadAllText(FindSourceFile("Views", "CustomThemeWindow.xaml"));
        var actionXaml = File.ReadAllText(FindSourceFile("Controls", "ActionEditorControl.xaml"));
        var nestedActionXaml = File.ReadAllText(FindSourceFile("Controls", "NestedActionEditorControl.xaml"));
        var cardControl = File.ReadAllText(FindSourceFile("Controls", "CardSurfaceControl.cs"));

        Assert.Contains("x:Name=\"SettingsWorkspace\"", mainXaml, StringComparison.Ordinal);
        var shellStart = mainXaml.IndexOf("<Grid x:Name=\"SettingsWorkspace\"", StringComparison.Ordinal);
        var hostStart = mainXaml.IndexOf("<Grid x:Name=\"SettingsWorkspaceContentHost\"", shellStart, StringComparison.Ordinal);
        var contentStart = mainXaml.IndexOf("<Grid x:Name=\"SettingsWorkspaceContent\"", shellStart, StringComparison.Ordinal);
        Assert.True(shellStart >= 0 && hostStart > shellStart && contentStart > hostStart);
        var shell = mainXaml[shellStart..hostStart];
        Assert.Contains("HorizontalAlignment=\"Stretch\"", shell, StringComparison.Ordinal);
        Assert.DoesNotContain("MaxWidth=", shell, StringComparison.Ordinal);
        Assert.Contains("Width=\"{Binding ActualWidth, ElementName=SettingsWorkspaceContentHost}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("MaxWidth=\"1100\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("HorizontalAlignment=\"Left\"", mainXaml[contentStart..], StringComparison.Ordinal);
        Assert.Contains("Width=\"200\" MinWidth=\"190\" MaxWidth=\"210\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource SettingsContentContainer}\"", mainXaml[contentStart..], StringComparison.Ordinal);
        Assert.Contains("HorizontalContentAlignment=\"Stretch\"", mainXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Style=\"{StaticResource GlassCard}\"", mainXaml[contentStart..], StringComparison.Ordinal);
        Assert.True(mainXaml[contentStart..].Split("Style=\"{StaticResource SettingsSectionCard}\"").Length - 1 >= 7);
        Assert.Contains("x:Key=\"SettingsContentContainer\"", baseStyles, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"MaxWidth\" Value=\"760\" />", baseStyles, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"HorizontalAlignment\" Value=\"Stretch\" />", baseStyles, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"CardSurfaceStyle\"", baseStyles, StringComparison.Ordinal);
        var cardStyleStart = baseStyles.IndexOf("x:Key=\"CardSurfaceStyle\"", StringComparison.Ordinal);
        var settingsCardStart = baseStyles.IndexOf("x:Key=\"SettingsSectionCard\"", cardStyleStart, StringComparison.Ordinal);
        var actionCardStart = baseStyles.IndexOf("x:Key=\"ActionCardSurfaceStyle\"", settingsCardStart, StringComparison.Ordinal);
        Assert.True(cardStyleStart >= 0 && settingsCardStart > cardStyleStart && actionCardStart > settingsCardStart);
        var cardStyle = baseStyles[cardStyleStart..settingsCardStart];
        Assert.Contains("Background\" Value=\"{DynamicResource CardSurfaceBrush}\"", cardStyle, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"SurfaceOpacity\" Value=\"0.24\" />", cardStyle, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"HoverSurfaceBrush\" Value=\"Transparent\" />", cardStyle, StringComparison.Ordinal);
        Assert.Contains("TemplateBinding HoverSurfaceBrush", cardStyle, StringComparison.Ordinal);
        Assert.Contains("InteractiveHoverBrush", baseStyles, StringComparison.Ordinal);
        Assert.Contains("HoverIntensityPercent", themeWindowXaml, StringComparison.Ordinal);
        Assert.Contains("CustomTheme.HoverIntensity", themeWindowXaml, StringComparison.Ordinal);
        Assert.Contains("Minimum=\"0\" Maximum=\"100\"", themeWindowXaml, StringComparison.Ordinal);
        Assert.Contains("controls:ThemeBackground", mainXaml, StringComparison.Ordinal);
        Assert.Contains("AutoFitWindowToBackground", mainXaml, StringComparison.Ordinal);
        Assert.Contains("HasGifBackground", themeWindowXaml, StringComparison.Ordinal);
        Assert.Contains("HasVideoBackground", themeWindowXaml, StringComparison.Ordinal);
        Assert.Contains("CustomTheme.VideoPlaybackSpeed", themeWindowXaml, StringComparison.Ordinal);
        Assert.Contains("CustomTheme.VideoAudioEnabled", themeWindowXaml, StringComparison.Ordinal);
        var settingsCardStyle = baseStyles[settingsCardStart..actionCardStart];
        Assert.Contains("BasedOn=\"{StaticResource CardSurfaceStyle}\"", settingsCardStyle, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"SettingsSectionCard\" TargetType=\"{x:Type controls:CardSurfaceControl}\"", settingsCardStyle, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"ActionCardSurfaceStyle\" TargetType=\"{x:Type controls:CardSurfaceControl}\" BasedOn=\"{StaticResource CardSurfaceStyle}\"", baseStyles, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"SettingsSectionCard\"", baseStyles, StringComparison.Ordinal);
        Assert.Contains("SurfaceOpacity", cardControl, StringComparison.Ordinal);
        var activityStyleStart = mainXaml.IndexOf("x:Key=\"ActivityRowSurfaceStyle\"", StringComparison.Ordinal);
        var activityStyleEnd = mainXaml.IndexOf("x:Key=\"ProfileResultStatusDotStyle\"", activityStyleStart, StringComparison.Ordinal);
        Assert.True(activityStyleStart >= 0 && activityStyleEnd > activityStyleStart);
        var activityStyle = mainXaml[activityStyleStart..activityStyleEnd];
        Assert.Contains("BasedOn=\"{StaticResource CardSurfaceStyle}\"", activityStyle, StringComparison.Ordinal);
        Assert.Contains("<Setter Property=\"HoverSurfaceBrush\" Value=\"{DynamicResource InteractiveHoverBrush}\" />", activityStyle, StringComparison.Ordinal);
        Assert.DoesNotContain("<Setter Property=\"SurfaceOpacity\" Value=\"1\" />", activityStyle, StringComparison.Ordinal);
        var profilePanelStart = mainXaml.IndexOf("x:Name=\"ProfileNavigationContent\"", StringComparison.Ordinal);
        Assert.True(profilePanelStart >= 0 && profilePanelStart < shellStart);
        Assert.Contains("Padding=\"0,0,6,0\"", mainXaml[profilePanelStart..shellStart], StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SettingsProfileSelector\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding AllProfiles}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("DisplayMemberPath=\"SettingsDisplayName\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("SelectedItem=\"{Binding SelectedProfile, Mode=TwoWay}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding ProfileColorOptions}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("ItemsSource=\"{Binding ProfileIconOptions}\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SubmenuArrow\"", baseStyles, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SubmenuPopup\"", baseStyles, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"GeneralSettingsPanel\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DataSettingsPanel\"", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Settings.Density", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Settings.Data", mainXaml, StringComparison.Ordinal);
        Assert.Contains("Settings.Diagnostics", mainXaml, StringComparison.Ordinal);
        Assert.Contains("SelectedProfile.CloseSwitchBoardAfterSuccessfulCompletion", mainXaml, StringComparison.Ordinal);
        Assert.Contains("DataContext.ShowCardDetails", actionXaml, StringComparison.Ordinal);
        Assert.Contains("IsExecutionRunning", actionXaml, StringComparison.Ordinal);
        Assert.Contains("DataContext.ShowCardDetails", nestedActionXaml, StringComparison.Ordinal);
        Assert.Contains("ValidationLevel", nestedActionXaml, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void LogMaintenance_ClearsCurrentAndArchivedLogs()
    {
        var root = Path.Combine(Path.GetTempPath(), $"SwitchBoard-logs-{Guid.NewGuid():N}");
        try
        {
            var paths = new AppDataPaths(root);
            new LogMaintenanceService(paths).Clear();
            Assert.True(Directory.Exists(paths.LogsDirectory));
            Directory.CreateDirectory(paths.LogsDirectory);
            File.WriteAllText(Path.Combine(paths.LogsDirectory, "switchboard.log"), "current");
            File.WriteAllText(Path.Combine(paths.LogsDirectory, "switchboard.log.1"), "archive");

            new LogMaintenanceService(paths).Clear();

            Assert.Empty(File.ReadAllText(Path.Combine(paths.LogsDirectory, "switchboard.log")));
            Assert.False(File.Exists(Path.Combine(paths.LogsDirectory, "switchboard.log.1")));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static string FindSourceFile(params string[] relativePath)
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                var candidate = Path.Combine([directory.FullName, .. relativePath]);
                if (File.Exists(candidate)) return candidate;
            }
        }

        throw new FileNotFoundException("Could not find a source file for the settings regression test.",
            Path.Combine(relativePath));
    }
}
