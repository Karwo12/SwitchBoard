using System.Diagnostics;
using System.IO;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Text.Json.Serialization;
using SwitchBoard.Models.Actions;
using SwitchBoard.Models.Execution;
using SwitchBoard.Models.Profiles;
using SwitchBoard.Services.Execution;
using SwitchBoard.Services.Execution.Handlers;
using SwitchBoard.Services.Windows;
using SwitchBoard.Localization;
using SwitchBoard.ViewModels;
using SwitchBoard.Data;
using SwitchBoard.Services.Persistence;
using SwitchBoard.Services;
using SwitchBoard.Models.Categories;
using SwitchBoard.Services.Profiles;
using SwitchBoard.Themes;
using SwitchBoard.Services.Logging;
using System.Windows.Media;
using System.Windows.Media.Imaging;

var failures = new List<string>();

static ActionDefinition Action(string type, JsonObject parameters, ActionFailurePolicy failurePolicy = ActionFailurePolicy.Continue) =>
    new() { Type = type, Parameters = parameters, FailurePolicy = failurePolicy };

static void Check(bool condition, string name, List<string> failures)
{
    Console.WriteLine($"{(condition ? "PASS" : "FAIL")} {name}");
    if (!condition) failures.Add(name);
}

var serviceManager = new WindowsServiceManager();
var powerManager = new WindowsPowerPlanManager();
var displayManager = new WindowsDisplayManager();
var testRoot = Path.Combine(Path.GetTempPath(), $"SwitchBoard-runtime-{Guid.NewGuid():N}");
Directory.CreateDirectory(testRoot);
var testAppDataRoot = Path.Combine(testRoot, "appdata");
using var sessionRepository = new JsonExecutionSessionRepository(new AppDataPaths(testAppDataRoot));
var restoreOrder = new List<string>();
var reversibleHandler = new TestReversibleHandler(restoreOrder, sessionRepository);
var registry = new ActionRegistry(
[
    new ProgramRunActionHandler(),
    new ProcessSetStateActionHandler(),
    new ServiceSetStateActionHandler(serviceManager),
    new PowerSetPlanActionHandler(powerManager),
    new ScriptRunActionHandler(),
    new DelayActionHandler(),
    reversibleHandler
]);
var runner = new ProfileRunner(registry, sessionRepository);

