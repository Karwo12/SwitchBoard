using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace SwitchBoard.Services.Discovery;

public sealed class WindowsProcessDiscoveryService : IProcessDiscoveryService
{
    public Task<IReadOnlyList<ProcessCandidate>> GetProcessesAsync(
        CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<ProcessCandidate>>(
            () => ScanProcesses(cancellationToken),
            cancellationToken);

    private static IReadOnlyList<ProcessCandidate> ScanProcesses(CancellationToken cancellationToken)
    {
        var results = new List<ProcessCandidate>();
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return results;
        }

        foreach (var process in processes)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var processName = TryRead(() => process.ProcessName);
                if (string.IsNullOrWhiteSpace(processName))
                {
                    continue;
                }

                var processId = TryRead(() => process.Id, -1);
                var windowTitle = TryRead(() => process.MainWindowTitle);
                var executablePath = TryRead(() => process.MainModule?.FileName);
                var executableName = !string.IsNullOrWhiteSpace(executablePath)
                    ? Path.GetFileName(executablePath)
                    : $"{processName}.exe";
                var fileDescription = TryRead(() =>
                    string.IsNullOrWhiteSpace(executablePath)
                        ? null
                        : FileVersionInfo.GetVersionInfo(executablePath).FileDescription);
                var displayName = FirstNonEmpty(windowTitle, fileDescription, processName);
                var suggestedName = FirstNonEmpty(fileDescription, windowTitle, processName);

                results.Add(new ProcessCandidate(
                    processId,
                    processName,
                    executableName,
                    executablePath,
                    windowTitle,
                    displayName,
                    suggestedName,
                    FileIconProvider.TryGetSmallIcon(executablePath)));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or
                                               NotSupportedException or ArgumentException)
            {
                // A protected or terminating process must not abort the remaining scan.
            }
            finally
            {
                process.Dispose();
            }
        }

        return results
            .OrderBy(candidate => candidate.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(candidate => candidate.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(candidate => candidate.ProcessId)
            .ToList();
    }

    private static T? TryRead<T>(Func<T?> reader)
    {
        try
        {
            return reader();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or
                                           NotSupportedException or ArgumentException)
        {
            return default;
        }
    }

    private static T TryRead<T>(Func<T> reader, T fallback)
    {
        try
        {
            return reader();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or
                                           NotSupportedException or ArgumentException)
        {
            return fallback;
        }
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
}
