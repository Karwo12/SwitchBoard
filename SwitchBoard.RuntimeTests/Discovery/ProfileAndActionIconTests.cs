using System.Collections.Concurrent;
using SwitchBoard.RuntimeTests.TestInfrastructure;

namespace SwitchBoard.RuntimeTests.Discovery;

[Collection("Windows runtime")]
public sealed class ProfileAndActionIconTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task FileIconCache_ReusesOneLoadedImageForMultipleActions()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"SwitchBoard-icon-{Guid.NewGuid():N}.exe");
        try
        {
            await File.WriteAllBytesAsync(sourcePath, [0]);
            var expected = CreateIcon();
            var loadCount = 0;
            var cache = new FileIconCache(16, _ =>
            {
                Interlocked.Increment(ref loadCount);
                return expected;
            });
            var localization = new TestLocalizationService();
            var first = ProgramAction(sourcePath, localization, cache);
            var second = ProgramAction(sourcePath, localization, cache);

            await TestHelpers.WaitUntilAsync(() => first.HasApplicationIcon && second.HasApplicationIcon);

            Assert.Same(expected, first.ApplicationIcon);
            Assert.Same(expected, second.ApplicationIcon);
            Assert.Equal(1, Volatile.Read(ref loadCount));
            Assert.Equal(1, cache.CachedEntryCount);
        }
        finally
        {
            try { File.Delete(sourcePath); } catch { }
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void PackagedActionFallback_ReusesOneFrozenImageAcrossCards()
    {
        var cache = new FileIconCache();
        var first = new ActionItemViewModel(new ActionDefinition
        {
            Type = ActionTypeIds.Delay,
            Parameters = new JsonObject()
        }, new TestLocalizationService(), iconCache: cache);
        var second = new ActionItemViewModel(new ActionDefinition
        {
            Type = ActionTypeIds.Delay,
            Parameters = new JsonObject()
        }, new TestLocalizationService(), iconCache: new FileIconCache());

        var firstIcon = Assert.IsAssignableFrom<BitmapSource>(first.ActionFallbackIcon);
        Assert.Same(firstIcon, second.ActionFallbackIcon);
        Assert.True(firstIcon.IsFrozen);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task FileIconCache_ExtractsAnEmbeddedWindowsExecutableIcon()
    {
        var taskManagerPath = Path.Combine(Environment.SystemDirectory, "taskmgr.exe");
        Assert.True(File.Exists(taskManagerPath), "Task Manager must exist on Windows.");

        var icon = await new FileIconCache().GetSmallIconAsync(taskManagerPath);

        Assert.NotNull(icon);
        Assert.True(icon!.CanFreeze || icon.IsFrozen);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task ExplicitProcessExecutablePath_FlowsFromResolverThroughCacheToViewModelAndRaisesBindings()
    {
        var taskManagerPath = Path.Combine(Environment.SystemDirectory, "taskmgr.exe");
        Assert.True(File.Exists(taskManagerPath), "Task Manager must exist on Windows.");
        var changedProperties = new ConcurrentQueue<string?>();
        var action = new ActionItemViewModel(new ActionDefinition
        {
            Type = ActionTypeIds.ProcessConfigure,
            Parameters = new JsonObject
            {
                [ActionParameterNames.ProcessName] = "taskmgr",
                [ActionParameterNames.ExecutablePath] = taskManagerPath
            }
        }, new TestLocalizationService(), iconCache: new FileIconCache());
        action.PropertyChanged += (_, eventArgs) => changedProperties.Enqueue(eventArgs.PropertyName);

        await TestHelpers.WaitUntilAsync(() => action.HasApplicationIcon, TimeSpan.FromSeconds(5));

        Assert.NotNull(action.ApplicationIcon);
        Assert.Contains(nameof(ActionItemViewModel.ApplicationIcon), changedProperties);
        Assert.Contains(nameof(ActionItemViewModel.HasApplicationIcon), changedProperties);
    }

    [EnvironmentFact("CurrentCatalogIconSmoke")]
    [Trait("Category", "Smoke")]
    [Trait("Platform", "Windows")]
    public async Task CurrentCatalog_ExecutableAndServiceActionsFlowAllTheWayToApplicationIcons()
    {
        var requestedProcesses = new[]
        {
            "hid", "AnyDesk", "RadeonSoftware", "VmConnect", "mmc", "Taskmgr", "RemoteMouse", "RemoteMouseCore"
        };
        using var repository = new JsonCatalogRepository(new AppDataPaths());
        var catalog = await repository.LoadAsync();
        var definitions = catalog.Profiles.SelectMany(profile => profile.Actions).ToList();
        var cache = new FileIconCache();
        var localization = new TestLocalizationService();

        foreach (var processName in requestedProcesses)
        {
            var definition = definitions.FirstOrDefault(action =>
                string.Equals(action.Parameters[ActionParameterNames.ProcessName]?.GetValue<string>(), processName,
                    StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(definition);

            var action = new ActionItemViewModel(definition!, localization, iconCache: cache);
            var sourcePath = await ActionIconSourceResolver.ResolveAsync(ActionIconSourceResolver.Capture(action));
            Assert.False(string.IsNullOrWhiteSpace(sourcePath), $"{processName} did not resolve an EXE icon source.");
            Assert.True(File.Exists(sourcePath), $"{processName} source does not exist: {sourcePath}");
            Assert.NotNull(await cache.GetSmallIconAsync(sourcePath));

            await TestHelpers.WaitUntilAsync(() => action.HasApplicationIcon, TimeSpan.FromSeconds(5),
                timeoutDetails: () => $"{processName} resolved {sourcePath}, but did not publish ApplicationIcon.");
            Assert.NotNull(action.ApplicationIcon);
        }

        foreach (var service in new[] { (Name: "AnyDesk", HasEmbeddedIcon: true),
                                        (Name: "RemoteMouseService", HasEmbeddedIcon: false) })
        {
            var definition = definitions.FirstOrDefault(action =>
                string.Equals(action.Parameters[ActionParameterNames.ServiceName]?.GetValue<string>(), service.Name,
                    StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(definition);

            var action = new ActionItemViewModel(definition!, localization, iconCache: cache);
            var sourcePath = await ActionIconSourceResolver.ResolveAsync(ActionIconSourceResolver.Capture(action));
            Assert.False(string.IsNullOrWhiteSpace(sourcePath), $"{service.Name} did not resolve an EXE ImagePath.");
            Assert.True(File.Exists(sourcePath), $"{service.Name} source does not exist: {sourcePath}");
            var icon = await cache.GetSmallIconAsync(sourcePath);

            if (service.HasEmbeddedIcon)
            {
                Assert.NotNull(icon);
                await TestHelpers.WaitUntilAsync(() => action.HasApplicationIcon, TimeSpan.FromSeconds(5),
                    timeoutDetails: () => $"{service.Name} resolved {sourcePath}, but did not publish ApplicationIcon.");
                Assert.NotNull(action.ApplicationIcon);
            }
            else
            {
                Assert.Null(icon);
                await TestHelpers.WaitUntilAsync(() => cache.TryGetCachedIcon(sourcePath, out var cached) && cached is null);
                Assert.False(action.HasApplicationIcon);
                Assert.Equal(ActionIconAsset.Service, action.FallbackIconAsset);
                Assert.NotNull(action.ActionFallbackIcon);
            }
        }
    }

    [EnvironmentFact("CurrentCatalogIconSmoke")]
    [Trait("Category", "Smoke")]
    [Trait("Platform", "Windows")]
    public async Task CurrentCatalog_ParallelInitialCardLoadsKeepTheirExecutableIcons()
    {
        var requestedActions = new[]
        {
            "Steam", "Crosshair X", "cs2", "Lexar RGB Sync Hid", "AnyDesk", "Google Chrome",
            "Menedżer zadań", "AMD Software: Host Application", "Microsoft Management Console", "Virtual Machine Connection"
        };
        using var repository = new JsonCatalogRepository(new AppDataPaths());
        var definitions = (await repository.LoadAsync()).Profiles.SelectMany(profile => profile.Actions).ToList();
        var cache = new FileIconCache();
        var actions = requestedActions.Select(name =>
        {
            var definition = definitions.FirstOrDefault(action => string.Equals(action.Name, name,
                StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(definition);
            return (Name: name, ViewModel: new ActionItemViewModel(definition!, new TestLocalizationService(), iconCache: cache));
        }).ToList();

        await TestHelpers.WaitUntilAsync(() => actions.All(item => item.ViewModel.HasApplicationIcon),
            TimeSpan.FromSeconds(8), timeoutDetails: () => string.Join(", ", actions
                .Where(item => !item.ViewModel.HasApplicationIcon).Select(item => item.Name)));

        foreach (var item in actions)
        {
            var sourcePath = await ActionIconSourceResolver.ResolveAsync(ActionIconSourceResolver.Capture(item.ViewModel));
            Assert.False(string.IsNullOrWhiteSpace(sourcePath), $"{item.Name} did not resolve an EXE source.");
            Assert.True(File.Exists(sourcePath), $"{item.Name} source does not exist: {sourcePath}");
            Assert.NotNull(item.ViewModel.ApplicationIcon);
        }
    }

    [EnvironmentFact("CurrentCatalogIconSmoke")]
    [Trait("Category", "Regression")]
    [Trait("Platform", "Windows")]
    public async Task CurrentCatalog_ActionCardLifecycle_PublishesToTheSameProfileActionAfterStatusRefresh()
    {
        using var repository = new JsonCatalogRepository(new AppDataPaths());
        var catalog = await repository.LoadAsync();
        var definition = catalog.Profiles.SelectMany(profile => profile.Actions).FirstOrDefault(action =>
            string.Equals(action.Name, "Google Chrome", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(definition);

        var extractionStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowExtraction = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = CreateIcon();
        var cache = new FileIconCache(loader: _ =>
        {
            extractionStarted.TrySetResult(true);
            allowExtraction.Task.GetAwaiter().GetResult();
            return expected;
        });
        var profile = new ProfileItemViewModel(new ProfileDefinition
        {
            Name = "Current catalog action-card lifecycle",
            Actions = [definition!]
        }, new TestLocalizationService(), cache);
        var displayedAction = Assert.Single(profile.Actions);

        try
        {
            await extractionStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            displayedAction.SetCurrentStatus("Running", "Status=Running", DateTimeOffset.UtcNow);
            Assert.Same(displayedAction, Assert.Single(profile.Actions));

            allowExtraction.TrySetResult(true);
            await TestHelpers.WaitUntilAsync(() => displayedAction.HasApplicationIcon, TimeSpan.FromSeconds(5));

            Assert.Same(expected, displayedAction.ApplicationIcon);
            Assert.True(displayedAction.HasApplicationIcon);
            Assert.Same(displayedAction, Assert.Single(profile.Actions));

            displayedAction.SetCurrentStatus("Stopped", "Status=Stopped", DateTimeOffset.UtcNow);
            Assert.Same(expected, displayedAction.ApplicationIcon);
            Assert.True(displayedAction.HasApplicationIcon);
        }
        finally
        {
            allowExtraction.TrySetResult(true);
        }
    }

    [EnvironmentFact("CurrentCatalogIconSmoke")]
    [Trait("Category", "Smoke")]
    [Trait("Platform", "Windows")]
    public async Task CurrentCatalog_ParallelFileIconCacheLoadsDoNotLoseEmbeddedExecutableIcons()
    {
        var paths = new[]
        {
            @"C:\Program Files (x86)\Steam\Steam.exe",
            @"C:\Program Files (x86)\Steam\steamapps\common\CrosshairX\CrosshairX.exe",
            @"C:\Program Files (x86)\Steam\steamapps\common\Counter-Strike Global Offensive\game\bin\win64\cs2.exe",
            @"C:\Program Files (x86)\Lexar RGB Sync\hid.exe",
            @"C:\Program Files (x86)\AnyDesk\AnyDesk.exe",
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            Path.Combine(Environment.SystemDirectory, "taskmgr.exe"),
            @"C:\Program Files\AMD\CNext\CNext\Radeonsoftware.exe",
            Path.Combine(Environment.SystemDirectory, "mmc.exe"),
            Path.Combine(Environment.SystemDirectory, "VmConnect.exe")
        };
        Assert.All(paths, path => Assert.True(File.Exists(path), path));

        var cache = new FileIconCache();
        var results = await Task.WhenAll(paths.Select(path => cache.GetSmallIconAsync(path)));

        Assert.All(results, Assert.NotNull);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task FileIconCache_UsesIntentionalFallbackWhenExecutableHasNoIcon()
    {
        var sourcePath = Path.Combine(Path.GetTempPath(), $"SwitchBoard-no-icon-{Guid.NewGuid():N}.exe");
        try
        {
            await File.WriteAllBytesAsync(sourcePath, [0]);

            var icon = await new FileIconCache().GetSmallIconAsync(sourcePath);

            Assert.Null(icon);
        }
        finally
        {
            try { File.Delete(sourcePath); } catch { }
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task MissingExplicitProcessExecutable_UsesProcessFallbackInsteadOfAnApplicationIcon()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"SwitchBoard-missing-icon-{Guid.NewGuid():N}.exe");
        var cache = new FileIconCache(loader: _ => throw new InvalidOperationException("A missing EXE must not be loaded."));
        var action = new ActionItemViewModel(new ActionDefinition
        {
            Type = ActionTypeIds.ProcessConfigure,
            Parameters = new JsonObject
            {
                [ActionParameterNames.ProcessName] = "missing-process",
                [ActionParameterNames.ExecutablePath] = missingPath
            }
        }, new TestLocalizationService(), iconCache: cache);

        await TestHelpers.WaitUntilAsync(() => cache.TryGetCachedIcon(missingPath, out _));

        Assert.False(action.HasApplicationIcon);
        Assert.Equal(ActionIconAsset.Process, action.FallbackIconAsset);
        Assert.NotNull(action.ActionFallbackIcon);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProfileIconSource_PersistsOnlyItsFileReference_AndMissingFileFallsBackSafely()
    {
        var root = Path.Combine(Path.GetTempPath(), $"SwitchBoard-profile-icon-{Guid.NewGuid():N}");
        var sourcePath = Path.Combine(root, "game.exe");
        try
        {
            Directory.CreateDirectory(root);
            await File.WriteAllBytesAsync(sourcePath, [0]);
            var paths = new AppDataPaths(root);
            var catalog = new SwitchBoardCatalog
            {
                Profiles =
                [
                    new ProfileDefinition
                    {
                        Name = "Counter-Strike 2",
                        IconSource = new ProfileIconSourceDefinition
                        {
                            Type = ProfileIconSourceDefinition.FileSourceType,
                            Path = sourcePath
                        }
                    }
                ]
            };

            using (var repository = new JsonCatalogRepository(paths))
            {
                await repository.SaveAsync(catalog);
                var reloaded = await repository.LoadAsync();
                var source = Assert.Single(reloaded.Profiles).IconSource;
                Assert.NotNull(source);
                Assert.Equal(ProfileIconSourceDefinition.FileSourceType, source!.Type);
                Assert.Equal(sourcePath, source.Path);
            }

            File.Delete(sourcePath);
            var missingSourceProfile = new ProfileItemViewModel(catalog.Profiles[0], new TestLocalizationService(),
                new FileIconCache(loader: _ => throw new InvalidOperationException("Missing file must not be loaded.")));

            await Task.Delay(50);
            Assert.Null(missingSourceProfile.IconImage);
            Assert.NotEmpty(missingSourceProfile.IconPathData);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProfileActionIconSource_PersistsItsStableActionId()
    {
        var root = Path.Combine(Path.GetTempPath(), $"SwitchBoard-profile-action-icon-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            var actionId = Guid.NewGuid();
            var paths = new AppDataPaths(root);
            var catalog = new SwitchBoardCatalog
            {
                Profiles =
                [
                    new ProfileDefinition
                    {
                        Name = "Counter-Strike 2",
                        Actions = [new ActionDefinition { Id = actionId, Type = ActionTypeIds.Delay }],
                        IconSource = new ProfileIconSourceDefinition
                        {
                            Type = ProfileIconSourceDefinition.ActionSourceType,
                            ActionId = actionId
                        }
                    }
                ]
            };

            using var repository = new JsonCatalogRepository(paths);
            await repository.SaveAsync(catalog);
            var reloaded = await repository.LoadAsync();
            var source = Assert.Single(reloaded.Profiles).IconSource;

            Assert.NotNull(source);
            Assert.Equal(ProfileIconSourceDefinition.ActionSourceType, source!.Type);
            Assert.Equal(actionId, source.ActionId);
            Assert.Null(source.Path);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProfileActionIconSource_ReusesTheSelectedActionImageAcrossChangesReorderAndRemoval()
    {
        var root = Path.Combine(Path.GetTempPath(), $"SwitchBoard-profile-action-icon-link-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            var firstPath = Path.Combine(root, "cs2.exe");
            var secondPath = Path.Combine(root, "cs2-new.exe");
            await File.WriteAllBytesAsync(firstPath, [0]);
            await File.WriteAllBytesAsync(secondPath, [0]);
            var actionId = Guid.NewGuid();
            var firstIcon = CreateIcon();
            var secondIcon = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null,
                new byte[] { 255, 127, 0, 255 }, 4);
            secondIcon.Freeze();
            var cache = new FileIconCache(loader: path => string.Equals(path, firstPath,
                StringComparison.OrdinalIgnoreCase) ? firstIcon : secondIcon);
            var profile = new ProfileItemViewModel(new ProfileDefinition
            {
                Name = "Counter-Strike 2",
                Actions =
                [
                    new ActionDefinition
                    {
                        Id = actionId,
                        Type = ActionTypeIds.ProgramRun,
                        Parameters = new JsonObject
                        {
                            [ActionParameterNames.Target] = firstPath,
                            [ActionParameterNames.TargetType] = TargetTypeIds.Executable
                        }
                    },
                    new ActionDefinition { Type = ActionTypeIds.Delay }
                ],
                IconSource = new ProfileIconSourceDefinition
                {
                    Type = ProfileIconSourceDefinition.ActionSourceType,
                    ActionId = actionId
                }
            }, new TestLocalizationService(), cache);
            var selectedAction = profile.Actions.Single(action => action.Id == actionId);

            await TestHelpers.WaitUntilAsync(() => ReferenceEquals(profile.IconImage, firstIcon));
            Assert.Same(selectedAction.ApplicationIcon, profile.IconImage);
            Assert.Equal(actionId, profile.IconSourceActionId);

            profile.Actions.Move(profile.Actions.IndexOf(selectedAction), 1);
            Assert.Equal(actionId, profile.IconSourceActionId);
            Assert.Same(firstIcon, profile.IconImage);

            selectedAction.Target = secondPath;
            await TestHelpers.WaitUntilAsync(() => ReferenceEquals(profile.IconImage, secondIcon));
            Assert.Same(selectedAction.ApplicationIcon, profile.IconImage);

            profile.SetIconSourcePath(firstPath);
            Assert.Equal(firstPath, profile.IconSourcePath);
            Assert.Null(profile.IconSourceActionId);
            profile.SetIconSourceAction(actionId);
            Assert.Null(profile.IconSourcePath);
            Assert.Equal(actionId, profile.IconSourceActionId);
            Assert.Same(secondIcon, profile.IconImage);

            profile.Actions.Remove(selectedAction);
            Assert.Equal(actionId, profile.IconSourceActionId);
            Assert.Null(profile.IconSourceAction);
            Assert.Null(profile.IconImage);
            var persistedSource = profile.ToModel().IconSource;
            Assert.NotNull(persistedSource);
            Assert.Equal(ProfileIconSourceDefinition.ActionSourceType, persistedSource!.Type);
            Assert.Equal(actionId, persistedSource.ActionId);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Theory]
    [InlineData(ActionTypeIds.Delay, "Delay")]
    [InlineData(ActionTypeIds.DisplayConfigure, "Display")]
    [InlineData(ActionTypeIds.AudioConfigure, "Audio")]
    [InlineData(ActionTypeIds.ServiceSetState, "Service")]
    [InlineData(ActionTypeIds.DeviceSetState, "Device")]
    [InlineData(ActionTypeIds.ScriptRun, "Script")]
    [InlineData(ActionTypeIds.ConditionIf, "Condition")]
    [InlineData(ActionTypeIds.PowerSetPlan, "Power")]
    [Trait("Category", "Unit")]
    public void ActionWithoutReliableExecutable_UsesItsPackagedTypeFallback(string type, string expectedAsset)
    {
        var action = new ActionItemViewModel(new ActionDefinition
        {
            Type = type,
            Parameters = new JsonObject()
        }, new TestLocalizationService());

        Assert.False(action.HasApplicationIcon);
        Assert.Equal(Enum.Parse<ActionIconAsset>(expectedAsset), action.FallbackIconAsset);
        Assert.NotNull(action.ActionFallbackIcon);
    }

    [Theory]
    [InlineData("StartOBS.cmd", "Command")]
    [InlineData("StartOBS.BAT", "Command")]
    [InlineData("StartOBS.ps1", "PowerShell")]
    [InlineData("StartOBS.py", "Script")]
    [Trait("Category", "Unit")]
    public void ScriptFallback_MapsExtensionsCaseInsensitively(string scriptPath, string expectedAsset)
    {
        var action = new ActionItemViewModel(new ActionDefinition
        {
            Type = ActionTypeIds.ScriptRun,
            Parameters = new JsonObject { [ActionParameterNames.ScriptPath] = scriptPath }
        }, new TestLocalizationService());

        Assert.Equal(Enum.Parse<ActionIconAsset>(expectedAsset), action.FallbackIconAsset);
        Assert.NotNull(action.ActionFallbackIcon);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void UnknownAction_UsesPackagedNeutralFallback()
    {
        var action = new ActionItemViewModel(new ActionDefinition
        {
            Type = "unknown.action.type",
            Parameters = new JsonObject()
        }, new TestLocalizationService());

        Assert.Equal(ActionIconAsset.Fallback, action.FallbackIconAsset);
        Assert.NotNull(action.ActionFallbackIcon);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void ProcessNameAndSteamUri_HaveSafeFallbacksWhenNoExecutableSourceIsAvailable()
    {
        var processAction = new ActionItemViewModel(new ActionDefinition
        {
            Type = ActionTypeIds.ProcessConfigure,
            Parameters = new JsonObject { [ActionParameterNames.ProcessName] = "not-a-running-switchboard-icon-process.exe" }
        }, new TestLocalizationService());
        var steamAction = new ActionItemViewModel(new ActionDefinition
        {
            Type = ActionTypeIds.ProgramRun,
            Parameters = new JsonObject
            {
                [ActionParameterNames.Target] = "steam://rungameid/730",
                [ActionParameterNames.TargetType] = TargetTypeIds.Uri
            }
        }, new TestLocalizationService());

        Assert.False(processAction.HasApplicationIcon);
        Assert.Equal(ActionIconAsset.Process, processAction.FallbackIconAsset);
        Assert.Equal(ActionIconAsset.Process, steamAction.FallbackIconAsset);
        Assert.NotNull(processAction.ActionFallbackIcon);
        Assert.NotNull(steamAction.ActionFallbackIcon);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task Resolver_PrefersExplicitExecutablePathAndSteamWorkingDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), $"SwitchBoard-icon-resolver-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(root);
            var explicitPath = Path.Combine(root, "crosshair.exe");
            var steamPath = Path.Combine(root, "cs2.exe");
            await File.WriteAllBytesAsync(explicitPath, [0]);
            await File.WriteAllBytesAsync(steamPath, [0]);

            var explicitSource = await ActionIconSourceResolver.ResolveAsync(new ActionIconSourceRequest(
                ActionTypeIds.ProgramRun, "steam://rungameid/1366800", TargetTypeIds.Uri, explicitPath,
                "CrosshairX", root, string.Empty));
            var steamWorkingDirectorySource = await ActionIconSourceResolver.ResolveAsync(new ActionIconSourceRequest(
                ActionTypeIds.ProgramRun, "steam://rungameid/730", TargetTypeIds.Uri, string.Empty,
                "cs2", root, string.Empty));

            Assert.Equal(explicitPath, explicitSource);
            Assert.Equal(steamPath, steamWorkingDirectorySource);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static ActionItemViewModel ProgramAction(string sourcePath, ILocalizationService localization,
        FileIconCache cache) => new(new ActionDefinition
        {
            Type = ActionTypeIds.ProgramRun,
            Parameters = new JsonObject
            {
                [ActionParameterNames.Target] = sourcePath,
                [ActionParameterNames.TargetType] = TargetTypeIds.Executable
            }
        }, localization, iconCache: cache);

    private static BitmapSource CreateIcon()
    {
        var source = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 127, 255, 255 }, 4);
        source.Freeze();
        return source;
    }
}
