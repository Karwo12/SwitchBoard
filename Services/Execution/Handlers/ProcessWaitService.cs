using System.Diagnostics;

namespace SwitchBoard.Services.Execution.Handlers;

/// <summary>
/// Small asynchronous process discovery helper shared by launch-time process configuration.
/// It always performs an immediate lookup and only then starts polling.
/// </summary>
public static class ProcessWaitService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    public static async Task<Process?> WaitForStartAsync(
        string processName,
        string? executablePath,
        int? processIdHint,
        TimeSpan maximumWait,
        CancellationToken cancellationToken)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matches = ProcessTargetResolver.Find(processName, executablePath, processIdHint);
            var selected = matches.FirstOrDefault();
            foreach (var extra in matches.Skip(1)) extra.Dispose();
            if (selected is not null) return selected;
            if (maximumWait <= TimeSpan.Zero || deadline.Elapsed >= maximumWait) return null;
            var remaining = maximumWait - deadline.Elapsed;
            await Task.Delay(remaining < PollInterval ? remaining : PollInterval, cancellationToken);
        }
    }
}
