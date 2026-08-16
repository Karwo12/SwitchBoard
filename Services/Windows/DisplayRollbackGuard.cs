using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.IO;

namespace SwitchBoard.Services.Windows;

public static class DisplayRollbackGuard
{
    private const string CommandName = "--display-rollback-guard";

    public static bool TryRun(string[] arguments)
    {
        if (arguments.Length != 5 || !string.Equals(arguments[0], CommandName, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var timeoutSeconds = int.Parse(arguments[3], System.Globalization.CultureInfo.InvariantCulture);
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(arguments[4]));
            var state = JsonSerializer.Deserialize<DisplayModeState>(json)
                        ?? throw new InvalidDataException("Display rollback state is empty.");
            using var completionEvent = EventWaitHandle.OpenExisting(arguments[1]);
            using var readyEvent = EventWaitHandle.OpenExisting(arguments[2]);
            readyEvent.Set();
            if (!completionEvent.WaitOne(TimeSpan.FromSeconds(timeoutSeconds)))
            {
                new WindowsDisplayManager().PersistAsync(state).GetAwaiter().GetResult();
            }
        }
        catch
        {
            // The guard has no UI. The main process still performs its own in-process rollback.
        }

        return true;
    }

    public static DisplayRollbackGuardSession Start(DisplayModeState previousState, TimeSpan timeout)
    {
        var eventName = $"Local\\SwitchBoard.DisplayRollback.{Guid.NewGuid():N}";
        var readyEventName = $"Local\\SwitchBoard.DisplayRollbackReady.{Guid.NewGuid():N}";
        var completionEvent = new EventWaitHandle(false, EventResetMode.ManualReset, eventName);
        var readyEvent = new EventWaitHandle(false, EventResetMode.ManualReset, readyEventName);
        Process? process = null;
        try
        {
            var executablePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(executablePath) &&
                string.Equals(Path.GetFileNameWithoutExtension(executablePath), "SwitchBoard", StringComparison.OrdinalIgnoreCase))
            {
                var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(previousState)));
                var startInfo = new ProcessStartInfo(executablePath) { UseShellExecute = false, CreateNoWindow = true };
                startInfo.ArgumentList.Add(CommandName);
                startInfo.ArgumentList.Add(eventName);
                startInfo.ArgumentList.Add(readyEventName);
                startInfo.ArgumentList.Add(Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture));
                startInfo.ArgumentList.Add(payload);
                process = Process.Start(startInfo);
                readyEvent.WaitOne(TimeSpan.FromSeconds(2));
            }

            return new DisplayRollbackGuardSession(completionEvent, readyEvent, process);
        }
        catch
        {
            process?.Dispose();
            completionEvent.Dispose();
            readyEvent.Dispose();
            return DisplayRollbackGuardSession.Empty;
        }
    }
}

public sealed class DisplayRollbackGuardSession : IDisposable
{
    private readonly EventWaitHandle? _completionEvent;
    private readonly Process? _process;
    private readonly EventWaitHandle? _readyEvent;
    private int _completed;

    internal DisplayRollbackGuardSession(EventWaitHandle completionEvent, EventWaitHandle readyEvent, Process? process)
    {
        _completionEvent = completionEvent;
        _readyEvent = readyEvent;
        _process = process;
    }

    private DisplayRollbackGuardSession()
    {
    }

    public static DisplayRollbackGuardSession Empty { get; } = new();

    public void Complete()
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
        {
            try { _completionEvent?.Set(); } catch { }
        }
    }

    public bool ProtectionExpired
    {
        get
        {
            if (_process is null || Volatile.Read(ref _completed) != 0) return false;
            try { return _process.HasExited; } catch { return true; }
        }
    }

    public void Dispose()
    {
        Complete();
        _completionEvent?.Dispose();
        _readyEvent?.Dispose();
        _process?.Dispose();
    }
}
