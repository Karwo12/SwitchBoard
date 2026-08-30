using System.Net;
using System.Net.Http;
using System.Windows.Forms;
using SwitchBoard.RuntimeTests.TestInfrastructure;
using SwitchBoard.Services.Actions;
using SwitchBoard.Services.Updates;
using SwitchBoard.Services.Tray;
using SwitchBoard.Services.Diagnostics;

namespace SwitchBoard.RuntimeTests.Operations;

[Collection("Windows runtime")]
public sealed class ConfigurationOperationsTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task FullBackup_RestoresCatalogSettingsAndOwnedThemeAsset()
    {
        var root = Path.Combine(Path.GetTempPath(), $"SwitchBoard-backup-{Guid.NewGuid():N}");
        try
        {
            var paths = new AppDataPaths(root);
            Directory.CreateDirectory(paths.CustomThemeDirectory);
            var asset = Path.Combine(paths.CustomThemeDirectory, "background.png");
            var bytes = new byte[] { 1, 2, 3, 4, 5 };
            await File.WriteAllBytesAsync(asset, bytes);
            var profile = new ProfileDefinition { Name = "Games", Color = "#4F8EF7", Icon = "gamepad" };
            var catalog = new SwitchBoardCatalog { Profiles = [profile] };
            var settings = new UserSettings
            {
                CloseBehavior = "tray",
                AutomaticBackupEnabled = true,
                AutomaticBackupCount = 3,
                CustomThemes =
                [
                    new CustomThemeDefinition
                    {
                        Id = "custom-test",
                        Name = "Test",
                        Colors = new CustomThemeSettings { BackgroundAssetFileName = "background.png" }
                    }
                ]
            };
            var archive = Path.Combine(root, "configuration.sbbackup");
            var service = new SwitchBoardBackupService();

            await service.ExportAsync(catalog, settings, archive, paths);
            var package = await service.ImportPackageAsync(archive);

            Assert.Equal("Games", Assert.Single(package.Document.Catalog.Profiles).Name);
            Assert.Equal("tray", package.Document.Settings.CloseBehavior);
            Assert.True(package.Document.Settings.AutomaticBackupEnabled);
            Assert.Equal(bytes, package.ThemeAssets["background.png"]);

            Directory.Delete(paths.CustomThemeDirectory, recursive: true);
            using var staged = await service.StageThemeAssetsAsync(package, paths);
            staged.Commit();
            staged.Complete();
            Assert.Equal(bytes, await File.ReadAllBytesAsync(asset));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    [Trait("Category", "Regression")]
    public async Task CorruptedBackup_IsRejectedWithoutChangingCurrentConfigurationFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), $"SwitchBoard-corrupt-{Guid.NewGuid():N}");
        try
        {
            var paths = new AppDataPaths(root);
            Directory.CreateDirectory(paths.RootDirectory);
            await File.WriteAllTextAsync(paths.CatalogFilePath, "current-catalog");
            await File.WriteAllTextAsync(paths.SettingsFilePath, "current-settings");
            var corrupt = Path.Combine(root, "bad.sbbackup");
            await File.WriteAllTextAsync(corrupt, "not a zip");

            await Assert.ThrowsAnyAsync<Exception>(() => new SwitchBoardBackupService().ImportPackageAsync(corrupt));

            Assert.Equal("current-catalog", await File.ReadAllTextAsync(paths.CatalogFilePath));
            Assert.Equal("current-settings", await File.ReadAllTextAsync(paths.SettingsFilePath));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    [Trait("Category", "Regression")]
    public async Task BackupWithNewerCatalogSchema_IsRejectedBeforeImport()
    {
        var root = Path.Combine(Path.GetTempPath(), $"SwitchBoard-newer-catalog-{Guid.NewGuid():N}");
        try
        {
            var archivePath = Path.Combine(root, "newer.sbbackup");
            var service = new SwitchBoardBackupService();
            await service.ExportAsync(new SwitchBoardCatalog(), new UserSettings(), archivePath);

            using (var archive = System.IO.Compression.ZipFile.Open(
                       archivePath, System.IO.Compression.ZipArchiveMode.Update))
            {
                var manifestEntry = archive.GetEntry("backup.json")!;
                string manifest;
                using (var reader = new StreamReader(manifestEntry.Open()))
                    manifest = reader.ReadToEnd();

                var document = JsonNode.Parse(manifest)!.AsObject();
                document["catalog"]!["schemaVersion"] = CatalogSchema.CurrentVersion + 1;
                manifestEntry.Delete();
                using var writer = new StreamWriter(archive.CreateEntry("backup.json").Open());
                writer.Write(document.ToJsonString());
            }

            await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportPackageAsync(archivePath));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task AutomaticBackup_RotatesOldArchives()
    {
        var root = Path.Combine(Path.GetTempPath(), $"SwitchBoard-rotation-{Guid.NewGuid():N}");
        try
        {
            var paths = new AppDataPaths(root);
            var service = new SwitchBoardBackupService();
            for (var index = 0; index < 3; index++)
                await service.CreateAutomaticBackupAsync(new SwitchBoardCatalog(), new UserSettings(), paths, 2);

            Assert.Equal(2, Directory.EnumerateFiles(paths.AutoBackupsDirectory, "SwitchBoard-auto-*.sbbackup").Count());
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ManagedBackup_IsListedAndDoesNotChangeAutomaticBackupRetention()
    {
        var root = Path.Combine(Path.GetTempPath(), $"SwitchBoard-managed-backup-{Guid.NewGuid():N}");
        try
        {
            var paths = new AppDataPaths(root);
            var service = new SwitchBoardBackupService();
            await service.CreateAutomaticBackupAsync(new SwitchBoardCatalog(), new UserSettings(), paths, 1);

            var first = await service.CreateManagedBackupAsync(new SwitchBoardCatalog
            {
                Profiles = [new ProfileDefinition { Name = "Manual backup" }]
            }, new UserSettings(), paths, "manual");
            var second = await service.CreateManagedBackupAsync(new SwitchBoardCatalog(), new UserSettings(), paths, "exit");

            var listed = service.ListManagedBackups(paths);
            Assert.Equal(3, listed.Count);
            Assert.Contains(listed, backup => backup.Path == first);
            Assert.Contains(listed, backup => backup.Path == second);
            Assert.Single(Directory.EnumerateFiles(paths.AutoBackupsDirectory, "SwitchBoard-auto-*.sbbackup"));
            Assert.Equal("Manual backup", Assert.Single((await service.ImportPackageAsync(first)).Document.Catalog.Profiles).Name);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ResetSettings_KeepsProfilesAndCategories()
    {
        using var context = new RuntimeTestContext();
        var root = Path.Combine(Path.GetTempPath(), $"SwitchBoard-reset-settings-{Guid.NewGuid():N}");
        try
        {
            var category = new CategoryDefinition { Name = "Work" };
            var profile = new ProfileDefinition { Name = "Profile", CategoryId = category.Id };
            var catalog = new SwitchBoardCatalog { Categories = [category], Profiles = [profile] };
            var settings = new UserSettings { InterfaceDensity = "compact", CustomThemes = [new CustomThemeDefinition()] };
            using var main = CreateMain(context, catalog, settings, new AppDataPaths(root));

            await main.ResetSettingsAsync();

            Assert.Single(main.AllProfiles);
            Assert.Single(main.Categories);
            Assert.Equal("standard", settings.InterfaceDensity);
            Assert.Empty(settings.CustomThemes);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    [Trait("Category", "Regression")]
    public async Task DiagnosticExports_ContainOnlyRequestedHistoryDiagnosticsAndLogs()
    {
        var root = Path.Combine(Path.GetTempPath(), $"SwitchBoard-diagnostics-{Guid.NewGuid():N}");
        try
        {
            var paths = new AppDataPaths(root);
            Directory.CreateDirectory(paths.LogsDirectory);
            await File.WriteAllTextAsync(Path.Combine(paths.LogsDirectory, "switchboard.log"), "diagnostic log");
            var records = new List<PersistentActivityRecord>
            {
                new() { ProfileName = "Profile", Message = "Completed" }
            };
            var service = new DiagnosticExportService(paths);
            var diagnosticPath = Path.Combine(root, "diagnostics.zip");
            var historyPath = Path.Combine(root, "history.zip");

            await service.ExportDiagnosticsAsync(diagnosticPath, "SwitchBoard: test", records);
            await service.ExportHistoryAsync(historyPath, records);

            using (var diagnostics = System.IO.Compression.ZipFile.OpenRead(diagnosticPath))
            {
                var names = diagnostics.Entries.Select(entry => entry.FullName).ToList();
                Assert.Contains("diagnostics.txt", names);
                Assert.Contains("history.json", names);
                Assert.Contains("logs/switchboard.log", names);
                Assert.DoesNotContain("catalog.json", names);
                Assert.DoesNotContain("settings.json", names);
            }

            using (var history = System.IO.Compression.ZipFile.OpenRead(historyPath))
                Assert.Equal(["history.json"], history.Entries.Select(entry => entry.FullName));
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    [Trait("Category", "Regression")]
    public async Task DiagnosticExport_BoundsHistoryAndLogPayload()
    {
        var root = Path.Combine(Path.GetTempPath(), $"SwitchBoard-diagnostics-bound-{Guid.NewGuid():N}");
        try
        {
            var paths = new AppDataPaths(root);
            Directory.CreateDirectory(paths.LogsDirectory);
            await File.WriteAllTextAsync(Path.Combine(paths.LogsDirectory, "switchboard.log"),
                new string('x', 400 * 1024) + "\ntoken=very-secret-value");
            var records = Enumerable.Range(0, 250).Select(index => new PersistentActivityRecord
            {
                Timestamp = DateTimeOffset.UtcNow.AddMinutes(-index), Message = $"record {index}"
            }).ToList();
            var destination = Path.Combine(root, "diagnostics.zip");

            await new DiagnosticExportService(paths).ExportDiagnosticsAsync(destination, "SwitchBoard: test", records);

            using var archive = System.IO.Compression.ZipFile.OpenRead(destination);
            var history = archive.GetEntry("history.json")!;
            using var reader = new StreamReader(history.Open());
            var exportedRecords = JsonSerializer.Deserialize<List<PersistentActivityRecord>>(await reader.ReadToEndAsync())!;
            Assert.Equal(200, exportedRecords.Count);
            Assert.InRange(archive.GetEntry("logs/switchboard.log")!.Length, 1, 256 * 1024);
            using var logReader = new StreamReader(archive.GetEntry("logs/switchboard.log")!.Open());
            var log = await logReader.ReadToEndAsync();
            Assert.DoesNotContain("very-secret-value", log, StringComparison.Ordinal);
            Assert.Contains("token=[redacted]", log, StringComparison.Ordinal);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    [Trait("Category", "Regression")]
    public async Task ResetSettings_SynchronizesWindowsStartupRegistration()
    {
        using var context = new RuntimeTestContext();
        var root = Path.Combine(Path.GetTempPath(), $"SwitchBoard-reset-startup-{Guid.NewGuid():N}");
        try
        {
            var startup = new TestStartupRegistrationService(enabled: true);
            using var main = CreateMain(context, new SwitchBoardCatalog(),
                new UserSettings { LaunchAtStartup = true }, new AppDataPaths(root),
                startupRegistrationService: startup);

            await main.ResetSettingsAsync();

            Assert.False(startup.IsEnabled);
            Assert.Contains(false, startup.RequestedStates);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    [Trait("Category", "Regression")]
    public async Task ResetAll_DoesNotRunWhenSafetyBackupCannotBeCreated()
    {
        using var context = new RuntimeTestContext();
        var rootFile = Path.Combine(Path.GetTempPath(), $"SwitchBoard-reset-blocked-{Guid.NewGuid():N}");
        try
        {
            await File.WriteAllTextAsync(rootFile, "not a directory");
            var catalog = new SwitchBoardCatalog { Profiles = [new ProfileDefinition { Name = "Keep" }] };
            using var main = CreateMain(context, catalog, new UserSettings(), new AppDataPaths(rootFile));

            await main.ResetAllDataAsync();

            Assert.Single(main.AllProfiles);
        }
        finally { try { File.Delete(rootFile); } catch { } }
    }

    [Fact]
    [Trait("Category", "Regression")]
    public async Task ResetAll_ConfirmationHappensBeforeSafetyBackup()
    {
        using var context = new RuntimeTestContext();
        var root = Path.Combine(Path.GetTempPath(), $"SwitchBoard-reset-cancelled-{Guid.NewGuid():N}");
        try
        {
            var dialog = new TestDialogService { ConfirmResult = false };
            var catalog = new SwitchBoardCatalog { Profiles = [new ProfileDefinition { Name = "Keep" }] };
            using var main = CreateMain(context, catalog, new UserSettings(), new AppDataPaths(root), dialog: dialog);

            await main.ResetAllDataAsync();

            Assert.Single(main.AllProfiles);
            Assert.Empty(Directory.Exists(Path.Combine(root, "backups", "automatic"))
                ? Directory.EnumerateFiles(Path.Combine(root, "backups", "automatic"))
                : []);
            Assert.Single(dialog.Confirmations);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task ResetAll_AfterVerifiedBackup_LoadsEmptyConfiguration()
    {
        using var context = new RuntimeTestContext();
        var root = Path.Combine(Path.GetTempPath(), $"SwitchBoard-reset-all-{Guid.NewGuid():N}");
        try
        {
            var catalog = new SwitchBoardCatalog { Profiles = [new ProfileDefinition { Name = "Delete" }] };
            using var main = CreateMain(context, catalog, new UserSettings(), new AppDataPaths(root));

            await main.ResetAllDataAsync();

            Assert.Empty(main.AllProfiles);
            Assert.Empty(main.Categories);
            var safetyBackup = Assert.Single(Directory.EnumerateFiles(
                Path.Combine(root, "backups", "automatic"), "SwitchBoard-safety-*.sbbackup"));
            await new SwitchBoardBackupService().ImportPackageAsync(safetyBackup);
            Assert.Contains(safetyBackup, main.StatusMessage);
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void SearchPresentation_DoesNotModifyProfileStructure()
    {
        using var context = new RuntimeTestContext();
        var category = new CategoryDefinition { Name = "Games" };
        var match = new ProfileDefinition { Name = "Counter Strike", CategoryId = category.Id, SortOrder = 0 };
        var other = new ProfileDefinition { Name = "Work", CategoryId = category.Id, SortOrder = 1 };
        using var main = CreateMain(context, new SwitchBoardCatalog { Categories = [category], Profiles = [match, other] },
            new UserSettings(), new AppDataPaths(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));

        main.ProfileSearchText = "counter";

        Assert.Equal(new[] { match.Id, other.Id }, main.AllProfiles.Select(profile => profile.Id));
        Assert.Equal(new[] { match.Id, other.Id }, main.Categories.Single().Profiles.Select(profile => profile.Id));
        Assert.Equal(new[] { match.Id }, main.Categories.Single().VisibleProfiles.Select(profile => profile.Id));
        Assert.Single(main.FilteredRootNavigationItems.OfType<CategoryItemViewModel>());
    }

    [Fact]
    [Trait("Category", "Regression")]
    public void AddCategory_ShowsEmptyCategoryInProfileNavigation()
    {
        using var context = new RuntimeTestContext();
        var existing = new CategoryDefinition { Name = "Games" };
        using var main = CreateMain(context, new SwitchBoardCatalog { Categories = [existing] }, new UserSettings(),
            new AppDataPaths(Path.Combine(Path.GetTempPath(), $"SwitchBoard-add-category-{Guid.NewGuid():N}")));

        var initialCount = main.Categories.Count;
        main.AddCategoryCommand.Execute(null);

        var added = Assert.Single(main.Categories.Skip(initialCount));
        Assert.Contains(added, main.FilteredRootNavigationItems);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ProfileAppearanceCommands_UpdateOnlyPresentationFieldsAndKeepLegacyDefaults()
    {
        using var context = new RuntimeTestContext();
        var profile = new ProfileDefinition { Name = "Games" };
        using var main = CreateMain(context, new SwitchBoardCatalog { Profiles = [profile] }, new UserSettings(),
            new AppDataPaths(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));

        main.SetProfileColorCommand.Execute("#4F8EF7");
        main.SetProfileIconCommand.Execute("gamepad");

        Assert.Equal("#4F8EF7", main.SelectedProfile!.Color);
        Assert.Equal("gamepad", main.SelectedProfile.Icon);
        Assert.Equal("#4F8EF7", main.SelectedProfile.ToModel().Color);
        Assert.Equal("gamepad", main.SelectedProfile.ToModel().Icon);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ActivityFilters_ChangeOnlyThePresentation()
    {
        using var context = new RuntimeTestContext();
        var activity = new ActivityService();
        var profile = new ProfileDefinition { Name = "Gaming" };
        activity.Add(ActivityLevel.Success, "Done", profile.Id);
        activity.Add(ActivityLevel.Error, "Failed", profile.Id);
        using var main = CreateMain(context, new SwitchBoardCatalog { Profiles = [profile] }, new UserSettings(),
            new AppDataPaths(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))), activity);

        main.ActivityStatusFilter = "error";

        Assert.Single(main.ActivityDisplayEntries);
        Assert.Equal(2, activity.Records.Count);
        main.ClearActivityFiltersCommand.Execute(null);
        Assert.Equal(2, main.ActivityDisplayEntries.Count);
        Assert.Equal(2, activity.Records.Count);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ActivityFilters_FindProcessNameFromTheAssociatedAction()
    {
        using var context = new RuntimeTestContext();
        var action = new ActionDefinition
        {
            Type = ActionTypeIds.ProcessSetState,
            Parameters = new JsonObject { [ActionParameterNames.ProcessName] = "notepad.exe" }
        };
        var profile = new ProfileDefinition { Name = "Praca", Actions = [action] };
        var activity = new ActivityService();
        activity.Add(ActivityLevel.Success, "Action completed", profile.Id, action.Id);
        using var main = CreateMain(context, new SwitchBoardCatalog { Profiles = [profile] }, new UserSettings(),
            new AppDataPaths(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))), activity);

        main.ActivitySearchText = "NOTEPAD";

        Assert.Single(main.ActivityDisplayEntries);
        Assert.Single(activity.Records);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ActivityFilters_FindEveryProcessShownInHistoryActions()
    {
        using var context = new RuntimeTestContext();
        var profile = new ProfileDefinition { Name = "Gaming" };
        var actionId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        var activity = new ActivityService();
        activity.Record(new PersistentActivityRecord
        {
            SessionId = sessionId,
            ProfileId = profile.Id,
            ProfileName = profile.Name,
            ActionId = actionId,
            ActionType = ActionTypeIds.ProgramRun,
            FriendlyName = "Anydesk",
            EventType = ActivityEventTypes.Execute,
            Level = ActivityLevel.Success,
            Result = "success",
            Message = "Verified: Anydesk is running."
        });
        using var main = CreateMain(context, new SwitchBoardCatalog { Profiles = [profile] }, new UserSettings(),
            new AppDataPaths(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))), activity);

        main.ActivitySearchText = "andesk";

        Assert.Single(main.HistoryEntries);
        Assert.Single(activity.Records);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ActivityNavigation_UsesStableIdsAndReportsStaleTargets()
    {
        using var context = new RuntimeTestContext();
        var action = new ActionDefinition { Type = ActionTypeIds.Delay, Name = "Delay" };
        var profile = new ProfileDefinition { Name = "Gaming", Actions = [action] };
        using var main = CreateMain(context, new SwitchBoardCatalog { Profiles = [profile] }, new UserSettings(),
            new AppDataPaths(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));

        Assert.True(main.NavigateToProfileAction(profile.Id, action.Id));
        Assert.Equal(profile.Id, main.SelectedProfile?.Id);
        Assert.Equal(action.Id, main.SelectedAction?.Id);

        Assert.False(main.NavigateToProfileAction(profile.Id, Guid.NewGuid()));
        Assert.Equal("Activity.NavigationActionMissing", main.StatusMessage);
        Assert.False(main.NavigateToProfileAction(Guid.NewGuid(), action.Id));
        Assert.Equal("Activity.NavigationProfileMissing", main.StatusMessage);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ActivityTabs_RemoveLiveSubtabAndKeepLegacySelectionReadable()
    {
        using var context = new RuntimeTestContext();
        var settings = new UserSettings { LastActivityTabIndex = 0 };
        using var main = CreateMain(context, new SwitchBoardCatalog(), settings,
            new AppDataPaths(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"))));

        Assert.Equal(2, main.ActivityTabIndex);
        main.ActivityTabIndex = 1;
        Assert.Equal(1, main.ActivityTabIndex);
        main.ActivityTabIndex = 2;
        Assert.Equal(2, main.ActivityTabIndex);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void Preflight_UsesExistingValidationAndActionCapabilities()
    {
        var localization = new TestLocalizationService();
        var profile = new ProfileItemViewModel(new ProfileDefinition
        {
            Name = "Profile",
            Actions =
            [
                new ActionDefinition { Type = ActionTypeIds.ProcessConfigure,
                    Parameters = new JsonObject { [ActionParameterNames.ProcessName] = "process" } },
                new ActionDefinition { Type = ActionTypeIds.ScriptRun,
                    Parameters = new JsonObject
                    {
                        [ActionParameterNames.ScriptPath] = "script.ps1",
                        [ActionParameterNames.RunAsAdministrator] = true
                    } }
            ]
        }, localization);

        var result = new ProfilePreflightService().Analyze(profile, profileReferencesAreValid: true);

        Assert.True(result.HasErrors);
        Assert.Single(result.AdministratorActions);
        Assert.Equal("script.run", ActionDescriptorRegistry.Get(ActionTypeIds.ScriptRun)?.TypeId);
    }

    [Fact]
    [Trait("Category", "Regression")]
    public void Preflight_SkipsDescendantsOfDisabledCompositeActions()
    {
        var invalidNested = new ActionDefinition
        {
            Type = ActionTypeIds.ProcessConfigure,
            Parameters = new JsonObject { [ActionParameterNames.ProcessName] = "" }
        };
        var disabledCondition = new ActionDefinition
        {
            Type = ActionTypeIds.ConditionIf,
            IsEnabled = false,
            Parameters = new JsonObject
            {
                [ActionParameterNames.ConditionType] = ConditionTypeIds.FileExists,
                [ActionParameterNames.ConditionValue] = "never-used",
                [ActionParameterNames.ThenActions] = new JsonArray(JsonSerializer.SerializeToNode(invalidNested))
            }
        };
        var profile = new ProfileItemViewModel(new ProfileDefinition
        {
            Name = "Profile",
            Actions = [disabledCondition]
        }, new TestLocalizationService());

        var result = new ProfilePreflightService().Analyze(profile, profileReferencesAreValid: true);

        Assert.Equal(0, result.ReadyActionCount);
        Assert.Empty(result.Issues);
        Assert.False(result.RequiresAdministrator);
    }

    [Fact]
    [Trait("Category", "Regression")]
    public void Preflight_RecognizesAdministratorRequirementForRestoreScript()
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"SwitchBoard-script-{Guid.NewGuid():N}.ps1");
        var restoreScriptPath = Path.Combine(Path.GetTempPath(), $"SwitchBoard-restore-{Guid.NewGuid():N}.ps1");
        try
        {
            File.WriteAllText(scriptPath, "Write-Output ready");
            File.WriteAllText(restoreScriptPath, "Write-Output restore");
            var action = new ActionDefinition
            {
                Type = ActionTypeIds.ScriptRun,
                RestoreBehavior = ActionRestoreBehavior.RunRestoreScript,
                Parameters = new JsonObject
                {
                    [ActionParameterNames.ScriptPath] = scriptPath,
                    [ActionParameterNames.ScriptType] = ScriptTypeIds.PowerShell,
                    [ActionParameterNames.RestoreScriptPath] = restoreScriptPath,
                    [ActionParameterNames.RestoreScriptType] = ScriptTypeIds.PowerShell,
                    [ActionParameterNames.RestoreScriptRunAsAdministrator] = true
                }
            };
            var profile = new ProfileItemViewModel(new ProfileDefinition
            {
                Name = "Profile",
                Actions = [action]
            }, new TestLocalizationService());

            var result = new ProfilePreflightService().Analyze(profile, profileReferencesAreValid: true);

            Assert.True(result.RequiresAdministrator);
            Assert.Single(result.AdministratorActions);
        }
        finally
        {
            try { File.Delete(scriptPath); } catch { }
            try { File.Delete(restoreScriptPath); } catch { }
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void LegacyProfileAppearance_UsesSafeEmptyDefaults()
    {
        var legacy = JsonSerializer.Deserialize<ProfileDefinition>("{\"name\":\"Legacy\"}")!;
        var current = new ProfileDefinition { Name = "Current", Color = "#4F8EF7", Icon = "bolt" };

        Assert.Null(legacy.Color);
        Assert.Null(legacy.Icon);
        Assert.Equal("#4F8EF7", current.Color);
        Assert.Equal("bolt", current.Icon);
    }

    [Theory]
    [InlineData("v1.2.0", "1.1.0", UpdateCheckStatus.UpdateAvailable)]
    [InlineData("v1.2.0", "1.2.0", UpdateCheckStatus.UpToDate)]
    [InlineData("v1.1.0", "1.2.0", UpdateCheckStatus.UpToDate)]
    public async Task UpdateCheck_HandlesVersionComparison(string tag, string current, UpdateCheckStatus expected)
    {
        using var client = new HttpClient(new StubHandler(HttpStatusCode.OK,
            $$"""{"tag_name":"{{tag}}","html_url":"https://github.com/Karwo12/SwitchBoard/releases/tag/{{tag}}","draft":false,"prerelease":false}"""));
        var result = await new GitHubReleaseUpdateService(client).CheckAsync(Version.Parse(current));
        Assert.Equal(expected, result.Status);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task UpdateCheck_ConvertsNetworkFailureToResult()
    {
        using var client = new HttpClient(new ThrowingHandler());
        var result = await new GitHubReleaseUpdateService(client).CheckAsync(new Version(1, 0, 0));
        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void TrayDispose_IsIdempotentAndReleasesItsOwnedServiceState()
    {
        var icon = new TestTrayIcon();
        var tray = new SystemTrayService(() => { }, () => { }, () => [], () => false, _ => { }, () => { }, icon);
        tray.Dispose();
        tray.Dispose();
        Assert.True(tray.IsDisposed);
        Assert.True(icon.IsDisposed);
        Assert.False(icon.Visible);
    }

    private static MainWindowViewModel CreateMain(RuntimeTestContext context, SwitchBoardCatalog catalog,
        UserSettings settings, AppDataPaths paths, IActivityService? activity = null,
        IStartupRegistrationService? startupRegistrationService = null, TestDialogService? dialog = null) => new(
        new TestCatalogService(), dialog ?? new TestDialogService(), catalog, new TestThemeManager(), new TestLocalizationService(),
        new TestSettingsRepository(), settings, context.Runner,
        new ProfileRestoreRunner(context.Registry, context.SessionRepository), context.SessionRepository,
        new TestCompletionBehavior(), new TestDisplayManager(new("", "", "", 1, 1, 1, 32, 0, 0, 0, 0)),
        new TestCustomThemeEditorService(), activityService: activity, appDataPaths: paths,
        startupRegistrationService: startupRegistrationService);

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new HttpRequestException("offline");
    }

    private sealed class TestTrayIcon : ITrayIcon
    {
        private EventHandler? _doubleClick;
        public event EventHandler? DoubleClick
        {
            add => _doubleClick += value;
            remove => _doubleClick -= value;
        }
        public bool Visible { get; set; }
        public bool IsDisposed { get; private set; }
        public void AttachMenu(ContextMenuStrip menu) { }
        public void Dispose()
        {
            _doubleClick = null;
            IsDisposed = true;
        }
    }

    private sealed class TestStartupRegistrationService(bool enabled) : IStartupRegistrationService
    {
        public bool IsEnabled { get; private set; } = enabled;
        public List<bool> RequestedStates { get; } = [];

        public bool TrySetEnabled(bool enabled, out string? error)
        {
            error = null;
            RequestedStates.Add(enabled);
            IsEnabled = enabled;
            return true;
        }
    }
}
