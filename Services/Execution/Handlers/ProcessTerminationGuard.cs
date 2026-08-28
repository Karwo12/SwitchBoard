using System.Diagnostics;
using SwitchBoard.Services.Logging;

namespace SwitchBoard.Services.Execution.Handlers;

/// <summary>
/// Last line of defence before a process termination. Process selection is performed by
/// <see cref="ProcessTargetResolver"/>, but persisted identities and launch tracking can
/// bypass a fresh lookup, so every destructive path must pass through this guard.
/// </summary>
internal static class ProcessTerminationGuard
{
    private static readonly int CurrentProcessId = Process.GetCurrentProcess().Id;

    public static bool IsCurrentProcessId(int processId) => processId == CurrentProcessId;

    public static bool TryPrepareForKill(Process process, string requestedProcessName,
        string? requestedExecutablePath, IAppLogger? logger, string operation, out string? error)
    {
        var processId = TryReadId(process);
        var actualProcessName = TryReadName(process);
        string? actualExecutablePath = null;
        var pathAvailable = RuntimeProcessIdentityService.TryReadPath(process, out actualExecutablePath);

        logger?.Info("ProcessTermination",
            $"BEFORE Kill Operation={Sanitize(operation)} " +
            $"RequestedProcessName={Sanitize(requestedProcessName)} " +
            $"OptionalExecutablePath={Sanitize(requestedExecutablePath)} " +
            $"PID={processId?.ToString() ?? "unknown"} " +
            $"ActualProcessName={Sanitize(actualProcessName)} " +
            $"ActualExecutablePath={Sanitize(pathAvailable ? actualExecutablePath : "unavailable")}");

        if (processId is { } id && IsCurrentProcessId(id))
        {
            error = $"Refusing to terminate the current SwitchBoard process (PID {id}).";
            logger?.Warning("ProcessTermination", error);
            return false;
        }

        if (processId is null)
        {
            error = "Refusing to terminate a process whose PID could not be verified.";
            logger?.Warning("ProcessTermination", error);
            return false;
        }

        error = null;
        return true;
    }

    private static int? TryReadId(Process process)
    {
        try { return process.Id; }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                           System.ComponentModel.Win32Exception or NotSupportedException)
        { return null; }
    }

    private static string? TryReadName(Process process)
    {
        try { return process.ProcessName; }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
                                           System.ComponentModel.Win32Exception or NotSupportedException)
        { return null; }
    }

    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "n/a";
        var sanitized = value.Replace('\r', ' ').Replace('\n', ' ');
        return sanitized.Length <= 300 ? sanitized : sanitized[..300] + "…";
    }
}
