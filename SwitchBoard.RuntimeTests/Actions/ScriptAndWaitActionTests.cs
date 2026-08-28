using SwitchBoard.RuntimeTests.TestInfrastructure;

namespace SwitchBoard.RuntimeTests.Actions;

[Collection("Windows runtime")]
public sealed class ScriptAndWaitActionTests : RuntimeTestBase
{
    [EnvironmentFact("PowerShellExecution")]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task ScriptRun_PowerShellArguments_ArePassedAndWaitedFor()
    {
        using var context = new RuntimeTestContext();
        var outputPath = Path.Combine(context.Root, "script-output.txt");
        var scriptPath = Path.Combine(context.Root, "success.ps1");
        await File.WriteAllTextAsync(scriptPath,
            "param([string]$Value)\nSet-Content -LiteralPath $env:SB_TEST_OUTPUT -Value $Value\nexit 0\n");
        Environment.SetEnvironmentVariable("SB_TEST_OUTPUT", outputPath);

        var result = await new ScriptRunActionHandler().ExecuteAsync(
            Action(ActionTypeIds.ScriptRun, new JsonObject
            {
                [ActionParameterNames.ScriptPath] = scriptPath,
                [ActionParameterNames.ScriptType] = ScriptTypeIds.AutoDetect,
                [ActionParameterNames.Arguments] = "\"argument with spaces\"",
                [ActionParameterNames.WorkingDirectory] = context.Root,
                [ActionParameterNames.WaitForExit] = true
            }), new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

        Assert.True(result.IsSuccessful && File.Exists(outputPath), "PowerShell script should complete successfully.");
        Assert.Equal("argument with spaces", (await File.ReadAllTextAsync(outputPath)).Trim());
    }

    [EnvironmentFact("PowerShellExecution")]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task ScriptRun_BatchArguments_AreQuotedCorrectly()
    {
        using var context = new RuntimeTestContext();
        var outputPath = Path.Combine(context.Root, "batch-output.txt");
        var scriptPath = Path.Combine(context.Root, "success.cmd");
        await File.WriteAllTextAsync(scriptPath, "@echo off\r\n>\"%SB_TEST_BATCH_OUTPUT%\" echo %~1\r\nexit /b 0\r\n");
        Environment.SetEnvironmentVariable("SB_TEST_BATCH_OUTPUT", outputPath);

        var result = await new ScriptRunActionHandler().ExecuteAsync(
            Action(ActionTypeIds.ScriptRun, new JsonObject
            {
                [ActionParameterNames.ScriptPath] = scriptPath,
                [ActionParameterNames.ScriptType] = ScriptTypeIds.BatchCmd,
                [ActionParameterNames.Arguments] = "\"batch argument\"",
                [ActionParameterNames.WaitForExit] = true
            }), new(Guid.NewGuid(), Guid.NewGuid()), CancellationToken.None);

        Assert.True(result.IsSuccessful && File.Exists(outputPath), "Batch script should complete successfully.");
        Assert.Equal("batch argument", (await File.ReadAllTextAsync(outputPath)).Trim());
    }

    [EnvironmentFact("PowerShellExecution")]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task ProfileRunner_FailedScript_ContinuesAccordingToFailurePolicy()
    {
        using var context = new RuntimeTestContext();
        var failedScript = Path.Combine(context.Root, "failure.ps1");
        await File.WriteAllTextAsync(failedScript, "exit 1\n");
        var profile = new ProfileDefinition
        {
            CategoryId = Guid.NewGuid(), Name = "Failure policy runtime test",
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

        var session = await context.Runner.RunAsync(profile);

        Assert.Equal(ExecutionSessionStatus.CompletedWithErrors, session.Status);
        Assert.Equal(ActionJournalStatus.Failed, session.Journal[0].Status);
        Assert.Equal(ActionJournalStatus.Success, session.Journal[1].Status);
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task WaitProcessStart_DetectsAProcessThatAppearsLater()
    {
        using var context = new RuntimeTestContext();
        var probePath = CreateProbe(context.Root, "sbwaitstartprobe");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var waitTask = new WaitProcessActionHandler(ActionTypeIds.WaitProcessStart).ExecuteAsync(
            Action(ActionTypeIds.WaitProcessStart, new JsonObject
            { [ActionParameterNames.ProcessName] = "sbwaitstartprobe" }),
            new(Guid.NewGuid(), Guid.NewGuid()), cts.Token);
        await Task.Delay(250, cts.Token);
        var probe = StartProbe(probePath);
        try
        {
            var result = await waitTask;
            Assert.True(result.IsSuccessful, "wait.processStart should observe the process asynchronously.");
        }
        finally
        {
            Kill(probe);
            probe?.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task WaitProcessExit_DetectsTheExactProcessDisappearing()
    {
        using var context = new RuntimeTestContext();
        var probePath = CreateProbe(context.Root, "sbwaitexitprobe");
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var probe = StartProbe(probePath);
        Assert.NotNull(probe);
        try
        {
            var waitTask = new WaitProcessActionHandler(ActionTypeIds.WaitProcessExit).ExecuteAsync(
                Action(ActionTypeIds.WaitProcessExit, new JsonObject
                { [ActionParameterNames.ProcessName] = "sbwaitexitprobe" }),
                new(Guid.NewGuid(), Guid.NewGuid()), cts.Token);
            await Task.Delay(250, cts.Token);
            probe!.Kill(entireProcessTree: true);

            var result = await waitTask;

            Assert.True(result.IsSuccessful, "wait.processExit should observe the exact process-name disappearance.");
        }
        finally
        {
            Kill(probe);
            probe.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task WaitProcessStart_CancellationStopsWaiting()
    {
        using var context = new RuntimeTestContext();
        using var cts = new CancellationTokenSource(180);
        var task = new WaitProcessActionHandler(ActionTypeIds.WaitProcessStart).ExecuteAsync(
            Action(ActionTypeIds.WaitProcessStart, new JsonObject
            { [ActionParameterNames.ProcessName] = $"missing-{Guid.NewGuid():N}" }),
            new(Guid.NewGuid(), Guid.NewGuid()), cts.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProcessWaitService_FindsAProcessAfterAConfiguredDelay()
    {
        using var context = new RuntimeTestContext();
        var probePath = CreateProbe(context.Root, "sblateprobe");
        Process? delayedProcess = null;
        try
        {
            var delayedStart = Task.Run(async () =>
            {
                await Task.Delay(450);
                delayedProcess = StartProbe(probePath);
            });
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var found = await ProcessWaitService.WaitForStartAsync("sblateprobe", probePath, null,
                TimeSpan.FromSeconds(4), cts.Token);
            await delayedStart;

            Assert.NotNull(found);
        }
        finally
        {
            Kill(delayedProcess);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProcessWaitService_ReturnsNullAfterTimeout()
    {
        using var context = new RuntimeTestContext();
        using var result = await ProcessWaitService.WaitForStartAsync(
            $"missing-{Guid.NewGuid():N}", null, null, TimeSpan.FromMilliseconds(250), CancellationToken.None);

        Assert.Null(result);
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProcessWaitService_CancellationStopsPolling()
    {
        using var context = new RuntimeTestContext();
        using var cts = new CancellationTokenSource();
        var task = ProcessWaitService.WaitForStartAsync(
            $"missing-{Guid.NewGuid():N}", null, null, TimeSpan.FromSeconds(10), cts.Token);
        cts.CancelAfter(150);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await task);
    }

    [EnvironmentFact("Notepad")]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task WaitWindow_DetectsANotepadWindow()
    {
        using var windowProcess = Process.Start(new ProcessStartInfo("notepad.exe") { UseShellExecute = true });
        Assert.NotNull(windowProcess);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var result = await new WaitWindowActionHandler().ExecuteAsync(
                Action(ActionTypeIds.WaitWindow, new JsonObject
                {
                    [ActionParameterNames.ProcessName] = "notepad",
                    [ActionParameterNames.WindowMatchMode] = WindowMatchModeIds.Any
                }), new(Guid.NewGuid(), Guid.NewGuid()), cts.Token);

            Assert.True(result.IsSuccessful, "wait.window should detect a visible main window.");
        }
        finally
        {
            Kill(windowProcess);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task ProcessConfigure_AffinityAndPriority_AreRestored()
    {
        using var context = new RuntimeTestContext();
        var path = CreateProbe(context.Root, "sbaffinity");
        var process = StartProbe(path);
        Assert.NotNull(process);
        try
        {
            var cpus = Enumerable.Range(0, Math.Min(Environment.ProcessorCount, IntPtr.Size * 8))
                .Where(cpu => cpu != 0 || Environment.ProcessorCount == 1).ToArray();
            var action = Action(ActionTypeIds.ProcessConfigure, new JsonObject
            {
                [ActionParameterNames.ProcessName] = "sbaffinity",
                [ActionParameterNames.ExecutablePath] = path,
                [ActionParameterNames.ChangeAffinity] = true,
                [ActionParameterNames.ChangePriority] = true,
                [ActionParameterNames.ProcessPriority] = ProcessPriorityIds.BelowNormal,
                [ActionParameterNames.CpuIndices] = new JsonArray(cpus.Select(cpu => (JsonNode?)JsonValue.Create(cpu)).ToArray())
            });
            var handler = new ProcessConfigureActionHandler();
            var executionContext = new ActionExecutionContext(Guid.NewGuid(), Guid.NewGuid());
            var oldState = await handler.CaptureStateAsync(action, executionContext, CancellationToken.None);
            var result = await handler.ExecuteAsync(action, executionContext, CancellationToken.None);
            process.Refresh();
            var expectedMask = unchecked((long)ProcessConfigureActionHandler.ReadAffinityMask(
                action.Parameters[ActionParameterNames.CpuIndices] as JsonArray));

            Assert.True(result.IsSuccessful && process.ProcessorAffinity.ToInt64() == expectedMask &&
                        process.PriorityClass == ProcessPriorityClass.BelowNormal,
                "process.configure should apply affinity and priority.");
            Assert.NotNull(oldState);
            await handler.RestoreAsync(action, oldState!, executionContext, CancellationToken.None);
            process.Refresh();
            Assert.Equal(oldState!["affinityMask"]!.GetValue<long>(), process.ProcessorAffinity.ToInt64());
        }
        finally
        {
            Kill(process);
            process.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task ProcessSettings_MemoryPriority_IsAppliedAndRestored()
    {
        using var context = new RuntimeTestContext();
        var path = CreateProbe(context.Root, "sbmemory");
        var process = StartProbe(path);
        Assert.NotNull(process);
        try
        {
            var settings = new ProcessSettingsService();
            var parameters = new JsonObject
            {
                [ActionParameterNames.ChangePriority] = false,
                [ActionParameterNames.ProcessPriority] = ProcessPriorityIds.NoChange,
                [ActionParameterNames.ProcessMemoryPriority] = ProcessMemoryPriorityIds.Low
            };
            var before = settings.Capture(process!, parameters);
            settings.Apply(process, parameters);
            var changed = settings.Capture(process, parameters);
            settings.Restore(process, before);
            var restored = settings.Capture(process, parameters);

            Assert.Equal(2, changed["memoryPriority"]?.GetValue<int>());
            Assert.Equal(before["memoryPriority"]?.GetValue<int>(), restored["memoryPriority"]?.GetValue<int>());
        }
        finally
        {
            Kill(process);
            process.Dispose();
        }
    }

    [EnvironmentFact("PowerShellExecution")]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task ScriptRun_TracksOnlyTheTargetProcessCreatedByTheScript()
    {
        using var context = new RuntimeTestContext();
        var targetPath = CreateProbe(context.Root, $"sbscript-target-{Guid.NewGuid():N}");
        var targetName = Path.GetFileNameWithoutExtension(targetPath);
        var scriptPath = Path.Combine(context.Root, "start-target.ps1");
        var quotedTarget = targetPath.Replace("'", "''");
        await File.WriteAllTextAsync(scriptPath,
            $"Start-Process -FilePath '{quotedTarget}' -ArgumentList @('/c','ping -n 30 127.0.0.1 > nul')\n");
        var existing = StartProbe(targetPath);
        Assert.NotNull(existing);
        try
        {
            await Task.Delay(300);
            var action = Action(ActionTypeIds.ScriptRun, new JsonObject
            {
                [ActionParameterNames.ScriptPath] = scriptPath,
                [ActionParameterNames.ScriptType] = ScriptTypeIds.PowerShell,
                [ActionParameterNames.WaitForExit] = true,
                [ActionParameterNames.ProcessName] = targetName,
                [ActionParameterNames.ExecutablePath] = targetPath,
                [ActionParameterNames.WaitForProcessStart] = true,
                [ActionParameterNames.ProcessStartWaitSeconds] = 5
            });
            action.RestoreBehavior = ActionRestoreBehavior.CloseIfStartedBySwitchBoard;
            var profile = new ProfileDefinition
            {
                CategoryId = Guid.NewGuid(), Name = "Script process tracking test", Actions = [action]
            };

            var session = await context.Runner.RunAsync(profile);
            var pending = await context.SessionRepository.GetLatestPendingAsync(profile.Id);
            var state = pending?.Actions.Single().PreviousState;
            var trackedIds = (state?["launchedProcesses"] as JsonArray)?.OfType<JsonObject>()
                .Select(item => item["processId"]?.GetValue<int>() ?? 0).Where(id => id > 0).ToHashSet() ?? [];

            Assert.Equal(ExecutionSessionStatus.Completed, session.Status);
            Assert.NotNull(pending);
            Assert.Contains(existing!.Id, (state?["preExistingProcesses"] as JsonArray)?.OfType<JsonObject>()
                .Select(item => item["processId"]?.GetValue<int>() ?? 0) ?? []);
            Assert.NotEmpty(trackedIds);
            Assert.DoesNotContain(existing.Id, trackedIds);

            await new ProfileRestoreRunner(context.Registry, context.SessionRepository).RunAsync(pending!);
            await Task.Delay(300);
            Assert.True(IsAlive(existing.Id));
            Assert.All(trackedIds, id => Assert.False(IsAlive(id)));
        }
        finally
        {
            Kill(existing);
            foreach (var process in Process.GetProcessesByName(targetName)) Kill(process);
        }
    }

    [EnvironmentFact("PowerShellExecution")]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task ScriptRun_TracksTargetInBackgroundWhenWaitingIsDisabled()
    {
        using var context = new RuntimeTestContext();
        var targetPath = CreateProbe(context.Root, $"sbscript-background-target-{Guid.NewGuid():N}");
        var targetName = Path.GetFileNameWithoutExtension(targetPath);
        var delayedScriptPath = Path.Combine(context.Root, "start-target-later.ps1");
        var scriptPath = Path.Combine(context.Root, "start-background-target.ps1");
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var quotedTarget = targetPath.Replace("'", "''");
        await File.WriteAllTextAsync(delayedScriptPath,
            $"Start-Sleep -Milliseconds 1200\nStart-Process -FilePath '{quotedTarget}' -ArgumentList @('/c','ping -n 30 127.0.0.1 > nul')\n");
        var quotedDelayedScript = delayedScriptPath.Replace("'", "''");
        await File.WriteAllTextAsync(scriptPath,
            $"Start-Process -FilePath '{powershell.Replace("'", "''")}' -ArgumentList @('-NoLogo','-NoProfile','-File','{quotedDelayedScript}')\n");

        using var existing = StartProbe(targetPath);
        try
        {
            await Task.Delay(300);
            var action = Action(ActionTypeIds.ScriptRun, new JsonObject
            {
                [ActionParameterNames.ScriptPath] = scriptPath,
                [ActionParameterNames.ScriptType] = ScriptTypeIds.PowerShell,
                [ActionParameterNames.WaitForExit] = true,
                [ActionParameterNames.ProcessName] = targetName,
                [ActionParameterNames.ExecutablePath] = targetPath,
                [ActionParameterNames.WaitForProcessStart] = false,
                [ActionParameterNames.ProcessStartWaitSeconds] = 5
            });
            action.RestoreBehavior = ActionRestoreBehavior.CloseIfStartedBySwitchBoard;
            var profile = new ProfileDefinition
            {
                CategoryId = Guid.NewGuid(), Name = "Script background process tracking test", Actions = [action]
            };

            var session = await context.Runner.RunAsync(profile);
            Assert.Equal(ExecutionSessionStatus.Completed, session.Status);

            PersistentExecutionSession? pending = null;
            HashSet<int> trackedIds = [];
            var deadline = DateTime.UtcNow.AddSeconds(7);
            while (DateTime.UtcNow < deadline && trackedIds.Count == 0)
            {
                pending = await context.SessionRepository.GetLatestPendingAsync(profile.Id);
                var state = pending?.Actions.Single().PreviousState;
                trackedIds = (state?["launchedProcesses"] as JsonArray)?.OfType<JsonObject>()
                    .Select(item => item["processId"]?.GetValue<int>() ?? 0)
                    .Where(id => id > 0).ToHashSet() ?? [];
                if (trackedIds.Count == 0) await Task.Delay(100);
            }

            Assert.NotNull(pending);
            Assert.NotEmpty(trackedIds);
            Assert.DoesNotContain(existing!.Id, trackedIds);

            await new ProfileRestoreRunner(context.Registry, context.SessionRepository).RunAsync(pending!);
            await Task.Delay(300);
            Assert.True(IsAlive(existing.Id));
            Assert.All(trackedIds, id => Assert.False(IsAlive(id)));
        }
        finally
        {
            Kill(existing);
            foreach (var process in Process.GetProcessesByName(targetName)) Kill(process);
        }
    }

    private static bool IsAlive(int processId)
    {
        try { using var process = Process.GetProcessById(processId); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static string CreateProbe(string root, string name)
    {
        var path = Path.Combine(root, $"{name}.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), path);
        return path;
    }

    private static Process? StartProbe(string path) => Process.Start(new ProcessStartInfo(path)
    {
        UseShellExecute = false,
        Arguments = "/c ping -n 30 127.0.0.1 > nul"
    });

    private static void Kill(Process? process)
    {
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
    }
}
