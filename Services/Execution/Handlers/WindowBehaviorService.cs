using SwitchBoard.Models.Actions;

namespace SwitchBoard.Services.Execution.Handlers;

internal static class WindowBehaviorService
{
    public static async Task<bool> ApplyAsync(string processName, string? executablePath,
        string behavior, int waitSeconds, CancellationToken cancellationToken)
    {
        var maximumWait = TimeSpan.FromSeconds(Math.Clamp(waitSeconds, 1, 300));
        var deadline = DateTime.UtcNow + maximumWait;
        var found = false;
        var hideSettleDeadline = DateTime.MinValue;
        var appliedHandles = new HashSet<nint>();

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var windows = WindowInterop.FindWindows(processName, executablePath, WindowMatchModeIds.Any, string.Empty);
            foreach (var window in windows)
            {
                found = true;
                if (!appliedHandles.Add(window.Handle) && behavior != WindowBehaviorIds.Hide) continue;
                try { WindowInterop.ApplyBehavior(window.Handle, behavior); }
                catch (InvalidOperationException) when (behavior == WindowBehaviorIds.Hide) { }
                catch (ArgumentException) when (behavior == WindowBehaviorIds.Hide) { }
            }

            if (found)
            {
                if (behavior != WindowBehaviorIds.Hide) return true;
                if (hideSettleDeadline == DateTime.MinValue)
                    hideSettleDeadline = DateTime.UtcNow.AddMilliseconds(1500);
                if (DateTime.UtcNow >= hideSettleDeadline) return true;
            }

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) break;
            await Task.Delay(remaining < TimeSpan.FromMilliseconds(150) ? remaining : TimeSpan.FromMilliseconds(150), cancellationToken);
        }

        return found;
    }
}
