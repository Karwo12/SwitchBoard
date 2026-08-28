using SwitchBoard.RuntimeTests.TestInfrastructure;

namespace SwitchBoard.RuntimeTests.Actions;

[Collection("Windows runtime")]
public sealed class ProcessLaunchTrackerTests : RuntimeTestBase
{
    [Fact]
    [Trait("Category", "Integration")]
    [Trait("Platform", "Windows")]
    public async Task ProcessLaunchTracker_TracksOnlyInstancesCreatedAfterCapture()
    {
        using var context = new RuntimeTestContext();
        var powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var targetPath = Path.Combine(context.Root, $"tracked-target-{Guid.NewGuid():N}.exe");
        File.Copy(powershell, targetPath);
        var processName = Path.GetFileNameWithoutExtension(targetPath);
        using var existing = StartSleep(targetPath);
        Process? launched = null;

        try
        {
            await Task.Delay(300);
            var captured = ProcessLaunchTracker.Capture(processName, targetPath);
            launched = StartSleep(targetPath);
            var identities = await ProcessLaunchTracker.TrackAsync(captured, processName, targetPath,
                TimeSpan.FromSeconds(5), CancellationToken.None);

            captured["startedBySwitchBoard"] = true;
            captured["launchedProcesses"] = identities;
            var trackedIds = identities.OfType<JsonObject>()
                .Select(item => item["processId"]?.GetValue<int>() ?? 0).ToHashSet();

            Assert.Contains(launched.Id, trackedIds);
            Assert.DoesNotContain(existing.Id, trackedIds);
            var close = await ProcessLaunchTracker.CloseAsync(captured, CancellationToken.None);

            Assert.True(close.IsSuccessful);
            Assert.False(IsAlive(launched.Id));
            Assert.True(IsAlive(existing.Id));
        }
        finally
        {
            TryKill(launched);
            TryKill(existing);
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    public async Task ProcessLaunchTracker_CloseWithoutSavedProcess_IsNoOp()
    {
        var result = await ProcessLaunchTracker.CloseAsync(new JsonObject(), CancellationToken.None);

        Assert.True(result.IsSuccessful);
    }

    private static Process StartSleep(string path)
    {
        var startInfo = new ProcessStartInfo(path) { UseShellExecute = false };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("Start-Sleep -Seconds 30");
        return Process.Start(startInfo)!;
    }

    private static bool IsAlive(int processId)
    {
        try { using var process = Process.GetProcessById(processId); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    private static void TryKill(Process? process)
    {
        if (process is null) return;
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) { }
        finally { process.Dispose(); }
    }
}