try
{
    var outputPath = Path.Combine(testRoot, "script-output.txt");
    var successScript = Path.Combine(testRoot, "success.ps1");
    await File.WriteAllTextAsync(successScript, "param([string]$Value)\nSet-Content -LiteralPath $env:SB_TEST_OUTPUT -Value $Value\nexit 0\n");
    Environment.SetEnvironmentVariable("SB_TEST_OUTPUT", outputPath);
    var scriptSuccess = Action(ActionTypeIds.ScriptRun, new JsonObject
    {
        [ActionParameterNames.ScriptPath] = successScript,
        [ActionParameterNames.ScriptType] = ScriptTypeIds.AutoDetect,
        [ActionParameterNames.Arguments] = "\"argument with spaces\"",
        [ActionParameterNames.WorkingDirectory] = testRoot,
        [ActionParameterNames.WaitForExit] = true
    });
    var successResult = await new ScriptRunActionHandler().ExecuteAsync(scriptSuccess, new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
    Check(successResult.IsSuccessful && File.Exists(outputPath) && (await File.ReadAllTextAsync(outputPath)).Trim() == "argument with spaces",
        "script.run PowerShell wait/arguments/exit 0", failures);

    var batchOutputPath = Path.Combine(testRoot, "batch-output.txt");
    var batchScript = Path.Combine(testRoot, "success.cmd");
    await File.WriteAllTextAsync(batchScript, "@echo off\r\n>\"%SB_TEST_BATCH_OUTPUT%\" echo %~1\r\nexit /b 0\r\n");
    Environment.SetEnvironmentVariable("SB_TEST_BATCH_OUTPUT", batchOutputPath);
    var batchResult = await new ScriptRunActionHandler().ExecuteAsync(
        Action(ActionTypeIds.ScriptRun, new JsonObject
        {
            [ActionParameterNames.ScriptPath] = batchScript,
            [ActionParameterNames.ScriptType] = ScriptTypeIds.BatchCmd,
            [ActionParameterNames.Arguments] = "\"batch argument\"",
            [ActionParameterNames.WaitForExit] = true
        }), new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
    Check(batchResult.IsSuccessful && File.Exists(batchOutputPath) && (await File.ReadAllTextAsync(batchOutputPath)).Trim() == "batch argument",
        "script.run Batch/CMD quoting and exit 0", failures);

    var failedScript = Path.Combine(testRoot, "failure.ps1");
    await File.WriteAllTextAsync(failedScript, "exit 1\n");
    var profile = new ProfileDefinition
    {
        CategoryId = Guid.NewGuid(),
        Name = "Failure policy runtime test",
        Actions =
        [
            Action(ActionTypeIds.ScriptRun, new JsonObject
            {
                [ActionParameterNames.ScriptPath] = failedScript,
                [ActionParameterNames.ScriptType] = ScriptTypeIds.PowerShell,
                [ActionParameterNames.WaitForExit] = true
            }),
            Action(ActionTypeIds.Delay, new JsonObject { [ActionParameterNames.DelaySeconds] = 0 })
        ]
    };
    profile.Actions[0].SortOrder = 0;
    profile.Actions[1].SortOrder = 1;
    var failedSession = await runner.RunAsync(profile);
    Check(failedSession.Status == ExecutionSessionStatus.CompletedWithErrors &&
          failedSession.Journal[0].Status == ActionJournalStatus.Failed &&
          failedSession.Journal[1].Status == ActionJournalStatus.Success,
        "ProfileRunner continues and reports CompletedWithErrors", failures);

    var reversibleProfile = new ProfileDefinition
    {
        CategoryId = Guid.NewGuid(), Name = "Persistent restore test",
        Actions =
        [
            Action(TestReversibleHandler.TypeId, new JsonObject { ["key"] = "first" }),
            Action(TestReversibleHandler.TypeId, new JsonObject { ["key"] = "second" })
        ]
    };
    for (var index = 0; index < reversibleProfile.Actions.Count; index++)
    {
        reversibleProfile.Actions[index].SortOrder = index;
        reversibleProfile.Actions[index].RestoreBehavior = ActionRestoreBehavior.RestorePreviousState;
    }
    var reversibleSession = await runner.RunAsync(reversibleProfile);
    var pending = await new JsonExecutionSessionRepository(new AppDataPaths(Path.Combine(testRoot, "appdata")))
        .GetLatestPendingAsync(reversibleProfile.Id);
    Check(reversibleSession.Status == ExecutionSessionStatus.Completed && pending?.PendingRestoreCount == 2,
        "session survives repository restart with two pending changes", failures);
    Check(reversibleHandler.CaptureWasPersistedBeforeExecute,
        "CaptureState is persisted before Execute", failures);
    if (pending is not null)
    {
        await new ProfileRestoreRunner(registry, sessionRepository).RunAsync(pending);
        Check(restoreOrder.SequenceEqual(["second", "first"]), "Restore runs in reverse action order", failures);
        var restored = await sessionRepository.LoadAsync(pending.SessionId);
        Check(restored?.Status == PersistentSessionStatus.Restored && restored.PendingRestoreCount == 0,
            "successful Restore atomically clears pending actions", failures);
    }

    var partialProfile = new ProfileDefinition
    {
        CategoryId = Guid.NewGuid(), Name = "Partial restore test",
        Actions =
        [
            Action(TestReversibleHandler.TypeId, new JsonObject { ["key"] = "partial-first", ["failOnce"] = true }),
            Action(TestReversibleHandler.TypeId, new JsonObject { ["key"] = "partial-second" })
        ]
    };
    for (var index = 0; index < partialProfile.Actions.Count; index++)
    {
        partialProfile.Actions[index].SortOrder = index;
        partialProfile.Actions[index].RestoreBehavior = ActionRestoreBehavior.RestorePreviousState;
    }
    await runner.RunAsync(partialProfile);
    var partial = await sessionRepository.GetLatestPendingAsync(partialProfile.Id);
    if (partial is not null)
    {
        var restoreRunner = new ProfileRestoreRunner(registry, sessionRepository);
        partial = await restoreRunner.RunAsync(partial);
        Check(partial.Status == PersistentSessionStatus.PartiallyRestored && partial.PendingRestoreCount == 1,
            "partial Restore keeps only failed action pending", failures);
        partial = await restoreRunner.RunAsync(partial);
        Check(partial.Status == PersistentSessionStatus.Restored &&
              reversibleHandler.RestoreAttempts["partial-second"] == 1 &&
              reversibleHandler.RestoreAttempts["partial-first"] == 2,
            "Restore retry skips actions already restored", failures);
    }

    var recoverySession = new PersistentExecutionSession
    {
        ProfileId = Guid.NewGuid(), ProfileName = "Interrupted", Status = PersistentSessionStatus.Executing,
        Actions = [new PersistentSessionAction
        {
            ActionId = Guid.NewGuid(), ActionType = TestReversibleHandler.TypeId, RequiresRestore = true,
            PreviousState = new JsonObject { ["key"] = "recovery" }, RestoreStatus = PersistentActionRestoreStatus.Pending
        }]
    };
    await sessionRepository.SaveAsync(recoverySession);
    await sessionRepository.MaintainAsync(TimeSpan.FromDays(30));
    var recovered = await sessionRepository.LoadAsync(recoverySession.SessionId);
    Check(recovered?.Status == PersistentSessionStatus.RecoveryRequired && recovered.PendingRestoreCount == 1,
        "startup recovery preserves interrupted pending state", failures);

    var oldRestored = new PersistentExecutionSession
    {
        ProfileId = Guid.NewGuid(), ProfileName = "Old restored", Status = PersistentSessionStatus.Restored
    };
    await sessionRepository.SaveAsync(oldRestored);
    var oldPath = Path.Combine(testAppDataRoot, "sessions", $"{oldRestored.SessionId:N}.json");
    var oldJson = JsonNode.Parse(await File.ReadAllTextAsync(oldPath))!.AsObject();
    oldJson["updatedAt"] = DateTimeOffset.UtcNow.AddDays(-40).ToString("O");
    await File.WriteAllTextAsync(oldPath, oldJson.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    await sessionRepository.MaintainAsync(TimeSpan.FromDays(30));
    Check(!File.Exists(oldPath), "session cleanup removes only old fully restored sessions", failures);

    var settingsRepository = new JsonSettingsRepository(new AppDataPaths(Path.Combine(testRoot, "settings-appdata")));
    var customSettings = new UserSettings
    {
        ThemeId = ThemeIds.Custom, LanguageId = "pl",
        CustomTheme = CustomThemeSettings.CreateDefault()
    };
    customSettings.CustomTheme.Accent = "#FF123456";
    customSettings.CustomTheme.BackgroundAssetFileName = "background-test.gif";
    await settingsRepository.SaveAsync(customSettings);
    var customReloaded = await settingsRepository.LoadAsync();
    Check(customReloaded.ThemeId == ThemeIds.Custom && customReloaded.CustomTheme.Accent == "#FF123456" &&
          customReloaded.CustomTheme.BackgroundAssetFileName == "background-test.gif",
        "Custom Theme colors and local background asset persist", failures);
    settingsRepository.Dispose();
    var logPaths = new AppDataPaths(Path.Combine(testRoot, "logging-appdata"));
    var testLogger = new RollingFileLogger(logPaths);
    testLogger.Info("Regression", "technical log smoke test");
    Check(File.Exists(Path.Combine(logPaths.LogsDirectory, "switchboard.log")),
        "rotating technical log is created in LocalAppData layout", failures);

    var themeTestResults = new List<(bool Passed, string Name)>();
    var themeThread = new Thread(() =>
    {
        var app = new System.Windows.Application();
        try
        {
            var themePaths = new AppDataPaths(Path.Combine(testRoot, "theme-appdata"));
            Directory.CreateDirectory(themePaths.CustomThemeDirectory);
            CreateTestImages(themePaths.CustomThemeDirectory);
            var manager = new ThemeManager(themePaths);
            foreach (var theme in manager.AvailableThemes.Where(item => item.Id != ThemeIds.Custom))
            {
                manager.ApplyTheme(theme.Id);
                var required = new[] { "BackgroundBrush", "SurfaceBrush", "CardSurfaceBrush", "ElevatedSurfaceBrush",
                    "BorderBrush", "TextPrimaryBrush", "TextSecondaryBrush", "PrimaryButtonBackground",
                    "PrimaryButtonForeground", "IconPrimary", "IconAccent", "IconMuted" };
                var complete = required.All(key => app.TryFindResource(key) is Brush);
                var readable = app.TryFindResource("PrimaryButtonBackground") is SolidColorBrush button &&
                               app.TryFindResource("PrimaryButtonForeground") is SolidColorBrush text &&
                               ContrastRatio(button.Color, text.Color) >= 3.0;
                themeTestResults.Add((complete && readable, $"theme contract and primary button contrast: {theme.Id}"));
            }
            foreach (var asset in new[] { "test.jpg", "test.png", "test.bmp", "test.gif" })
            {
                var custom = CustomThemeSettings.CreateDefault();
                custom.BackgroundAssetFileName = asset;
                manager.ApplyTheme(ThemeIds.Custom, custom);
                themeTestResults.Add((string.Equals(app.TryFindResource("CustomBackgroundPath") as string,
                    Path.Combine(themePaths.CustomThemeDirectory, asset), StringComparison.OrdinalIgnoreCase),
                    $"Custom background resource: {Path.GetExtension(asset)}"));
            }
            using var gifStream = File.OpenRead(Path.Combine(themePaths.CustomThemeDirectory, "test.gif"));
            var gifDecoder = new GifBitmapDecoder(gifStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            themeTestResults.Add((gifDecoder.Frames.Count == 2, "animated GIF decodes and caches two frames"));
        }
        finally { app.Shutdown(); }
    });
    themeThread.SetApartmentState(ApartmentState.STA);
    themeThread.Start();
    themeThread.Join();
    foreach (var result in themeTestResults) Check(result.Passed, result.Name, failures);

    var undo = new UndoService<string>(75, TimeSpan.FromSeconds(2));
    undo.Record("before typing", "field:name", true);
    undo.Record("after N", "field:name", true);
    undo.Record("after No", "field:name", true);
    Check(undo.Count == 1 && undo.TryUndo(out var undoState) && undoState == "before typing",
        "Undo stack coalesces consecutive TextBox edits", failures);

    var configurationCatalog = new SwitchBoardCatalog
    {
        Categories = [new CategoryDefinition { Name = "Category", SortOrder = 0 }]
    };
    configurationCatalog.Profiles.Add(new ProfileDefinition
    {
        CategoryId = configurationCatalog.Categories[0].Id, Name = "Profile", SortOrder = 0,
        Actions =
        [
            Action(ActionTypeIds.ProgramRun, new JsonObject { [ActionParameterNames.Target] = "one.exe" }),
            Action(ActionTypeIds.Delay, new JsonObject { [ActionParameterNames.DelaySeconds] = 1 })
        ]
    });
    configurationCatalog.Profiles[0].Actions[0].SortOrder = 0;
    configurationCatalog.Profiles[0].Actions[1].SortOrder = 1;
    var originalCategoryId = configurationCatalog.Categories[0].Id;
    var originalProfileId = configurationCatalog.Profiles[0].Id;
    var originalActionId = configurationCatalog.Profiles[0].Actions[0].Id;
    var testLocalization = new TestLocalizationService();
    var testCatalogService = new TestCatalogService();
    var main = new MainWindowViewModel(testCatalogService, new TestDialogService(), configurationCatalog,
        new TestThemeManager(), testLocalization, new TestSettingsRepository(),
        new UserSettings { ThemeId = ThemeIds.Graphite, LanguageId = "en" }, runner,
        new ProfileRestoreRunner(registry, sessionRepository), sessionRepository,
        new TestCompletionBehavior(), new TestDisplayManager(new("", "", "", 1, 1, 1, 32, 0, 0, 0, 0)),
        new TestCustomThemeEditorService());

    var initialActionCount = main.SelectedProfile!.Actions.Count;
    main.AddActionCommand.Execute(null);
    main.UndoCommand.Execute(null);
    Check(main.SelectedProfile!.Actions.Count == initialActionCount, "Undo add action", failures);
    var initialProfileCount = main.Profiles.Count;
    main.AddProfileCommand.Execute(null);
    main.UndoCommand.Execute(null);
    Check(main.Profiles.Count == initialProfileCount, "Undo add profile", failures);
    var initialCategoryCount = main.Categories.Count;
    main.AddCategoryCommand.Execute(null);
    main.UndoCommand.Execute(null);
    Check(main.Categories.Count == initialCategoryCount, "Undo add category", failures);
    var actionToDelete = main.SelectedProfile.Actions.First(item => item.Id == originalActionId);
    main.DeleteActionCommand.Execute(actionToDelete);
    main.UndoCommand.Execute(null);
    Check(main.SelectedProfile!.Actions.Any(item => item.Id == originalActionId), "Undo delete action keeps GUID", failures);
    var originalProfileName = main.SelectedProfile!.Name;
    main.SelectedProfile.Name = "Renamed profile";
    main.UndoCommand.Execute(null);
    Check(main.SelectedProfile!.Name == originalProfileName, "Undo profile rename", failures);
    var originalCategoryName = main.SelectedCategory!.Name;
    main.SelectedCategory.Name = "Renamed category";
    main.UndoCommand.Execute(null);
    Check(main.SelectedCategory!.Name == originalCategoryName, "Undo category rename", failures);
    var renamed = main.SelectedProfile.Actions.First(item => item.Id == originalActionId);
    var oldActionName = renamed.Name;
    renamed.Name = "Renamed action";
    main.UndoCommand.Execute(null);
    Check(main.SelectedProfile!.Actions.First(item => item.Id == originalActionId).Name == oldActionName, "Undo action name", failures);

    void ChangeAndUndo(Action<ActionItemViewModel> change, Func<ActionItemViewModel, bool> restored, string testName)
    {
        var currentAction = main.SelectedProfile!.Actions.First(item => item.Id == originalActionId);
        change(currentAction);
        main.UndoCommand.Execute(null);
        Check(restored(main.SelectedProfile!.Actions.First(item => item.Id == originalActionId)), testName, failures);
    }
    ChangeAndUndo(item => item.Target = "changed.exe", item => item.Target == "one.exe", "Undo action target");
    ChangeAndUndo(item => item.TimeoutSeconds = 42, item => item.TimeoutSeconds == 0, "Undo timeout");
    ChangeAndUndo(item => item.FailurePolicyId = "stop", item => item.FailurePolicyId == "continue", "Undo failure policy");
    ChangeAndUndo(item => item.RestoreBehaviorId = "previous", item => item.RestoreBehaviorId == "none", "Undo restore behavior");
    ChangeAndUndo(item => item.IsEnabled = false, item => item.IsEnabled, "Undo enable/disable action");
    var firstAction = main.SelectedProfile!.Actions.First(item => item.Id == originalActionId);
    main.MoveActionDownCommand.Execute(firstAction);
    main.UndoCommand.Execute(null);
    Check(main.SelectedProfile.Actions[0].Id == originalActionId, "Undo action reorder", failures);

    main.DeleteProfileCommand.Execute(main.SelectedProfile);
    main.UndoCommand.Execute(null);
    Check(main.Profiles.Any(item => item.Id == originalProfileId), "Undo delete profile keeps children", failures);
    main.DeleteCategoryCommand.Execute(main.SelectedCategory);
    main.UndoCommand.Execute(null);
    Check(main.Categories.Any(item => item.Id == originalCategoryId) && main.Profiles.Any(item => item.Id == originalProfileId),
        "Undo delete category keeps profiles and GUIDs", failures);

    var currentProfile = main.SelectedProfile!;
    currentProfile.Name = "First change";
    main.SelectedProfile!.Actions.First(item => item.Id == originalActionId).TimeoutSeconds = 9;
    main.UndoCommand.Execute(null);
    main.UndoCommand.Execute(null);
    Check(main.SelectedProfile!.Name == "Profile" &&
          main.SelectedProfile.Actions.First(item => item.Id == originalActionId).TimeoutSeconds == 0 &&
          !main.UndoCommand.CanExecute(null), "multiple Ctrl+Z command steps and disabled empty Undo", failures);
    main.SelectedProfile.Actions.First(item => item.Id == originalActionId).Target = "saved-change.exe";
    main.SaveCommand.Execute(null);
    for (var wait = 0; wait < 20 && main.HasUnsavedChanges; wait++) await Task.Delay(25);
    main.UndoCommand.Execute(null);
    Check(main.SelectedProfile.Actions.First(item => item.Id == originalActionId).Target == "one.exe" && main.HasUnsavedChanges,
        "Undo remains available after Save", failures);
    Check(testCatalogService.Saved.Categories.Any(item => item.Id == originalCategoryId) &&
          testCatalogService.Saved.Profiles.Any(item => item.Id == originalProfileId &&
              item.Actions.Any(action => action.Id == originalActionId)),
        "category/profile/action Save snapshot is restart-safe", failures);

    using (var notepad = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true }))
    {
        if (notepad is not null)
        {
            await Task.Delay(500);
            var processAction = Action(ActionTypeIds.ProcessSetState, new JsonObject
            {
                [ActionParameterNames.ProcessName] = "notepad",
                [ActionParameterNames.DesiredState] = ProcessDesiredStateIds.Stopped
            });
            var handler = new ProcessSetStateActionHandler();
            var first = await handler.ExecuteAsync(processAction, new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
            await Task.Delay(750);
            var second = await handler.ExecuteAsync(processAction, new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
            Check(first.IsSuccessful && !first.IsSkipped, "process.setState stops Notepad", failures);
            Check(second.IsSuccessful && second.IsSkipped, "process.setState skips absent process", failures);
        }
        else
        {
            failures.Add("Notepad could not start");
        }
    }

    var powershellPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
    var preexistingInfo = new ProcessStartInfo(powershellPath) { UseShellExecute = false };
    preexistingInfo.ArgumentList.Add("-NoProfile");
    preexistingInfo.ArgumentList.Add("-Command");
    preexistingInfo.ArgumentList.Add("Start-Sleep -Seconds 30");
    using (var preexisting = Process.Start(preexistingInfo))
    {
        if (preexisting is not null)
        {
            await Task.Delay(350);
            var skipExisting = await new ProgramRunActionHandler().ExecuteAsync(
                Action(ActionTypeIds.ProgramRun, new JsonObject
                {
                    [ActionParameterNames.Target] = powershellPath,
                    [ActionParameterNames.StartOnlyIfNotAlreadyRunning] = true
                }), new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
            Check(skipExisting.IsSkipped, "program.run already-running detection skips duplicate", failures);
            var programAction = Action(ActionTypeIds.ProgramRun, new JsonObject
            {
                [ActionParameterNames.Target] = powershellPath,
                [ActionParameterNames.Arguments] = "-NoProfile -Command \"Start-Sleep -Seconds 30\"",
                [ActionParameterNames.StartOnlyIfNotAlreadyRunning] = false
            });
            programAction.RestoreBehavior = ActionRestoreBehavior.CloseIfStartedBySwitchBoard;
            var programProfile = new ProfileDefinition
            {
                CategoryId = Guid.NewGuid(), Name = "Program restore test", Actions = [programAction]
            };
            var programSession = await runner.RunAsync(programProfile);
            var programPending = await sessionRepository.GetLatestPendingAsync(programProfile.Id);
            var launchedPid = programPending?.Actions[0].PreviousState?["processId"]?.GetValue<int>() ?? 0;
            Check(programSession.Status == ExecutionSessionStatus.Completed && launchedPid > 0 && launchedPid != preexisting.Id && !preexisting.HasExited,
                "program.run records only the instance started by SwitchBoard", failures);
            if (programPending is not null)
            {
                await new ProfileRestoreRunner(registry, sessionRepository).RunAsync(programPending);
                await Task.Delay(250);
                var launchedStillAlive = true;
                try { using var launched = Process.GetProcessById(launchedPid); launchedStillAlive = !launched.HasExited; }
                catch (ArgumentException) { launchedStillAlive = false; }
                Check(!launchedStillAlive && !preexisting.HasExited,
                    "program.run Restore closes exact PID and preserves pre-existing instance", failures);
            }
            if (!preexisting.HasExited) preexisting.Kill();
        }
    }

    var powershellInfo = new ProcessStartInfo(powershellPath) { UseShellExecute = false };
    powershellInfo.ArgumentList.Add("-NoProfile");
    powershellInfo.ArgumentList.Add("-Command");
    powershellInfo.ArgumentList.Add("Start-Sleep -Seconds 30");
    using (var powershell = Process.Start(powershellInfo))
    {
        if (powershell is not null)
        {
            await Task.Delay(350);
            var processAction = Action(ActionTypeIds.ProcessSetState, new JsonObject
            {
                [ActionParameterNames.ProcessName] = "powershell",
                [ActionParameterNames.ExecutablePath] = powershellPath,
                [ActionParameterNames.DesiredState] = ProcessDesiredStateIds.Stopped
            });
            processAction.RuntimeProcessIdHint = powershell.Id;
            var powershellProfile = new ProfileDefinition
            {
                CategoryId = Guid.NewGuid(), Name = "PowerShell process runtime test",
                Actions = [processAction, Action(ActionTypeIds.Delay, new JsonObject { [ActionParameterNames.DelaySeconds] = 0 })]
            };
            powershellProfile.Actions[0].SortOrder = 0;
            powershellProfile.Actions[1].SortOrder = 1;
            var powershellSession = await runner.RunAsync(powershellProfile);
            Check(powershellSession.Status == ExecutionSessionStatus.Completed &&
                  powershellSession.Journal[0].Status == ActionJournalStatus.Success &&
                  powershellSession.Journal[1].Status == ActionJournalStatus.Success && powershell.HasExited,
                "process.setState PowerShell kill remains Success and profile continues", failures);
        }
    }

    var restoreScriptMarker = Path.Combine(testRoot, "restore-script-marker.txt");
    var mainRestoreScript = Path.Combine(testRoot, "main-for-restore.ps1");
    var cleanupScript = Path.Combine(testRoot, "cleanup.ps1");
    await File.WriteAllTextAsync(mainRestoreScript, $"Set-Content -LiteralPath '{restoreScriptMarker.Replace("'", "''")}' -Value created\nexit 0\n");
    await File.WriteAllTextAsync(cleanupScript, $"Remove-Item -LiteralPath '{restoreScriptMarker.Replace("'", "''")}' -Force -ErrorAction SilentlyContinue\nexit 0\n");
    var scriptWithRestore = Action(ActionTypeIds.ScriptRun, new JsonObject
    {
        [ActionParameterNames.ScriptPath] = mainRestoreScript,
        [ActionParameterNames.ScriptType] = ScriptTypeIds.PowerShell,
        [ActionParameterNames.WaitForExit] = true,
        [ActionParameterNames.RestoreScriptPath] = cleanupScript,
        [ActionParameterNames.RestoreScriptType] = ScriptTypeIds.PowerShell,
        [ActionParameterNames.RestoreScriptWaitForExit] = true
    });
    scriptWithRestore.RestoreBehavior = ActionRestoreBehavior.RunRestoreScript;
    var scriptRestoreProfile = new ProfileDefinition
    {
        CategoryId = Guid.NewGuid(), Name = "Restore script test", Actions = [scriptWithRestore]
    };
    await runner.RunAsync(scriptRestoreProfile);
    var scriptPending = await sessionRepository.GetLatestPendingAsync(scriptRestoreProfile.Id);
    Check(File.Exists(restoreScriptMarker) && scriptPending?.PendingRestoreCount == 1,
        "script.run saves explicit restore script configuration", failures);
    if (scriptPending is not null) await new ProfileRestoreRunner(registry, sessionRepository).RunAsync(scriptPending);
    Check(!File.Exists(restoreScriptMarker), "Restore Script executes and verifies exit code", failures);

    var notepadRestorePath = Path.Combine(Environment.SystemDirectory, "notepad.exe");
    using (var restorableNotepad = Process.Start(new ProcessStartInfo(notepadRestorePath) { UseShellExecute = true }))
    {
        if (restorableNotepad is not null)
        {
            await Task.Delay(500);
            try { notepadRestorePath = restorableNotepad.MainModule?.FileName ?? notepadRestorePath; }
            catch (Exception) { }
            var processRestoreAction = Action(ActionTypeIds.ProcessSetState, new JsonObject
            {
                [ActionParameterNames.ProcessName] = "notepad",
                [ActionParameterNames.ExecutablePath] = notepadRestorePath,
                [ActionParameterNames.DesiredState] = ProcessDesiredStateIds.Stopped
            });
            processRestoreAction.RuntimeProcessIdHint = restorableNotepad.Id;
            processRestoreAction.RestoreBehavior = ActionRestoreBehavior.RestartIfWasRunning;
            var processRestoreProfile = new ProfileDefinition
            {
                CategoryId = Guid.NewGuid(), Name = "Process restart test", Actions = [processRestoreAction]
            };
            await runner.RunAsync(processRestoreProfile);
            var processPending = await sessionRepository.GetLatestPendingAsync(processRestoreProfile.Id);
            Check(restorableNotepad.HasExited && processPending?.PendingRestoreCount == 1,
                "process.setState captures executable before stopping", failures);
            if (processPending is not null) await new ProfileRestoreRunner(registry, sessionRepository).RunAsync(processPending);
            await Task.Delay(500);
            var restarted = Process.GetProcessesByName("notepad");
            Check(restarted.Length > 0, "process.setState Restore restarts EXE when it was running", failures);
            foreach (var process in restarted) { try { process.Kill(); } catch { } finally { process.Dispose(); } }
        }
    }

    var plans = await powerManager.GetPlansAsync();
    var originalPlan = await powerManager.GetActivePlanAsync();
    Check(plans.Count > 0 && plans.Any(plan => plan.Id == originalPlan), "power plan discovery and active plan", failures);
    foreach (var plan in plans) Console.WriteLine($"POWER {plan.DisplayName} | {plan.GuidText} | Active={plan.IsActive}");
    Check(plans.All(plan => !string.IsNullOrWhiteSpace(plan.DisplayName) &&
                            !string.Equals(plan.DisplayName, plan.GuidText, StringComparison.OrdinalIgnoreCase)),
        "power plan friendly names", failures);
    var capturedPower = await new PowerSetPlanActionHandler(powerManager).CaptureStateAsync(
        Action(ActionTypeIds.PowerSetPlan, []), new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
    Check(Guid.TryParse(capturedPower?["previousPowerPlanGuid"]?.GetValue<string>(), out var capturedPowerId) &&
          capturedPowerId == originalPlan, "power.setPlan CaptureState reads active plan", failures);
    var alternate = plans.FirstOrDefault(plan => plan.Id != originalPlan);
    if (alternate is not null)
    {
        var changedSuccessfully = false;
        try
        {
            var powerResult = await new PowerSetPlanActionHandler(powerManager).ExecuteAsync(
                Action(ActionTypeIds.PowerSetPlan, new JsonObject { [ActionParameterNames.PowerPlanGuid] = alternate.Id.ToString("D") }),
                new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
            changedSuccessfully = powerResult.IsSuccessful && await powerManager.GetActivePlanAsync() == alternate.Id;
            if (powerResult.IsSuccessful)
            {
                Check(changedSuccessfully, "power.setPlan changes and verifies active plan", failures);
            }
            else
            {
                Console.WriteLine($"LIMIT power plan change not permitted: {powerResult.Message}");
            }
        }
        finally
        {
            if (changedSuccessfully)
            {
                await powerManager.SetActivePlanAsync(originalPlan);
                Check(await powerManager.GetActivePlanAsync() == originalPlan, "power plan restored after test", failures);
            }
        }
    }
    else
    {
        Console.WriteLine("SKIP power.setPlan change: only one power plan is available.");
    }

    var services = await serviceManager.GetServicesAsync();
    Check(services.Count > 0, "Windows service discovery", failures);
    var runningService = services.FirstOrDefault(service => service.Status == "Running");
    if (runningService is not null)
    {
        var serviceHandler = new ServiceSetStateActionHandler(serviceManager);
        var capturedService = await serviceHandler.CaptureStateAsync(
            Action(ActionTypeIds.ServiceSetState, new JsonObject { [ActionParameterNames.ServiceName] = runningService.ServiceName }),
            new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
        Check(capturedService?["previousState"]?.GetValue<string>() == ServiceDesiredStateIds.Running,
            "service.setState CaptureState reads current state", failures);
        var skipped = await serviceManager.SetStateAsync(runningService.ServiceName, ServiceDesiredStateIds.Running, TimeSpan.FromSeconds(5));
        Check(skipped.IsSuccessful && skipped.IsSkipped, "service.setState Running -> Start is Skipped", failures);
    }

    var safeService = services.FirstOrDefault(service => string.Equals(service.ServiceName, "WerSvc", StringComparison.OrdinalIgnoreCase));
    if (safeService is not null)
    {
        var initialState = safeService.Status == "Running" ? ServiceDesiredStateIds.Running : ServiceDesiredStateIds.Stopped;
        var changedState = initialState == ServiceDesiredStateIds.Running ? ServiceDesiredStateIds.Stopped : ServiceDesiredStateIds.Running;
        var changed = await serviceManager.SetStateAsync(safeService.ServiceName, changedState, TimeSpan.FromSeconds(15));
        if (changed.IsSuccessful)
        {
            var restored = await serviceManager.SetStateAsync(safeService.ServiceName, initialState, TimeSpan.FromSeconds(15));
            Check(restored.IsSuccessful, "service.setState safe stop/start and restore", failures);
        }
        else
        {
            Console.WriteLine($"LIMIT service stop/start not permitted: {changed.Message}");
        }
    }

    var advancedAction = new ActionItemViewModel(new ActionDefinition
    {
        Type = ActionTypeIds.ServiceSetState,
        Timeout = TimeSpan.FromSeconds(17),
        FailurePolicy = ActionFailurePolicy.Stop,
        RestoreBehavior = ActionRestoreBehavior.RestorePreviousState,
        Parameters = new JsonObject { [ActionParameterNames.DesiredState] = ServiceDesiredStateIds.Unchanged }
    }, new TestLocalizationService());
    var advancedRoundTrip = advancedAction.ToModel();
    var advancedJson = JsonSerializer.Serialize(advancedRoundTrip);
    var advancedReloaded = JsonSerializer.Deserialize<ActionDefinition>(advancedJson);
    Check(!advancedAction.IsAdvancedOptionsExpanded && advancedReloaded?.Timeout == TimeSpan.FromSeconds(17) &&
          advancedReloaded.FailurePolicy == ActionFailurePolicy.Stop &&
          advancedReloaded.RestoreBehavior == ActionRestoreBehavior.RestorePreviousState &&
          !advancedJson.Contains("AdvancedOptions", StringComparison.OrdinalIgnoreCase),
        "advanced options default collapsed and values persist", failures);
    var validationLocalization = new TestLocalizationService();
    var invalidProgram = new ActionItemViewModel(Action(ActionTypeIds.ProgramRun, []), validationLocalization);
    var delayWithoutRestore = new ActionItemViewModel(Action(ActionTypeIds.Delay,
        new JsonObject { [ActionParameterNames.DelaySeconds] = 2 }), validationLocalization);
    var invalidRestoreScript = new ActionItemViewModel(Action(ActionTypeIds.ScriptRun,
        new JsonObject { [ActionParameterNames.ScriptPath] = successScript }), validationLocalization)
    { RestoreBehaviorId = "restoreScript" };
    Check(!invalidProgram.IsValid && !invalidProgram.SupportsRestore && !delayWithoutRestore.SupportsRestore &&
          !invalidRestoreScript.IsValid,
        "inline validation blocks obvious errors and Delay has no Restore", failures);

    var displays = await displayManager.GetDisplaysAsync();
    foreach (var display in displays)
    {
        Console.WriteLine($"DISPLAY {display.MonitorNumber}: {display.DisplayName} | {display.DeviceId} | {display.CurrentModeText} | Primary={display.IsPrimary} | Modes={display.Modes.Count}");
    }
    Check(displays.Count > 0, "display monitor discovery", failures);
    Check(displays.All(display => display.Modes.Count > 0), "display supported mode discovery", failures);
    if (displays.FirstOrDefault() is { } nativeDisplay)
    {
        var nativeState = await displayManager.GetCurrentStateAsync(nativeDisplay.DeviceId, nativeDisplay.DeviceName);
        try
        {
            await displayManager.ApplyTemporaryAsync(nativeState);
            var nativeVerified = await displayManager.GetCurrentStateAsync(nativeDisplay.DeviceId, nativeDisplay.DeviceName);
            Check(nativeVerified.Width == nativeState.Width && nativeVerified.Height == nativeState.Height &&
                  nativeVerified.RefreshRate == nativeState.RefreshRate,
                "display native apply/verify of current safe mode", failures);
            await displayManager.RestoreAsync(nativeState);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"LIMIT native display change rejected by the current session: {exception.Message}");
        }
    }

    var simulatedPrevious = new DisplayModeState("DISPLAY-TEST", "MONITOR-TEST", "Test monitor", 1920, 1080, 60, 32, 0, 0, 0, 0);
    var simulatedTargetAction = Action(ActionTypeIds.DisplayConfigure, new JsonObject
    {
        [ActionParameterNames.DisplayDeviceName] = simulatedPrevious.DeviceName,
        [ActionParameterNames.DisplayDeviceId] = simulatedPrevious.DeviceId,
        [ActionParameterNames.DisplayName] = simulatedPrevious.DisplayName,
        [ActionParameterNames.DisplayWidth] = 2560,
        [ActionParameterNames.DisplayHeight] = 1440,
        [ActionParameterNames.DisplayRefreshRate] = 144
    });
    var simulatedKeepManager = new TestDisplayManager(simulatedPrevious);
    var simulatedKeep = await new DisplayConfigureActionHandler(simulatedKeepManager, new TestDisplayConfirmationService(true))
        .ExecuteAsync(simulatedTargetAction, new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
    Check(simulatedKeep.IsSuccessful && simulatedKeepManager.State.Width == 2560 && simulatedKeepManager.State.RefreshRate == 144,
        "display.configure verified Keep flow", failures);
    var simulatedRevertManager = new TestDisplayManager(simulatedPrevious);
    var simulatedRevert = await new DisplayConfigureActionHandler(simulatedRevertManager, new TestDisplayConfirmationService(false))
        .ExecuteAsync(simulatedTargetAction, new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
    Check(!simulatedRevert.IsSuccessful && simulatedRevertManager.State == simulatedPrevious,
        "display.configure verified Revert flow", failures);
    var simulatedTimeoutManager = new TestDisplayManager(simulatedPrevious);
    var simulatedTimeout = await new DisplayConfigureActionHandler(simulatedTimeoutManager, new TestDisplayConfirmationService(false, TimeSpan.FromMilliseconds(350)))
        .ExecuteAsync(simulatedTargetAction, new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
    Check(!simulatedTimeout.IsSuccessful && simulatedTimeoutManager.State == simulatedPrevious,
        "display.configure verified timeout Revert flow", failures);
    var simulatedRestoreManager = new TestDisplayManager(simulatedPrevious);
    var simulatedRestoreHandler = new DisplayConfigureActionHandler(simulatedRestoreManager, new TestDisplayConfirmationService(true));
    var simulatedCapturedState = await simulatedRestoreHandler.CaptureStateAsync(simulatedTargetAction,
        new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
    await simulatedRestoreHandler.ExecuteAsync(simulatedTargetAction, new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
    await simulatedRestoreHandler.RestoreAsync(simulatedTargetAction, simulatedCapturedState!,
        new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
    Check(simulatedRestoreManager.State == simulatedPrevious, "display.configure Restore verifies previous mode", failures);
    var testDisplay = displays.FirstOrDefault(display => display.Modes.Any(mode =>
        mode.Width != display.CurrentWidth || mode.Height != display.CurrentHeight || mode.RefreshRate != display.CurrentRefreshRate));
    if (testDisplay is not null)
    {
        var originalDisplayState = await displayManager.GetCurrentStateAsync(testDisplay.DeviceId, testDisplay.DeviceName);
        var alternateMode = testDisplay.Modes
            .Where(mode => mode.Width != originalDisplayState.Width || mode.Height != originalDisplayState.Height || mode.RefreshRate != originalDisplayState.RefreshRate)
            .OrderByDescending(mode => mode.Width == originalDisplayState.Width && mode.Height == originalDisplayState.Height)
            .ThenBy(mode => Math.Abs(mode.RefreshRate - originalDisplayState.RefreshRate))
            .First();
        var displayAction = Action(ActionTypeIds.DisplayConfigure, new JsonObject
        {
            [ActionParameterNames.DisplayDeviceName] = testDisplay.DeviceName,
            [ActionParameterNames.DisplayDeviceId] = testDisplay.DeviceId,
            [ActionParameterNames.DisplayName] = testDisplay.DisplayName,
            [ActionParameterNames.DisplayWidth] = alternateMode.Width,
            [ActionParameterNames.DisplayHeight] = alternateMode.Height,
            [ActionParameterNames.DisplayRefreshRate] = alternateMode.RefreshRate
        });
        try
        {
            var keepResult = await new DisplayConfigureActionHandler(displayManager, new TestDisplayConfirmationService(true))
                .ExecuteAsync(displayAction, new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
            if (keepResult.IsSuccessful)
            {
                var changedDisplayState = await displayManager.GetCurrentStateAsync(testDisplay.DeviceId, testDisplay.DeviceName);
                Check(changedDisplayState.Width == alternateMode.Width && changedDisplayState.Height == alternateMode.Height &&
                      changedDisplayState.RefreshRate == alternateMode.RefreshRate,
                    "display.configure apply and Keep", failures);
            }
            else
            {
                Console.WriteLine($"LIMIT display mode change not permitted: {keepResult.Message}");
            }
        }
        finally
        {
            await displayManager.PersistAsync(originalDisplayState);
        }

        var revertResult = await new DisplayConfigureActionHandler(displayManager, new TestDisplayConfirmationService(false))
            .ExecuteAsync(displayAction, new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
        var revertedState = await displayManager.GetCurrentStateAsync(testDisplay.DeviceId, testDisplay.DeviceName);
        Check(!revertResult.IsSuccessful && revertedState.Width == originalDisplayState.Width &&
              revertedState.Height == originalDisplayState.Height && revertedState.RefreshRate == originalDisplayState.RefreshRate,
            "display.configure Revert restores previous mode", failures);

        var timeoutResult = await new DisplayConfigureActionHandler(displayManager, new TestDisplayConfirmationService(false, TimeSpan.FromMilliseconds(350)))
            .ExecuteAsync(displayAction, new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
        var timeoutState = await displayManager.GetCurrentStateAsync(testDisplay.DeviceId, testDisplay.DeviceName);
        Check(!timeoutResult.IsSuccessful && timeoutState.Width == originalDisplayState.Width &&
              timeoutState.Height == originalDisplayState.Height && timeoutState.RefreshRate == originalDisplayState.RefreshRate,
            "display.configure automatic timeout Revert", failures);
    }
    else
    {
        Console.WriteLine("SKIP display apply/revert: no alternate display mode is exposed by the current session.");
    }
}
finally
{
    Environment.SetEnvironmentVariable("SB_TEST_OUTPUT", null);
    Environment.SetEnvironmentVariable("SB_TEST_BATCH_OUTPUT", null);
    try { Directory.Delete(testRoot, true); } catch { }
}

Console.WriteLine(failures.Count == 0 ? "RUNTIME TESTS PASSED" : $"RUNTIME TEST FAILURES: {string.Join(", ", failures)}");
return failures.Count == 0 ? 0 : 1;

static void CreateTestImages(string directory)
{
    var red = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
        Enumerable.Repeat(new byte[] { 0, 0, 255, 255 }, 4).SelectMany(pixel => pixel).ToArray(), 8);
    var blue = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
        Enumerable.Repeat(new byte[] { 255, 0, 0, 255 }, 4).SelectMany(pixel => pixel).ToArray(), 8);
    red.Freeze();
    blue.Freeze();

    SaveEncoder(new JpegBitmapEncoder(), [red], Path.Combine(directory, "test.jpg"));
    SaveEncoder(new PngBitmapEncoder(), [red], Path.Combine(directory, "test.png"));
    SaveEncoder(new BmpBitmapEncoder(), [red], Path.Combine(directory, "test.bmp"));
    SaveEncoder(new GifBitmapEncoder(), [red, blue], Path.Combine(directory, "test.gif"));
}

static void SaveEncoder(BitmapEncoder encoder, IReadOnlyList<BitmapSource> frames, string path)
{
    foreach (var frame in frames) encoder.Frames.Add(BitmapFrame.Create(frame));
    using var stream = File.Create(path);
    encoder.Save(stream);
}

static double ContrastRatio(Color first, Color second)
{
    static double Linear(byte channel)
    {
        var value = channel / 255d;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    static double Luminance(Color color) =>
        0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);

    var light = Math.Max(Luminance(first), Luminance(second));
    var dark = Math.Min(Luminance(first), Luminance(second));
    return (light + 0.05) / (dark + 0.05);
}

sealed class TestDisplayConfirmationService(bool result, TimeSpan? delay = null) : IDisplayConfirmationService
{
    public async Task<bool> ConfirmAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (delay is { } wait) await Task.Delay(wait, cancellationToken);
        return result;
    }
}

sealed class TestLocalizationService : ILocalizationService
{
    public IReadOnlyList<LanguageDefinition> AvailableLanguages =>
        [new("en", "English", new Uri("Localization/Strings.en.xaml", UriKind.Relative))];
    public string CurrentLanguageId => "en";
    public string DetectSystemLanguage() => "en";
    public string ApplyLanguage(string? languageId) => languageId ?? "en";
    public string GetString(string resourceKey) => resourceKey;
    public string Format(string resourceKey, params object?[] arguments) => $"{resourceKey}: {string.Join(", ", arguments)}";
}

sealed class TestCatalogService : IProfileCatalogService
{
    public SwitchBoardCatalog Saved { get; private set; } = SwitchBoardCatalog.Empty();
    public Task<SwitchBoardCatalog> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Saved);
    public Task SaveAsync(SwitchBoardCatalog catalog, CancellationToken cancellationToken = default)
    {
        Saved = JsonSerializer.Deserialize<SwitchBoardCatalog>(JsonSerializer.Serialize(catalog))!;
        return Task.CompletedTask;
    }
}

sealed class TestSettingsRepository : ISettingsRepository
{
    public Task<UserSettings> LoadAsync(CancellationToken cancellationToken = default) => Task.FromResult(new UserSettings());
    public Task SaveAsync(UserSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

sealed class TestThemeManager : IThemeManager
{
    public IReadOnlyList<ThemeDefinition> AvailableThemes =>
        [new(ThemeIds.Graphite, "Graphite", new Uri("Themes/GraphiteTheme.xaml", UriKind.Relative))];
    public string CurrentThemeId => ThemeIds.Graphite;
    public string ApplyTheme(string? themeId, CustomThemeSettings? customTheme = null) => ThemeIds.Graphite;
}

sealed class TestCustomThemeEditorService : ICustomThemeEditorService
{
    public CustomThemeSettings? Edit(CustomThemeSettings current, Action<CustomThemeSettings> livePreview) => null;
}

sealed class TestCompletionBehavior : IProfileCompletionBehavior
{
    public void HandleSuccessfulCompletion(ProfileDefinition profile) { }
}

sealed class TestDialogService : IUserDialogService
{
    public bool Confirm(string title, string message) => true;
    public string? SelectFile(string title, string filter, string? initialPath = null) => null;
    public SwitchBoard.Services.Discovery.ProcessCandidate? SelectProcess(string title) => null;
    public SwitchBoard.Services.Discovery.ServiceCandidate? SelectService(string title) => null;
    public SwitchBoard.Services.Discovery.PowerPlanCandidate? SelectPowerPlan(string title) => null;
    public SwitchBoard.Services.Discovery.DisplayCandidate? SelectDisplay(string title) => null;
    public SwitchBoard.Services.Discovery.ProgramCandidate? FindProgram(string title) => null;
}

sealed class TestDisplayManager(DisplayModeState initialState) : IDisplayManager
{
    public DisplayModeState State { get; private set; } = initialState;
    public Task<IReadOnlyList<SwitchBoard.Services.Discovery.DisplayCandidate>> GetDisplaysAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<SwitchBoard.Services.Discovery.DisplayCandidate>>([]);
    public Task<DisplayModeState> GetCurrentStateAsync(string deviceId, string deviceName, CancellationToken cancellationToken = default) =>
        Task.FromResult(State);
    public Task ApplyTemporaryAsync(DisplayModeState state, CancellationToken cancellationToken = default) { State = state; return Task.CompletedTask; }
    public Task PersistAsync(DisplayModeState state, CancellationToken cancellationToken = default) { State = state; return Task.CompletedTask; }
    public Task RestoreAsync(DisplayModeState state, CancellationToken cancellationToken = default) { State = state; return Task.CompletedTask; }
}

sealed class TestReversibleHandler(List<string> restoreOrder, IExecutionSessionRepository repository) : IReversibleActionHandler
{
    public const string TypeId = "test.reversible";
    public string ActionType => TypeId;
    public bool CaptureWasPersistedBeforeExecute { get; private set; } = true;
    public Dictionary<string, int> RestoreAttempts { get; } = [];
    private readonly HashSet<string> _failedOnce = [];

    public Task<JsonObject?> CaptureStateAsync(ActionDefinition action, ActionExecutionContext context,
        CancellationToken cancellationToken) => Task.FromResult<JsonObject?>(new JsonObject
        {
            ["key"] = action.Parameters["key"]?.GetValue<string>(),
            ["failOnce"] = action.Parameters["failOnce"]?.GetValue<bool>() ?? false
        });

    public async Task<ActionExecutionResult> ExecuteAsync(ActionDefinition action, ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var session = await repository.LoadAsync(context.SessionId, cancellationToken);
        var item = session?.Actions.SingleOrDefault(candidate => candidate.ActionId == action.Id);
        CaptureWasPersistedBeforeExecute &= item?.PreviousState is not null && item.ExecutionStatus == PersistentActionExecutionStatus.Running;
        return ActionExecutionResult.Success();
    }

    public Task RestoreAsync(ActionDefinition action, JsonObject restoreState, ActionExecutionContext context,
        CancellationToken cancellationToken)
    {
        var key = restoreState["key"]?.GetValue<string>() ?? string.Empty;
        restoreOrder.Add(key);
        RestoreAttempts[key] = RestoreAttempts.GetValueOrDefault(key) + 1;
        if ((restoreState["failOnce"]?.GetValue<bool>() ?? false) && _failedOnce.Add(key))
            throw new InvalidOperationException("Simulated first restore failure.");
        return Task.CompletedTask;
    }
}
