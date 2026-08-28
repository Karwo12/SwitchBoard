using SwitchBoard.RuntimeTests.TestInfrastructure;

namespace SwitchBoard.RuntimeTests.ProcessSafety;

public sealed class ProcessTerminationSafetyTests
{
    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Platform", "Windows")]
    public void ProcessResolver_NeverReturnsTheCurrentSwitchBoardProcess()
    {
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        var lookup = ProcessTargetResolver.FindWithDiagnostics(current.ProcessName);

        try
        {
            Assert.DoesNotContain(lookup.Processes, process => process.Id == current.Id);
            Assert.DoesNotContain(lookup.PathUnverifiedProcesses, process => process.Id == current.Id);
        }
        finally
        {
            lookup.DisposeAll();
        }
    }

    [Fact]
    [Trait("Category", "Unit")]
    [Trait("Platform", "Windows")]
    public async Task ProcessLaunchTracker_RefusesToTerminateTheCurrentProcessIdentity()
    {
        using var current = System.Diagnostics.Process.GetCurrentProcess();
        var identity = RuntimeProcessIdentityService.TryCapture(current);
        Assert.NotNull(identity);
        var logger = new RecordingLogger();
        var restoreState = new JsonObject
        {
            ["targetProcessName"] = identity!.ProcessName,
            ["startedBySwitchBoard"] = true,
            ["launchedProcesses"] = new JsonArray(RuntimeProcessIdentityService.ToJson(identity))
        };

        var result = await ProcessLaunchTracker.CloseAsync(restoreState, CancellationToken.None, logger);

        Assert.False(result.IsSuccessful);
        Assert.Contains("current SwitchBoard process", result.Message, StringComparison.Ordinal);
        Assert.Contains(logger.Messages, message => message.Contains("BEFORE Kill", StringComparison.Ordinal));
        Assert.False(current.HasExited);
    }

    private sealed class RecordingLogger : IAppLogger
    {
        public List<string> Messages { get; } = [];
        public void Info(string area, string message) => Messages.Add($"INFO {area}: {message}");
        public void Warning(string area, string message) => Messages.Add($"WARNING {area}: {message}");
        public void Error(string area, Exception exception, string? message = null) =>
            Messages.Add($"ERROR {area}: {message} {exception.Message}");
    }
}
