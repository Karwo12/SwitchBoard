using SwitchBoard.RuntimeTests.TestInfrastructure;

namespace SwitchBoard.RuntimeTests.Execution;

public sealed class ProfileRunnerBoundaryTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public async Task HandlerExceptionBecomesFailureAndTheNextActionStillRuns()
    {
        using var context = new RuntimeTestContext();
        var logger = new RecordingLogger();
        var registry = new ActionRegistry([new ThrowingHandler(), new DelayActionHandler()]);
        var runner = new ProfileRunner(registry, context.SessionRepository, logger);
        var throwing = RuntimeTestContext.Action(ThrowingHandler.TypeId, [], ActionFailurePolicy.Continue);
        throwing.SortOrder = 1;
        var next = RuntimeTestContext.Action(ActionTypeIds.Delay,
            new JsonObject { [ActionParameterNames.DelaySeconds] = 0 });
        next.SortOrder = 2;
        var profile = new ProfileDefinition { Name = "Handler boundary", Actions = [throwing, next] };

        var session = await runner.RunAsync(profile);

        Assert.Equal(ExecutionSessionStatus.CompletedWithErrors, session.Status);
        Assert.Equal(ActionJournalStatus.Failed, session.Journal[0].Status);
        Assert.Equal(ActionJournalStatus.Success, session.Journal[1].Status);
        Assert.Contains(logger.Messages, message => message.Contains("BEFORE action Index=1", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message => message.Contains("AFTER action Index=1", StringComparison.Ordinal) &&
                                                    message.Contains("InvalidOperationException", StringComparison.Ordinal));
        Assert.Contains(logger.Messages, message => message.Contains("AFTER action Index=2", StringComparison.Ordinal));
    }

    private sealed class ThrowingHandler : IActionHandler
    {
        public const string TypeId = "test.throwing";
        public string ActionType => TypeId;

        public Task<ActionExecutionResult> ExecuteAsync(ActionDefinition action, ActionExecutionContext context,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Intentional handler exception.");

        public Task<ActionExecutionResult> RestoreAsync(ActionDefinition action, JsonObject restoreState,
            ActionExecutionContext context, CancellationToken cancellationToken) =>
            Task.FromResult(ActionExecutionResult.Skipped("Nothing to restore."));
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
