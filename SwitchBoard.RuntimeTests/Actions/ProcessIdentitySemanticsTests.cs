using SwitchBoard.RuntimeTests.TestInfrastructure;

namespace SwitchBoard.RuntimeTests.Actions;

[Collection("Windows runtime")]
public sealed class ProcessIdentitySemanticsTests : RuntimeTestBase
{
    [Fact]
    [Trait("Category", "Unit")]
    public void PersistentTarget_UsesExactNormalizedNameAndOptionalPath()
    {
        const string targetPath = @"C:\Apps\AnyDesk.exe";

        Assert.Equal("AnyDesk", ProcessTargetResolver.NormalizeName(" AnyDesk.exe "));
        Assert.Equal("client.preview", ProcessTargetResolver.NormalizeName("client.preview"));
        Assert.Equal(ProcessTargetMatch.Match,
            ProcessTargetResolver.MatchesSnapshot("AnyDesk", null, "AnyDesk.exe", null, false));
        Assert.Equal(ProcessTargetMatch.NoMatch,
            ProcessTargetResolver.MatchesSnapshot("AnyDesk", null, "AnyDeskHelper.exe", null, false));
        Assert.Equal(ProcessTargetMatch.NoMatch,
            ProcessTargetResolver.MatchesSnapshot("client.preview", null, "client", null, false));
        Assert.Equal(ProcessTargetMatch.Match,
            ProcessTargetResolver.MatchesSnapshot("AnyDesk.exe", targetPath, "anydesk", targetPath, true));
        Assert.Equal(ProcessTargetMatch.NoMatch,
            ProcessTargetResolver.MatchesSnapshot("AnyDesk", targetPath, "AnyDesk", @"D:\Apps\AnyDesk.exe", true));
        Assert.Throws<InvalidOperationException>(() =>
            ProcessTargetResolver.NormalizeConfiguredPath("AnyDesk.exe"));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void RuntimeIdentity_RejectsPidReuseAndVerifiesReadablePath()
    {
        var saved = new RuntimeProcessIdentity(4217, 0, 638900000000000000,
            @"C:\Apps\AnyDesk.exe", "AnyDesk");
        var same = new RuntimeProcessIdentity(4217, 0, saved.StartedAtUtcTicks,
            @"C:\Apps\AnyDesk.exe", "AnyDesk.exe");
        var reusedPid = same with { StartedAtUtcTicks = same.StartedAtUtcTicks + 1 };

        Assert.True(RuntimeProcessIdentityService.SameInstance(saved, same));
        Assert.False(RuntimeProcessIdentityService.SameInstance(saved, reusedPid));
        Assert.False(RuntimeProcessIdentityService.MatchesSnapshot(saved, saved.ProcessId,
            reusedPid.StartedAtUtcTicks, saved.ProcessName, saved.ExecutablePath, true));
        Assert.False(RuntimeProcessIdentityService.MatchesSnapshot(saved, saved.ProcessId,
            saved.StartedAtUtcTicks, saved.ProcessName, @"D:\Apps\AnyDesk.exe", true));
        Assert.True(RuntimeProcessIdentityService.WasPresentBefore(same, [saved]));
        Assert.False(RuntimeProcessIdentityService.WasPresentBefore(reusedPid, [saved]));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void InaccessibleExecutablePath_HasOneConservativePolicy()
    {
        const string targetPath = @"C:\Apps\AnyDesk.exe";
        var identity = new RuntimeProcessIdentity(4217, 0, 638900000000000000,
            targetPath, "AnyDesk");

        Assert.Equal(ProcessTargetMatch.PathUnavailable,
            ProcessTargetResolver.MatchesSnapshot("AnyDesk", targetPath, "AnyDesk", null, false));
        Assert.True(RuntimeProcessIdentityService.MatchesSnapshot(identity, identity.ProcessId,
            identity.StartedAtUtcTicks, identity.ProcessName, null, false));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProgramAndScriptLaunchTracking_UseTheSameTargetInsteadOfTheUri()
    {
        const string uri = "switchboard-test://launch/application";
        const string configuredName = "AnyDesk.exe";
        const string configuredPath = @"C:\Apps\AnyDesk.exe";
        var context = new ActionExecutionContext(Guid.NewGuid(), Guid.NewGuid());
        var program = Action(ActionTypeIds.ProgramRun, new JsonObject
        {
            [ActionParameterNames.Target] = uri,
            [ActionParameterNames.ProcessName] = configuredName,
            [ActionParameterNames.ExecutablePath] = configuredPath
        });
        program.RestoreBehavior = ActionRestoreBehavior.CloseIfStartedBySwitchBoard;
        var script = Action(ActionTypeIds.ScriptRun, new JsonObject
        {
            [ActionParameterNames.ProcessName] = configuredName,
            [ActionParameterNames.ExecutablePath] = configuredPath
        });
        script.RestoreBehavior = ActionRestoreBehavior.CloseIfStartedBySwitchBoard;

        var programState = await new ProgramRunActionHandler().CaptureStateAsync(
            program, context, CancellationToken.None);
        var scriptState = await new ScriptRunActionHandler().CaptureStateAsync(
            script, context, CancellationToken.None);

        Assert.NotNull(programState);
        Assert.NotNull(scriptState);
        Assert.Equal("AnyDesk", programState!["targetProcessName"]?.GetValue<string>());
        Assert.Equal(programState["targetProcessName"]?.GetValue<string>(),
            scriptState!["targetProcessName"]?.GetValue<string>());
        Assert.Equal(Path.GetFullPath(configuredPath),
            programState["targetExecutablePath"]?.GetValue<string>());
        Assert.Equal(programState["targetExecutablePath"]?.GetValue<string>(),
            scriptState["targetExecutablePath"]?.GetValue<string>());
        Assert.NotEqual(uri, programState["targetExecutablePath"]?.GetValue<string>());
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task Resolver_FindsEveryExactPathMatchAndNeverTrustsPidHintAlone()
    {
        using var context = new RuntimeTestContext();
        var processName = $"sbi{Guid.NewGuid():N}"[..11];
        var firstDirectory = Path.Combine(context.Root, "first");
        var secondDirectory = Path.Combine(context.Root, "second");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        var firstPath = Path.Combine(firstDirectory, $"{processName}.exe");
        var secondPath = Path.Combine(secondDirectory, $"{processName}.exe");
        File.Copy(GetPowerShellPath(), firstPath);
        File.Copy(GetPowerShellPath(), secondPath);
        using var first = StartSleep(firstPath);
        using var second = StartSleep(firstPath);
        using var sameNameWrongPath = StartSleep(secondPath);

        try
        {
            await Task.Delay(350);

            var wrongNameHint = ProcessTargetResolver.FindWithDiagnostics(
                processName, firstPath, Environment.ProcessId);
            try
            {
                Assert.Equal(new[] { first.Id, second.Id }.Order().ToArray(),
                    wrongNameHint.Processes.Select(process => process.Id).Order().ToArray());
            }
            finally { wrongNameHint.DisposeAll(); }

            var wrongPathHint = ProcessTargetResolver.FindWithDiagnostics(
                processName, firstPath, sameNameWrongPath.Id);
            try
            {
                Assert.Equal(new[] { first.Id, second.Id }.Order().ToArray(),
                    wrongPathHint.Processes.Select(process => process.Id).Order().ToArray());
                Assert.DoesNotContain(wrongPathHint.Processes,
                    process => process.Id == sameNameWrongPath.Id);
            }
            finally { wrongPathHint.DisposeAll(); }
        }
        finally
        {
            TryKill(first);
            TryKill(second);
            TryKill(sameNameWrongPath);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task WaitProcess_UsesTheSameExactPathSemantics()
    {
        using var context = new RuntimeTestContext();
        var processName = $"sbw{Guid.NewGuid():N}"[..11];
        var targetDirectory = Path.Combine(context.Root, "target");
        var otherDirectory = Path.Combine(context.Root, "other");
        Directory.CreateDirectory(targetDirectory);
        Directory.CreateDirectory(otherDirectory);
        var targetPath = Path.Combine(targetDirectory, $"{processName}.exe");
        var otherPath = Path.Combine(otherDirectory, $"{processName}.exe");
        File.Copy(GetPowerShellPath(), targetPath);
        File.Copy(GetPowerShellPath(), otherPath);
        using var other = StartSleep(otherPath);
        Process? target = null;
        var action = Action(ActionTypeIds.WaitProcessStart, new JsonObject
        {
            [ActionParameterNames.ProcessName] = processName,
            [ActionParameterNames.ExecutablePath] = targetPath
        });
        var handler = new WaitProcessActionHandler(ActionTypeIds.WaitProcessStart);

        try
        {
            await Task.Delay(250);
            using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(350)))
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handler.ExecuteAsync(
                    action, new(Guid.NewGuid(), Guid.NewGuid()), cancellation.Token));

            target = StartSleep(targetPath);
            using var successTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var result = await handler.ExecuteAsync(action,
                new(Guid.NewGuid(), Guid.NewGuid()), successTimeout.Token);
            Assert.True(result.IsSuccessful && !result.IsSkipped);
        }
        finally
        {
            TryKill(target);
            TryKill(other);
            target?.Dispose();
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task Resolver_ProcessDisappearingBeforeHintLookup_DoesNotCrash()
    {
        using var process = Process.Start(new ProcessStartInfo(GetPowerShellPath())
        {
            UseShellExecute = false,
            ArgumentList = { "-NoProfile", "-Command", "exit 0" }
        });
        Assert.NotNull(process);
        var processId = process!.Id;
        await process.WaitForExitAsync();

        var lookup = ProcessTargetResolver.FindWithDiagnostics(
            $"missing-{Guid.NewGuid():N}", null, processId);
        try
        {
            Assert.Empty(lookup.Processes);
            Assert.Null(lookup.ErrorMessage);
        }
        finally { lookup.DisposeAll(); }
    }

    private static string GetPowerShellPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        "System32", "WindowsPowerShell", "v1.0", "powershell.exe");

    private static Process StartSleep(string executablePath)
    {
        var startInfo = new ProcessStartInfo(executablePath) { UseShellExecute = false };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("Start-Sleep -Seconds 30");
        return Process.Start(startInfo)!;
    }

    private static void TryKill(Process? process)
    {
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) { }
    }
}
