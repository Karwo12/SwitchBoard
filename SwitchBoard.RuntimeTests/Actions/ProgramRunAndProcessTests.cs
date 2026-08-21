using SwitchBoard.RuntimeTests.TestInfrastructure;

namespace SwitchBoard.RuntimeTests.Actions;

[Collection("Windows runtime")]
public sealed class ProgramRunAndProcessTests : RuntimeTestBase
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task ProcessSetState_StoppedProcess_IsSkippedOnSecondExecution()
    {
        using var notepad = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true });
        Assert.NotNull(notepad);
        await Task.Delay(500);

        var action = Action(ActionTypeIds.ProcessSetState, new JsonObject
        {
            [ActionParameterNames.ProcessName] = "notepad",
            [ActionParameterNames.DesiredState] = ProcessDesiredStateIds.Stopped
        });
        var handler = new ProcessSetStateActionHandler();
        var first = await handler.ExecuteAsync(action, new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);
        await Task.Delay(750);
        var second = await handler.ExecuteAsync(action, new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

        Assert.True(first.IsSuccessful && !first.IsSkipped, "The first process.setState execution should stop Notepad.");
        Assert.True(second.IsSuccessful && second.IsSkipped, "The second process.setState execution should skip the absent process.");
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task ProcessConfigure_StopOperation_StopsProcess()
    {
        using var notepad = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true });
        Assert.NotNull(notepad);
        await Task.Delay(500);

        var action = Action(ActionTypeIds.ProcessConfigure, new JsonObject
        {
            [ActionParameterNames.ProcessName] = "notepad",
            [ActionParameterNames.ProcessOperation] = ProcessOperationIds.Stop
        });
        var result = await new ProcessConfigureActionHandler().ExecuteAsync(
            action, new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

        Assert.True(result.IsSuccessful && !result.IsSkipped,
            "process.configure stop operation should reuse the process stop handler.");
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task ProgramRun_ExistingProcess_IsSkippedWhenConfigured()
    {
        var powershellPath = GetPowerShellPath();
        using var preexisting = StartPowerShellSleep(powershellPath, 30);
        Assert.NotNull(preexisting);
        await Task.Delay(350);

        var result = await new ProgramRunActionHandler().ExecuteAsync(
            Action(ActionTypeIds.ProgramRun, new JsonObject
            {
                [ActionParameterNames.Target] = powershellPath,
                [ActionParameterNames.StartOnlyIfNotAlreadyRunning] = true
            }), new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

        Assert.True(result.IsSkipped, "program.run should skip an already-running target when configured to do so.");
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task ProgramRun_RestoreClosesOnlyTheInstanceStartedBySwitchBoard()
    {
        using var context = new RuntimeTestContext();
        var powershellPath = GetPowerShellPath();
        using var preexisting = StartPowerShellSleep(powershellPath, 30);
        Assert.NotNull(preexisting);
        await Task.Delay(350);

        var action = Action(ActionTypeIds.ProgramRun, new JsonObject
        {
            [ActionParameterNames.Target] = powershellPath,
            [ActionParameterNames.Arguments] = "-NoProfile -Command \"Start-Sleep -Seconds 30\"",
            [ActionParameterNames.StartOnlyIfNotAlreadyRunning] = false
        });
        action.RestoreBehavior = ActionRestoreBehavior.CloseIfStartedBySwitchBoard;
        var profile = new ProfileDefinition
        {
            CategoryId = Guid.NewGuid(), Name = "Program restore test", Actions = [action]
        };

        var session = await context.Runner.RunAsync(profile);
        var pending = await context.SessionRepository.GetLatestPendingAsync(profile.Id);
        var launchedPid = pending?.Actions[0].PreviousState?["processId"]?.GetValue<int>() ?? 0;
        Assert.True(session.Status == ExecutionSessionStatus.Completed && launchedPid > 0 &&
                    launchedPid != preexisting.Id && !preexisting.HasExited,
            "program.run should persist only the process instance started by SwitchBoard.");

        Assert.NotNull(pending);
        var reloaded = await context.SessionRepository.LoadAsync(pending!.SessionId);
        Assert.Equal(1, reloaded?.PendingRestoreCount);
        await new ProfileRestoreRunner(context.Registry, context.SessionRepository).RunAsync(reloaded!);
        await Task.Delay(250);

        Assert.False(IsProcessAlive(launchedPid), "Restore should close the exact launched PID.");
        Assert.False(preexisting.HasExited, "Restore must preserve the pre-existing process.");
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task ProgramRun_PostLaunchSettings_AreApplied()
    {
        using var context = new RuntimeTestContext();
        var powershellPath = GetPowerShellPath();
        var targetPath = Path.Combine(context.Root, $"switchboard-post-launch-{Guid.NewGuid():N}.exe");
        File.Copy(powershellPath, targetPath);
        try
        {
            var action = Action(ActionTypeIds.ProgramRun, new JsonObject
            {
                [ActionParameterNames.Target] = targetPath,
                [ActionParameterNames.Arguments] = "-NoProfile -Command \"Start-Sleep -Seconds 5\"",
                [ActionParameterNames.StartOnlyIfNotAlreadyRunning] = false,
                [ActionParameterNames.ChangePriority] = true,
                [ActionParameterNames.ChangeAffinity] = true,
                [ActionParameterNames.CpuIndices] = new JsonArray(0),
                [ActionParameterNames.ProcessPriority] = ProcessPriorityIds.BelowNormal,
                [ActionParameterNames.ProcessPerformanceMode] = ProcessPerformanceModeIds.HighPerformance,
                [ActionParameterNames.ProcessTargetMode] = ProcessTargetModeIds.Automatic
            });

            var result = await new ProgramRunActionHandler().ExecuteAsync(
                action, new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

            Assert.True(result.IsSuccessful, "program.run should apply automatic post-launch priority and affinity.");
        }
        finally
        {
            KillProcessesByName(Path.GetFileNameWithoutExtension(targetPath));
        }
    }

    [EnvironmentFact("Edge")]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task ProgramRun_EdgeRestore_ClosesOnlyNewEdgeProcesses()
    {
        using var context = new RuntimeTestContext();
        var edgePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft", "Edge", "Application", "msedge.exe");
        if (!File.Exists(edgePath))
            edgePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Microsoft", "Edge", "Application", "msedge.exe");
        Assert.True(File.Exists(edgePath), "The Edge environment requirement was satisfied but the executable disappeared.");

        var baseline = Process.GetProcessesByName("msedge");
        var baselineIds = baseline.Select(process => process.Id).ToHashSet();
        foreach (var process in baseline) process.Dispose();

        var edgeData = Path.Combine(context.Root, "edge-profile");
        var action = Action(ActionTypeIds.ProgramRun, new JsonObject
        {
            [ActionParameterNames.Target] = edgePath,
            [ActionParameterNames.Arguments] = $"--user-data-dir=\"{edgeData}\" --no-first-run about:blank",
            [ActionParameterNames.InstanceBehavior] = InstanceBehaviorIds.StartAnother
        });
        action.RestoreBehavior = ActionRestoreBehavior.CloseIfStartedBySwitchBoard;
        var profile = new ProfileDefinition
        {
            CategoryId = Guid.NewGuid(), Name = "Microsoft Edge restore test", Actions = [action]
        };

        try
        {
            var session = await context.Runner.RunAsync(profile);
            var pending = await context.SessionRepository.GetLatestPendingAsync(profile.Id);
            var state = pending?.Actions.Single().PreviousState;
            var tracked = state?["launchedProcesses"] as JsonArray;
            Assert.True(session.Status == ExecutionSessionStatus.Completed &&
                        state?["startedBySwitchBoard"]?.GetValue<bool>() == true && tracked?.Count > 1,
                "program.run should identify the multi-process Edge launch.");
            Assert.NotNull(pending);

            var reloaded = await context.SessionRepository.LoadAsync(pending!.SessionId);
            var restoreResult = await new ProfileRestoreRunner(context.Registry, context.SessionRepository).RunAsync(reloaded!);
            await Task.Delay(500);
            var after = Process.GetProcessesByName("msedge");
            var afterIds = after.Select(process => process.Id).ToHashSet();
            foreach (var process in after) process.Dispose();
            var trackedIds = tracked!.OfType<JsonObject>()
                .Select(item => item["processId"]?.GetValue<int>() ?? 0).Where(id => id > 0).ToHashSet();
            var persistedBaselineIds = (state?["preExistingProcesses"] as JsonArray)?.OfType<JsonObject>()
                .Select(item => item["processId"]?.GetValue<int>() ?? 0).Where(id => id > 0).ToHashSet() ?? [];

            Assert.Equal(0, restoreResult.PendingRestoreCount);
            Assert.False(trackedIds.Overlaps(afterIds), "Edge Restore should close every tracked child process.");
            Assert.False(trackedIds.Overlaps(persistedBaselineIds), "Edge Restore must not target baseline processes.");
            Assert.True(baselineIds.SetEquals(persistedBaselineIds), "The persisted Edge baseline set should be exact.");
        }
        finally
        {
            KillProcessesByName("msedge");
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task ProgramRun_RestoreClosesDescendantAfterLauncherExits()
    {
        using var context = new RuntimeTestContext();
        var helperPath = GetPowerShellPath();
        var launcherPath = Path.Combine(context.Root, "program-run-tree-launcher.ps1");
        var childPidPath = Path.Combine(context.Root, "program-run-child.pid");
        await File.WriteAllTextAsync(launcherPath, @"
param([string]$PidPath)
$child = Start-Process -FilePath (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') -ArgumentList @('-NoProfile', '-Command', 'Start-Sleep -Seconds 60') -PassThru
Set-Content -LiteralPath $PidPath -Value $child.Id
");

        var action = Action(ActionTypeIds.ProgramRun, new JsonObject
        {
            [ActionParameterNames.Target] = helperPath,
            [ActionParameterNames.Arguments] = $"-NoProfile -ExecutionPolicy Bypass -File \"{launcherPath}\" \"{childPidPath}\"",
            [ActionParameterNames.StartOnlyIfNotAlreadyRunning] = false
        });
        action.RestoreBehavior = ActionRestoreBehavior.CloseIfStartedBySwitchBoard;
        var profile = new ProfileDefinition
        {
            CategoryId = Guid.NewGuid(), Name = "Program process tree restore test", Actions = [action]
        };

        var session = await context.Runner.RunAsync(profile);
        var pending = await context.SessionRepository.GetLatestPendingAsync(profile.Id);
        await Task.Delay(1500);
        var state = pending?.Actions[0].PreviousState;
        var rootPid = state?["processId"]?.GetValue<int>() ?? 0;
        var childPid = File.Exists(childPidPath) && int.TryParse(await File.ReadAllTextAsync(childPidPath), out var parsed)
            ? parsed : 0;
        var tracked = state?["launchedProcesses"] as JsonArray;
        Assert.True(session.Status == ExecutionSessionStatus.Completed && rootPid > 0 && childPid > 0 &&
                    tracked?.OfType<JsonObject>().Any(item => item["processId"]?.GetValue<int>() == childPid) == true,
            "program.run should persist launcher and descendant identities.");
        Assert.False(IsProcessAlive(rootPid), "The test launcher should exit before Restore.");

        if (pending is not null)
            await new ProfileRestoreRunner(context.Registry, context.SessionRepository).RunAsync(pending);
        await Task.Delay(300);
        Assert.False(IsProcessAlive(childPid), "Restore should close a saved descendant after its launcher exits.");
        KillProcess(rootPid);
        KillProcess(childPid);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task ProcessSetState_Kill_AllowsFollowingActionToSucceed()
    {
        using var context = new RuntimeTestContext();
        var powershellPath = GetPowerShellPath();
        using var powershell = StartPowerShellSleep(powershellPath, 30);
        Assert.NotNull(powershell);
        await Task.Delay(350);

        var action = Action(ActionTypeIds.ProcessSetState, new JsonObject
        {
            [ActionParameterNames.ProcessName] = "powershell",
            [ActionParameterNames.ExecutablePath] = powershellPath,
            [ActionParameterNames.DesiredState] = ProcessDesiredStateIds.Stopped
        });
        action.RuntimeProcessIdHint = powershell!.Id;
        var profile = new ProfileDefinition
        {
            CategoryId = Guid.NewGuid(), Name = "PowerShell process runtime test",
            Actions = [action, Action(ActionTypeIds.Delay, new JsonObject { [ActionParameterNames.DelaySeconds] = 0 })]
        };
        profile.Actions[0].SortOrder = 0;
        profile.Actions[1].SortOrder = 1;

        var session = await context.Runner.RunAsync(profile);

        Assert.True(session.Status == ExecutionSessionStatus.Completed &&
                    session.Journal[0].Status == ActionJournalStatus.Success &&
                    session.Journal[1].Status == ActionJournalStatus.Success && powershell.HasExited,
            "process.setState should remain successful and allow the following action to run.");
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task ScriptRun_RestoreScript_RemovesItsMarker()
    {
        using var context = new RuntimeTestContext();
        var marker = Path.Combine(context.Root, "restore-script-marker.txt");
        var mainScript = Path.Combine(context.Root, "main-for-restore.ps1");
        var cleanupScript = Path.Combine(context.Root, "cleanup.ps1");
        await File.WriteAllTextAsync(mainScript, $"Set-Content -LiteralPath '{marker.Replace("'", "''")}' -Value created\nexit 0\n");
        await File.WriteAllTextAsync(cleanupScript, $"Remove-Item -LiteralPath '{marker.Replace("'", "''")}' -Force -ErrorAction SilentlyContinue\nexit 0\n");

        var action = Action(ActionTypeIds.ScriptRun, new JsonObject
        {
            [ActionParameterNames.ScriptPath] = mainScript,
            [ActionParameterNames.ScriptType] = ScriptTypeIds.PowerShell,
            [ActionParameterNames.WaitForExit] = true,
            [ActionParameterNames.RestoreScriptPath] = cleanupScript,
            [ActionParameterNames.RestoreScriptType] = ScriptTypeIds.PowerShell,
            [ActionParameterNames.RestoreScriptWaitForExit] = true
        });
        action.RestoreBehavior = ActionRestoreBehavior.RunRestoreScript;
        var profile = new ProfileDefinition
        {
            CategoryId = Guid.NewGuid(), Name = "Restore script test", Actions = [action]
        };

        await context.Runner.RunAsync(profile);
        var pending = await context.SessionRepository.GetLatestPendingAsync(profile.Id);
        Assert.True(File.Exists(marker) && pending?.PendingRestoreCount == 1,
            "script.run should save the explicit Restore Script configuration.");
        if (pending is not null)
            await new ProfileRestoreRunner(context.Registry, context.SessionRepository).RunAsync(pending);
        Assert.False(File.Exists(marker), "Restore Script should execute and remove the marker.");
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task ProcessSetState_RestartRestore_RestartsPreviouslyRunningProcess()
    {
        using var context = new RuntimeTestContext();
        var notepadPath = Path.Combine(Environment.SystemDirectory, "notepad.exe");
        using var notepad = Process.Start(new ProcessStartInfo(notepadPath) { UseShellExecute = true });
        Assert.NotNull(notepad);
        await Task.Delay(500);
        try { notepadPath = notepad!.MainModule?.FileName ?? notepadPath; } catch { }

        var action = Action(ActionTypeIds.ProcessSetState, new JsonObject
        {
            [ActionParameterNames.ProcessName] = "notepad",
            [ActionParameterNames.ExecutablePath] = notepadPath,
            [ActionParameterNames.DesiredState] = ProcessDesiredStateIds.Stopped
        });
        action.RuntimeProcessIdHint = notepad.Id;
        action.RestoreBehavior = ActionRestoreBehavior.RestartIfWasRunning;
        var profile = new ProfileDefinition
        {
            CategoryId = Guid.NewGuid(), Name = "Process restart test", Actions = [action]
        };

        await context.Runner.RunAsync(profile);
        var pending = await context.SessionRepository.GetLatestPendingAsync(profile.Id);
        Assert.True(notepad.HasExited && pending?.PendingRestoreCount == 1,
            "process.setState should capture the executable before stopping it.");
        if (pending is not null)
            await new ProfileRestoreRunner(context.Registry, context.SessionRepository).RunAsync(pending);
        await Task.Delay(500);
        var restarted = Process.GetProcessesByName("notepad");
        try { Assert.NotEmpty(restarted); }
        finally
        {
            foreach (var process in restarted)
            {
                try { if (!process.HasExited) process.Kill(); } catch { } finally { process.Dispose(); }
            }
        }
    }

    private static string GetPowerShellPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");

    private static Process? StartPowerShellSleep(string path, int seconds)
    {
        var info = new ProcessStartInfo(path) { UseShellExecute = false };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add($"Start-Sleep -Seconds {seconds}");
        return Process.Start(info);
    }

    private static bool IsProcessAlive(int processId)
    {
        if (processId <= 0) return false;
        try { using var process = Process.GetProcessById(processId); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static void KillProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException) { }
    }

    private static void KillProcessesByName(string processName)
    {
        foreach (var process in Process.GetProcessesByName(processName))
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) { }
            finally { process.Dispose(); }
        }
    }
}
