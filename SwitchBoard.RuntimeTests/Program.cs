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

if (args is ["--program-run-tree-helper", var childPidPath])
{
    var helperPowerShellPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
    var childInfo = new ProcessStartInfo(helperPowerShellPath) { UseShellExecute = false };
    childInfo.ArgumentList.Add("-NoProfile");
    childInfo.ArgumentList.Add("-Command");
    childInfo.ArgumentList.Add("Start-Sleep -Seconds 60");
    using var child = Process.Start(childInfo);
    if (child is null) return 2;
    await File.WriteAllTextAsync(childPidPath, child.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    await Task.Delay(1000);
    return 0;
}

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
    var persistedTheme = new CustomThemeDefinition { Name = "First custom theme" };
    persistedTheme.Colors.Accent = "#FF123456";
    persistedTheme.Colors.SecondaryButtonBackground = "#FF223344";
    persistedTheme.Colors.MenuBackground = "#FF334455";
    persistedTheme.Colors.MenuHoverBackground = "#FF445566";
    persistedTheme.Colors.SurfaceOpacity = 0.72;
    persistedTheme.Colors.CategoriesPanelOpacity = 0.51;
    persistedTheme.Colors.ProfilesPanelOpacity = 0.63;
    persistedTheme.Colors.ProfileEditorPanelOpacity = 0.84;
    persistedTheme.Colors.BackgroundAssetFileName = "background-test.gif";
    var customSettings = new UserSettings
    {
        ThemeId = persistedTheme.Id, LanguageId = "pl", CustomThemes = [persistedTheme]
    };
    await settingsRepository.SaveAsync(customSettings);
    var customReloaded = await settingsRepository.LoadAsync();
    Check(customReloaded.ThemeId == persistedTheme.Id && customReloaded.CustomThemes.Count == 1 &&
          customReloaded.CustomThemes[0].Name == "First custom theme" &&
          customReloaded.CustomThemes[0].Colors.Accent == "#FF123456" &&
          customReloaded.CustomThemes[0].Colors.SecondaryButtonBackground == "#FF223344" &&
          customReloaded.CustomThemes[0].Colors.MenuBackground == "#FF334455" &&
          customReloaded.CustomThemes[0].Colors.MenuHoverBackground == "#FF445566" &&
          Math.Abs(customReloaded.CustomThemes[0].Colors.SurfaceOpacity - 0.72) < 0.001 &&
          Math.Abs(customReloaded.CustomThemes[0].Colors.CategoriesPanelOpacity - 0.51) < 0.001 &&
          Math.Abs(customReloaded.CustomThemes[0].Colors.ProfilesPanelOpacity - 0.63) < 0.001 &&
          Math.Abs(customReloaded.CustomThemes[0].Colors.ProfileEditorPanelOpacity - 0.84) < 0.001 &&
          customReloaded.CustomThemes[0].Colors.BackgroundAssetFileName == "background-test.gif" &&
          customReloaded.CustomThemes[0].CreatedAt != default && customReloaded.CustomThemes[0].UpdatedAt != default,
        "Custom Theme collection, metadata, colors, and background persist", failures);
    var legacyOpacity = CustomThemeSettings.CreateDefault();
    legacyOpacity.Panel = "#80223344";
    legacyOpacity.MigrateSurfaceOpacityFromLegacyAlpha();
    Check(Math.Abs(legacyOpacity.SurfaceOpacity - 128d / 255) < 0.001 &&
          legacyOpacity.CategoriesPanelOpacity == legacyOpacity.SurfaceOpacity &&
          legacyOpacity.ProfilesPanelOpacity == legacyOpacity.SurfaceOpacity &&
          legacyOpacity.ProfileEditorPanelOpacity == legacyOpacity.SurfaceOpacity,
        "legacy custom surface alpha migrates to all opacity controls", failures);
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
            app.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
            {
                Source = new Uri("/SwitchBoard;component/Themes/BaseStyles.xaml", UriKind.Relative)
            });
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
                    "CategoriesSurfaceBrush", "ProfilesSurfaceBrush", "ProfileEditorSurfaceBrush",
                    "IconPrimary", "IconAccent", "IconMuted" };
                var complete = required.All(key => app.TryFindResource(key) is Brush);
                var readable = SemanticContrastIsAccessible(app);
                var rendered = RenderedSemanticControlsAreAccessible(app);
                var editable = manager.CreateEditableCopy(theme.Id);
                var editableColors = new[] { editable.Background, editable.Panel, editable.Card, editable.Elevated,
                    editable.Border, editable.PrimaryText, editable.SecondaryText, editable.Accent, editable.Hover,
                    editable.Selection, editable.PrimaryButtonBackground, editable.IconForeground };
                var canCopy = editableColors.All(value => CustomThemeColorItemViewModel.TryColor(value, out _));
                themeTestResults.Add((complete && readable && rendered && canCopy,
                    $"rendered theme contract and WCAG contrast: {theme.Id} " +
                    $"[complete={complete}, resources={readable}, controls={rendered}, copy={canCopy}]"));
            }
            foreach (var buttonColor in new[]
                     {
                         "#FFFFFFFF", "#FFE8E8EA", "#FF000000", "#FF24262B",
                         "#FF1473E6", "#FFFFE600", "#FFFFFF66", "#FF07101F"
                     })
            {
                var custom = CustomThemeSettings.CreateDefault();
                custom.Background = buttonColor;
                custom.PrimaryText = buttonColor;
                custom.SecondaryText = buttonColor;
                custom.Elevated = buttonColor;
                custom.PrimaryButtonBackground = buttonColor;
                custom.SecondaryButtonBackground = buttonColor;
                custom.MenuBackground = buttonColor;
                custom.MenuHoverBackground = buttonColor;
                custom.Hover = buttonColor;
                custom.Selection = buttonColor;
                custom.Accent = buttonColor;
                manager.ApplyTheme($"contrast-{buttonColor[3..]}", custom);
                var accentBackground = ((SolidColorBrush)app.TryFindResource("AccentBrush")!).Color;
                var accentForeground = ((SolidColorBrush)app.TryFindResource("AccentForegroundBrush")!).Color;
                themeTestResults.Add((SemanticContrastIsAccessible(app) &&
                                      ContrastRatio(accentBackground, accentForeground) >= 4.5,
                    $"WCAG semantic contrast matrix: {buttonColor}"));
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
            var transparentSurfaces = CustomThemeSettings.CreateDefault();
            transparentSurfaces.Background = "#FF102030";
            transparentSurfaces.Border = "#AAABCDEF";
            transparentSurfaces.SurfaceOpacity = 0.70;
            transparentSurfaces.CategoriesPanelOpacity = 0.25;
            transparentSurfaces.ProfilesPanelOpacity = 0.50;
            transparentSurfaces.ProfileEditorPanelOpacity = 0.85;
            transparentSurfaces.BackgroundOpacity = 0.37;
            manager.ApplyTheme("surface-opacity", transparentSurfaces);
            themeTestResults.Add((
                ((SolidColorBrush)app.TryFindResource("BackgroundBrush")!).Color == Color.FromArgb(255, 16, 32, 48) &&
                ((SolidColorBrush)app.TryFindResource("SurfaceBrush")!).Color.A == 178 &&
                ((SolidColorBrush)app.TryFindResource("CardSurfaceBrush")!).Color.A == 178 &&
                ((SolidColorBrush)app.TryFindResource("ElevatedSurfaceBrush")!).Color.A == 178 &&
                ((SolidColorBrush)app.TryFindResource("CategoriesSurfaceBrush")!).Color.A == 64 &&
                ((SolidColorBrush)app.TryFindResource("ProfilesSurfaceBrush")!).Color.A == 128 &&
                ((SolidColorBrush)app.TryFindResource("ProfileEditorSurfaceBrush")!).Color.A == 217 &&
                ((SolidColorBrush)app.TryFindResource("BorderBrush")!).Color.A == 170 &&
                Math.Abs((double)app.TryFindResource("CustomBackgroundOpacity")! - 0.37) < 0.001,
                "surface opacity changes only surface alpha and supports three independent main blocks"));
            using var gifStream = File.OpenRead(Path.Combine(themePaths.CustomThemeDirectory, "test.gif"));
            var gifDecoder = new GifBitmapDecoder(gifStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            themeTestResults.Add((gifDecoder.Frames.Count == 2, "animated GIF decodes and caches two frames"));

            var extreme = CustomThemeSettings.CreateDefault();
            extreme.Background = "#FFFFFFFF";
            extreme.Panel = "#FFFFFFFF";
            extreme.Card = "#FFFFFFFF";
            extreme.PrimaryText = "#FFFFFFFF";
            extreme.SecondaryText = "#FF000000";
            var liveApplyCount = 0;
            var editor = new SwitchBoard.Views.CustomThemeWindow(
                new CustomThemeEditRequest(CustomThemeEditMode.Add, "Extreme", extreme, [], "draft-live",
                    settings =>
                    {
                        liveApplyCount++;
                        manager.ApplyTemporary("draft-live", settings);
                    }),
                themePaths, new TestLocalizationService());
            var editorBackground = ((SolidColorBrush)editor.Resources["EditorBackgroundBrush"]).Color;
            var editorText = ((SolidColorBrush)editor.Resources["EditorTextBrush"]).Color;
            var editorInput = ((SolidColorBrush)editor.Resources["EditorInputBrush"]).Color;
            themeTestResults.Add((ContrastRatio(editorBackground, editorText) >= 12 &&
                                  ContrastRatio(editorInput, editorText) >= 10,
                "Custom Theme editor keeps fixed high-contrast dark resources for extreme theme colors"));
            editor.ViewModel.Colors.First(item => item.Key == "primaryText").Color = "#FF000000";
            editor.ViewModel.Colors.First(item => item.Key == "background").Color = "#FF000000";
            themeTestResults.Add((((SolidColorBrush)editor.Resources["EditorTextBrush"]).Color == editorText,
                "white/black live theme extremes cannot mutate the editor form colors"));
            themeTestResults.Add((liveApplyCount >= 2 && manager.CurrentThemeId == "draft-live" &&
                                  ((SolidColorBrush)app.TryFindResource("BackgroundBrush")!).Color == Colors.Black,
                "theme editor draft updates the real application resources live"));
            editor.ViewModel.SurfaceOpacityPercent = 68;
            editor.ViewModel.CategoriesPanelOpacityPercent = 31;
            themeTestResults.Add((Math.Abs(editor.ViewModel.Settings.SurfaceOpacity - 0.68) < 0.001 &&
                                  Math.Abs(editor.ViewModel.Settings.CategoriesPanelOpacity - 0.31) < 0.001 &&
                                  Math.Abs(editor.ViewModel.Settings.ProfilesPanelOpacity - 0.68) < 0.001 &&
                                  ((SolidColorBrush)app.TryFindResource("CategoriesSurfaceBrush")!).Color.A == 79 &&
                                  ((SolidColorBrush)app.TryFindResource("ProfilesSurfaceBrush")!).Color.A == 173,
                "global and per-column opacity sliders live-apply independently"));
            var originalName = editor.ViewModel.Name;
            editor.ViewModel.Colors.First(item => item.Key == "accent").Color = "#FFFF0000";
            editor.ViewModel.Reset();
            themeTestResults.Add((editor.ViewModel.Name == originalName &&
                                  editor.ViewModel.Settings.Accent == extreme.Accent,
                "Reset changes only the current editor form values and preserves its name"));
            editor.Close();
            var pickerEvents = new List<Color>();
            var picker = new SwitchBoard.Views.ThemeColorPickerWindow(Colors.White, new TestLocalizationService(),
                color => pickerEvents.Add(color));
            var pickerBackground = ((SolidColorBrush)picker.Background).Color;
            var pickerText = ((SolidColorBrush)picker.Foreground).Color;
            themeTestResults.Add((ContrastRatio(pickerBackground, pickerText) >= 12,
                "ARGB color picker has a fixed high-contrast dark appearance"));
            ((System.Windows.Controls.Slider)picker.FindName("Red")).Value = 16;
            var livePickerUpdate = pickerEvents.LastOrDefault().R == 16;
            ((System.Windows.Controls.Button)picker.FindName("CancelButton")).RaiseEvent(
                new System.Windows.RoutedEventArgs(System.Windows.Controls.Button.ClickEvent));
            themeTestResults.Add((livePickerUpdate && pickerEvents.LastOrDefault() == Colors.White,
                "ARGB picker live-applies slider changes and Cancel restores its color snapshot"));
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
    var themeEditor = new TestCustomThemeEditorService();
    var themeManager = new TestThemeManager();
    var themeSettingsRepository = new TestSettingsRepository();
    var editableSettings = new UserSettings { ThemeId = ThemeIds.Graphite, LanguageId = "en" };
    var main = new MainWindowViewModel(testCatalogService, new TestDialogService(), configurationCatalog,
        themeManager, testLocalization, themeSettingsRepository,
        editableSettings, runner,
        new ProfileRestoreRunner(registry, sessionRepository), sessionRepository,
        new TestCompletionBehavior(), new TestDisplayManager(new("", "", "", 1, 1, 1, 32, 0, 0, 0, 0)),
        themeEditor);

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

    var addCancelCount = editableSettings.CustomThemes.Count;
    var savesBeforeCancelledAdd = themeSettingsRepository.SaveCount;
    themeEditor.EditActions.Enqueue(request =>
    {
        var liveDraft = request.Colors.Clone();
        liveDraft.Background = "#FFFF00FF";
        request.ApplyTemporary?.Invoke(liveDraft);
    });
    themeEditor.Results.Enqueue(null);
    main.AddThemeCommand.Execute(null);
    await WaitUntilAsync(() => main.AddThemeCommand.CanExecute(null));
    Check(editableSettings.CustomThemes.Count == addCancelCount &&
          editableSettings.ThemeId == ThemeIds.Graphite && themeManager.CurrentThemeId == ThemeIds.Graphite &&
          themeManager.TemporaryApplyCount >= 2 && themeSettingsRepository.SaveCount == savesBeforeCancelledAdd,
        "Custom Theme add draft live-applies and Cancel leaves no collection item", failures);

    var whiteTheme = CustomThemeSettings.CreateDefault();
    whiteTheme.Background = "#FFFFFFFF";
    whiteTheme.PrimaryText = "#FFFFFFFF";
    themeEditor.Results.Enqueue(new("Snow", whiteTheme));
    main.AddThemeCommand.Execute(null);
    await WaitUntilAsync(() => editableSettings.CustomThemes.Count == 1);
    var snowOption = main.ThemeOptions.Single(item => item.IsCustom);
    Check(snowOption.DisplayName == "Snow" && snowOption.IsActive && editableSettings.ThemeId == snowOption.Id,
        "Custom Theme create adds collection item and activates it", failures);
    Check(themeSettingsRepository.Saved.CustomThemes.Single().Name == "Snow",
        "Custom Theme create is persisted atomically", failures);

    var restartThemeManager = new TestThemeManager();
    restartThemeManager.ApplyTheme(themeSettingsRepository.Saved.ThemeId,
        themeSettingsRepository.Saved.CustomThemes.Single().Colors);
    var restartedMain = new MainWindowViewModel(testCatalogService, new TestDialogService(), configurationCatalog,
        restartThemeManager, testLocalization, new TestSettingsRepository(), themeSettingsRepository.Saved,
        runner, new ProfileRestoreRunner(registry, sessionRepository), sessionRepository,
        new TestCompletionBehavior(), new TestDisplayManager(new("", "", "", 1, 1, 1, 32, 0, 0, 0, 0)),
        new TestCustomThemeEditorService());
    Check(restartedMain.ThemeOptions.Any(item => item.IsCustom && item.DisplayName == "Snow") &&
          restartedMain.SelectedThemeOption?.DisplayName == "Snow",
        "Custom Theme collection reloads with active selection after restart", failures);

    var graphiteBeforeInactiveEdit = main.ThemeOptions.Single(item => item.Id == ThemeIds.Graphite);
    main.SelectedThemeOption = graphiteBeforeInactiveEdit;
    await WaitUntilAsync(() => editableSettings.ThemeId == ThemeIds.Graphite);
    var persistedSnowBeforeCancel = editableSettings.CustomThemes.Single(item => item.Id == snowOption.Id).Colors.Clone();
    themeEditor.EditActions.Enqueue(request =>
    {
        var liveDraft = request.Colors.Clone();
        liveDraft.Card = "#FFFF00FF";
        request.ApplyTemporary?.Invoke(liveDraft);
    });
    themeEditor.Results.Enqueue(null);
    main.EditThemeCommand.Execute(snowOption.Id);
    await WaitUntilAsync(() => main.EditThemeCommand.CanExecute(snowOption.Id));
    Check(editableSettings.ThemeId == ThemeIds.Graphite && themeManager.CurrentThemeId == ThemeIds.Graphite &&
          editableSettings.CustomThemes.Single(item => item.Id == snowOption.Id).Colors.Card == persistedSnowBeforeCancel.Card,
        "editing an inactive theme live-applies and Cancel restores the previous active theme", failures);

    var blackTheme = whiteTheme.Clone();
    blackTheme.Background = "#FF000000";
    blackTheme.PrimaryText = "#FF000000";
    themeEditor.Results.Enqueue(new("Snow edited", blackTheme));
    main.EditThemeCommand.Execute(snowOption.Id);
    await WaitUntilAsync(() => snowOption.DisplayName == "Snow edited");
    Check(editableSettings.CustomThemes.Single().Colors.Background == "#FF000000" && snowOption.IsActive,
        "Custom Theme edit updates existing item without changing its ID", failures);

    var countBeforeCancelledDuplicate = editableSettings.CustomThemes.Count;
    themeEditor.EditActions.Enqueue(request => request.ApplyTemporary?.Invoke(request.Colors.Clone()));
    themeEditor.Results.Enqueue(null);
    main.DuplicateThemeCommand.Execute(snowOption.Id);
    await WaitUntilAsync(() => main.DuplicateThemeCommand.CanExecute(snowOption.Id));
    Check(editableSettings.CustomThemes.Count == countBeforeCancelledDuplicate &&
          editableSettings.ThemeId == snowOption.Id && themeManager.CurrentThemeId == snowOption.Id,
        "cancelled duplicate draft leaves no orphan and restores active theme", failures);

    themeEditor.Results.Enqueue(new("Snow copy", blackTheme.Clone()));
    main.DuplicateThemeCommand.Execute(snowOption.Id);
    await WaitUntilAsync(() => editableSettings.CustomThemes.Count == 2);
    var copiedOption = main.ThemeOptions.Single(item => item.IsCustom && item.DisplayName == "Snow copy");
    Check(copiedOption.Id != snowOption.Id && copiedOption.IsActive,
        "Custom Theme duplicate receives a new ID and becomes active", failures);

    themeEditor.RenameResults.Enqueue("Renamed copy");
    main.RenameThemeCommand.Execute(copiedOption.Id);
    await WaitUntilAsync(() => copiedOption.DisplayName == "Renamed copy");
    Check(editableSettings.CustomThemes.Any(item => item.Name == "Renamed copy"),
        "Custom Theme rename updates persistence", failures);

    main.DeleteThemeCommand.Execute(copiedOption.Id);
    await WaitUntilAsync(() => editableSettings.CustomThemes.Count == 1);
    Check(main.SelectedThemeOption?.Id == ThemeIds.Graphite && main.SelectedThemeOption.IsActive,
        "deleting active Custom Theme returns to a built-in theme", failures);

    var graphite = main.ThemeOptions.Single(item => item.Id == ThemeIds.Graphite);
    themeEditor.Results.Enqueue(new("Graphite copy", CustomThemeSettings.CreateDefault()));
    main.DuplicateThemeCommand.Execute(graphite.Id);
    await WaitUntilAsync(() => editableSettings.CustomThemes.Count == 2);
    Check(main.SelectedThemeOption?.DisplayName == "Graphite copy" && main.SelectedThemeOption.IsCustom,
        "editing a built-in theme saves and activates a new copy", failures);

    var duplicateNameVm = new CustomThemeEditorViewModel(new(
        CustomThemeEditMode.Add, "Snow edited", CustomThemeSettings.CreateDefault(), ["Snow edited"]), testLocalization);
    Check(!duplicateNameVm.IsNameValid && duplicateNameVm.NameError.Length > 0,
        "identical Custom Theme names are rejected", failures);

    var duplicateSources = new[]
    {
        new CustomThemeDefinition { Name = "First", Colors = CustomThemeSettings.CreateDefault() },
        new CustomThemeDefinition { Name = "Second", Colors = CustomThemeSettings.CreateDefault() },
        new CustomThemeDefinition { Name = "Last", Colors = CustomThemeSettings.CreateDefault() }
    };
    duplicateSources[0].Colors.Background = "#FF110000";
    duplicateSources[0].Colors.PrimaryButtonBackground = "#FFFF1111";
    duplicateSources[1].Colors.Background = "#FF001100";
    duplicateSources[1].Colors.PrimaryButtonBackground = "#FF11FF11";
    duplicateSources[2].Colors.Background = "#FF000011";
    duplicateSources[2].Colors.PrimaryButtonBackground = "#FF1111FF";
    var duplicateSettings = new UserSettings
    {
        ThemeId = duplicateSources[1].Id,
        CustomThemes = duplicateSources.Select(item => item.Clone()).ToList()
    };
    var duplicateManager = new TestThemeManager();
    duplicateManager.ApplyTheme(duplicateSettings.ThemeId, duplicateSettings.CustomThemes[1].Colors);
    var duplicateEditor = new TestCustomThemeEditorService { EchoWhenEmpty = true };
    var duplicateMain = new MainWindowViewModel(new TestCatalogService(), new TestDialogService(), configurationCatalog,
        duplicateManager, testLocalization, new TestSettingsRepository(), duplicateSettings, runner,
        new ProfileRestoreRunner(registry, sessionRepository), sessionRepository, new TestCompletionBehavior(),
        new TestDisplayManager(new("", "", "", 1, 1, 1, 32, 0, 0, 0, 0)), duplicateEditor);
    Check(duplicateMain.ThemeOptions.Single(item => item.Id == duplicateSources[1].Id).IsActive,
        "duplication test starts with the second source active", failures);
    var sourceIds = new[]
    {
        duplicateSources[1].Id, // active second
        duplicateSources[0].Id, // inactive first
        duplicateSources[2].Id, // inactive last
        duplicateSources[0].Id,
        duplicateSources[0].Id
    };
    foreach (var sourceId in sourceIds)
    {
        var sourceSnapshot = duplicateSettings.CustomThemes.Single(item => item.Id == sourceId).Colors.Clone();
        var previousRequestCount = duplicateEditor.Requests.Count;
        duplicateMain.DuplicateThemeCommand.Execute(sourceId);
        await WaitUntilAsync(() => duplicateEditor.Requests.Count == previousRequestCount + 1 &&
                                   duplicateMain.DuplicateThemeCommand.CanExecute(sourceId));
        var request = duplicateEditor.Requests[^1];
        var openedCopy = duplicateSettings.CustomThemes.SingleOrDefault(item => item.Id == request.ThemeId);
        Check(request.ThemeId is not null && request.ThemeId != sourceId && openedCopy is not null &&
              request.Colors.Background == sourceSnapshot.Background &&
              request.Colors.PrimaryButtonBackground == sourceSnapshot.PrimaryButtonBackground &&
              openedCopy.Colors.Background == sourceSnapshot.Background &&
              !ReferenceEquals(openedCopy.Colors, duplicateSettings.CustomThemes.Single(item => item.Id == sourceId).Colors),
            $"duplicate resolves exact source ID and opens exact new ID: {sourceId}", failures);
    }
    Check(duplicateSettings.CustomThemes.Any(item => item.Name == "First — copy") &&
          duplicateSettings.CustomThemes.Any(item => item.Name == "First — copy (2)") &&
          duplicateSettings.CustomThemes.Any(item => item.Name == "First — copy (3)"),
        "multiple copies of one ThemeId receive deterministic unique names", failures);

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

    var treeHelperPath = Environment.ProcessPath;
    var treeChildPidPath = Path.Combine(testRoot, "program-run-child.pid");
    if (!string.IsNullOrWhiteSpace(treeHelperPath) &&
        string.Equals(Path.GetExtension(treeHelperPath), ".exe", StringComparison.OrdinalIgnoreCase))
    {
        var treeAction = Action(ActionTypeIds.ProgramRun, new JsonObject
        {
            [ActionParameterNames.Target] = treeHelperPath,
            [ActionParameterNames.Arguments] = $"--program-run-tree-helper \"{treeChildPidPath}\"",
            [ActionParameterNames.StartOnlyIfNotAlreadyRunning] = false
        });
        treeAction.RestoreBehavior = ActionRestoreBehavior.CloseIfStartedBySwitchBoard;
        var treeProfile = new ProfileDefinition
        {
            CategoryId = Guid.NewGuid(), Name = "Program process tree restore test", Actions = [treeAction]
        };

        var treeSession = await runner.RunAsync(treeProfile);
        var treePending = await sessionRepository.GetLatestPendingAsync(treeProfile.Id);
        await Task.Delay(1500);
        var treeState = treePending?.Actions[0].PreviousState;
        var rootPid = treeState?["processId"]?.GetValue<int>() ?? 0;
        var childPid = File.Exists(treeChildPidPath) &&
                       int.TryParse(await File.ReadAllTextAsync(treeChildPidPath), out var parsedChildPid)
            ? parsedChildPid
            : 0;
        var trackedProcesses = treeState?["launchedProcesses"] as JsonArray;
        Check(treeSession.Status == ExecutionSessionStatus.Completed && rootPid > 0 && childPid > 0 &&
              trackedProcesses?.OfType<JsonObject>().Any(item =>
                  item["processId"]?.GetValue<int>() == childPid) == true,
            "program.run persists launcher and descendant process identities", failures);

        var rootExitedBeforeRestore = rootPid > 0;
        try
        {
            using var rootProcess = Process.GetProcessById(rootPid);
            rootExitedBeforeRestore = rootProcess.HasExited;
        }
        catch (ArgumentException) { rootExitedBeforeRestore = true; }
        Check(rootExitedBeforeRestore, "program.run test launcher exits before Restore", failures);

        if (treePending is not null)
            await new ProfileRestoreRunner(registry, sessionRepository).RunAsync(treePending);
        await Task.Delay(300);

        var childStillAlive = childPid > 0;
        try
        {
            using var childProcess = Process.GetProcessById(childPid);
            childStillAlive = !childProcess.HasExited;
        }
        catch (ArgumentException) { childStillAlive = false; }
        Check(!childStillAlive,
            "program.run Restore closes saved descendant after launcher has exited", failures);

        foreach (var processId in new[] { rootPid, childPid }.Where(value => value > 0))
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) { }
        }
    }
    else
    {
        failures.Add("Runtime test executable path is unavailable for program.run process-tree test");
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

static bool SemanticContrastIsAccessible(System.Windows.Application app)
{
    var pairs = new[]
    {
        ("PrimaryButtonBackground", "PrimaryButtonForeground"),
        ("PrimaryButtonHoverBackground", "PrimaryButtonHoverForeground"),
        ("PrimaryButtonPressedBackground", "PrimaryButtonPressedForeground"),
        ("PrimaryButtonDisabledBackground", "PrimaryButtonDisabledForeground"),
        ("SecondaryButtonBackground", "SecondaryButtonForeground"),
        ("SecondaryButtonHoverBackground", "SecondaryButtonHoverForeground"),
        ("SecondaryButtonPressedBackground", "SecondaryButtonPressedForeground"),
        ("SecondaryButtonDisabledBackground", "SecondaryButtonDisabledForeground"),
        ("MenuBackground", "MenuForeground"),
        ("MenuHoverBackground", "MenuHoverForeground"),
        ("MenuPressedBackground", "MenuPressedForeground"),
        ("MenuDisabledBackground", "MenuDisabledForeground"),
        ("SelectedBrush", "SelectionForeground"),
        ("HoverBrush", "HoverForeground"),
        ("DisabledInputBackground", "DisabledInputForeground"),
        ("BackgroundBrush", "TextPrimaryBrush"),
        ("BackgroundBrush", "TextSecondaryBrush")
    };
    var applicationBackground = RepresentativeColor(app.TryFindResource("BackgroundBrush") as Brush, Colors.Black);
    return pairs.All(pair => app.TryFindResource(pair.Item1) is Brush background &&
                             app.TryFindResource(pair.Item2) is SolidColorBrush foreground &&
                             BrushColors(background).All(color =>
                                 ContrastRatio(Composite(color, applicationBackground), foreground.Color) >= 4.5));
}

static bool RenderedSemanticControlsAreAccessible(System.Windows.Application app)
{
    var secondary = new System.Windows.Controls.Button();
    secondary.Style = app.TryFindResource(typeof(System.Windows.Controls.Button)) as System.Windows.Style;
    var primary = new System.Windows.Controls.Button
    {
        Style = app.TryFindResource("AccentButton") as System.Windows.Style
    };
    var menu = new System.Windows.Controls.ContextMenu();
    var menuItem = new System.Windows.Controls.MenuItem
    {
        Header = "Theme",
        Style = app.TryFindResource(typeof(System.Windows.Controls.MenuItem)) as System.Windows.Style
    };
    menu.Items.Add(menuItem);

    var normal = Matches(secondary, "SecondaryButtonBackground", "SecondaryButtonForeground") &&
                 Matches(primary, "PrimaryButtonBackground", "PrimaryButtonForeground") &&
                 Matches(menuItem, "MenuBackground", "MenuForeground");
    secondary.IsEnabled = false;
    primary.IsEnabled = false;
    menuItem.IsEnabled = false;
    return normal &&
           Matches(secondary, "SecondaryButtonDisabledBackground", "SecondaryButtonDisabledForeground") &&
           Matches(primary, "PrimaryButtonDisabledBackground", "PrimaryButtonDisabledForeground") &&
           Matches(menuItem, "MenuDisabledBackground", "MenuDisabledForeground");

    bool Matches(System.Windows.Controls.Control control, string backgroundKey, string foregroundKey) =>
        control.Background is Brush actualBackground && control.Foreground is SolidColorBrush actualForeground &&
        app.TryFindResource(backgroundKey) is Brush expectedBackground &&
        app.TryFindResource(foregroundKey) is SolidColorBrush expectedForeground &&
        BrushColors(actualBackground).SequenceEqual(BrushColors(expectedBackground)) &&
        actualForeground.Color == expectedForeground.Color;
}

static IReadOnlyList<Color> BrushColors(Brush brush) => brush switch
{
    SolidColorBrush solid => [solid.Color],
    GradientBrush gradient when gradient.GradientStops.Count > 0 => gradient.GradientStops.Select(stop => stop.Color).ToArray(),
    _ => [Colors.Black]
};

static Color RepresentativeColor(Brush? brush, Color fallback)
{
    if (brush is null) return fallback;
    var colors = BrushColors(brush);
    return Color.FromArgb((byte)Math.Round(colors.Average(value => value.A)),
        (byte)Math.Round(colors.Average(value => value.R)),
        (byte)Math.Round(colors.Average(value => value.G)),
        (byte)Math.Round(colors.Average(value => value.B)));
}

static Color Composite(Color foreground, Color background)
{
    if (foreground.A == byte.MaxValue) return foreground;
    var alpha = foreground.A / 255d;
    return Color.FromRgb(
        (byte)Math.Round(foreground.R * alpha + background.R * (1 - alpha)),
        (byte)Math.Round(foreground.G * alpha + background.G * (1 - alpha)),
        (byte)Math.Round(foreground.B * alpha + background.B * (1 - alpha)));
}

static async Task WaitUntilAsync(Func<bool> predicate)
{
    for (var attempt = 0; attempt < 100 && !predicate(); attempt++)
        await Task.Delay(20);
    if (!predicate()) throw new TimeoutException("The asynchronous test condition was not reached.");
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
    public string Format(string resourceKey, params object?[] arguments) => resourceKey == "CustomTheme.CopyName"
        ? $"{arguments[0]} — copy"
        : $"{resourceKey}: {string.Join(", ", arguments)}";
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
    public UserSettings Saved { get; private set; } = new();
    public int SaveCount { get; private set; }
    public Task<UserSettings> LoadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(Saved))!);
    public Task SaveAsync(UserSettings settings, CancellationToken cancellationToken = default)
    {
        SaveCount++;
        Saved = JsonSerializer.Deserialize<UserSettings>(JsonSerializer.Serialize(settings))!;
        return Task.CompletedTask;
    }
}

sealed class TestThemeManager : IThemeManager
{
    public IReadOnlyList<ThemeDefinition> AvailableThemes =>
        [new(ThemeIds.Graphite, "Graphite", new Uri("Themes/GraphiteTheme.xaml", UriKind.Relative))];
    public string CurrentThemeId { get; private set; } = ThemeIds.Graphite;
    public string ApplyTheme(string? themeId, CustomThemeSettings? customTheme = null)
    {
        CurrentThemeId = customTheme is not null && !string.IsNullOrWhiteSpace(themeId) ? themeId : ThemeIds.Graphite;
        return CurrentThemeId;
    }
    public string ApplyTemporary(string draftThemeId, CustomThemeSettings draft)
    {
        CurrentThemeId = draftThemeId;
        LastTemporarySettings = draft.Clone();
        TemporaryApplyCount++;
        return CurrentThemeId;
    }
    public CustomThemeSettings? LastTemporarySettings { get; private set; }
    public int TemporaryApplyCount { get; private set; }
    public CustomThemeSettings CreateEditableCopy(string builtInThemeId) => CustomThemeSettings.CreateDefault();
}

sealed class TestCustomThemeEditorService : ICustomThemeEditorService
{
    public Queue<CustomThemeEditResult?> Results { get; } = [];
    public Queue<Action<CustomThemeEditRequest>> EditActions { get; } = [];
    public Queue<string?> RenameResults { get; } = [];
    public List<CustomThemeEditRequest> Requests { get; } = [];
    public bool EchoWhenEmpty { get; set; }
    public CustomThemeEditResult? Edit(CustomThemeEditRequest request)
    {
        Requests.Add(request with { Colors = request.Colors.Clone() });
        if (EditActions.Count > 0) EditActions.Dequeue()(request);
        return Results.Count > 0 ? Results.Dequeue()
            : EchoWhenEmpty ? new(request.Name, request.Colors.Clone()) : null;
    }
    public string? Rename(string currentName, IReadOnlyCollection<string> unavailableNames) =>
        RenameResults.Count > 0 ? RenameResults.Dequeue() : null;
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
