using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security;

namespace SwitchBoard.Services.Discovery;

public sealed class WindowsProgramDiscoveryService : IProgramDiscoveryService
{
    private const int CommonLocationResultLimit = 6_000;
    private const int SystemDriveResultLimit = 12_000;
    private const int BatchSize = 24;

    public Task SearchAsync(
        ProgramSearchMode mode,
        IProgress<ProgramDiscoveryProgress> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);
        return Task.Run(
            () => Scan(mode, progress, cancellationToken),
            cancellationToken);
    }

    private static void Scan(
        ProgramSearchMode mode,
        IProgress<ProgramDiscoveryProgress> progress,
        CancellationToken cancellationToken)
    {
        var runningProcessNames = GetRunningProcessNames();
        var discoveredTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var batch = new List<ProgramCandidate>(BatchSize);
        var scannedFileCount = 0;
        var resultLimit = mode == ProgramSearchMode.SystemDrive
            ? SystemDriveResultLimit
            : CommonLocationResultLimit;

        foreach (var root in GetSearchRoots(mode))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (discoveredTargets.Count >= resultLimit)
            {
                break;
            }

            foreach (var path in EnumerateProgramFiles(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                scannedFileCount++;

                var candidate = TryCreateCandidate(path, runningProcessNames);
                if (candidate is not null && discoveredTargets.Add(candidate.TargetPath))
                {
                    batch.Add(candidate);
                }

                if (batch.Count >= BatchSize || scannedFileCount % 200 == 0)
                {
                    Report(progress, root.Path, scannedFileCount, batch);
                }

                if (discoveredTargets.Count >= resultLimit)
                {
                    break;
                }
            }

            Report(progress, root.Path, scannedFileCount, batch);
        }

        Report(progress, string.Empty, scannedFileCount, batch);
    }

    private static IReadOnlyList<SearchRoot> GetSearchRoots(ProgramSearchMode mode)
    {
        if (mode == ProgramSearchMode.SystemDrive)
        {
            var systemDrive = Path.GetPathRoot(Environment.SystemDirectory);
            return string.IsNullOrWhiteSpace(systemDrive)
                ? []
                : [new SearchRoot(systemDrive, true)];
        }

        var roots = new List<SearchRoot>();
        AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), true);
        AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), true);

        var pathDirectories = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var pathDirectory in pathDirectories)
        {
            AddRoot(roots, pathDirectory.Trim('"'), false);
        }

        AddRoot(roots, Environment.SystemDirectory, false);
        AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.Windows), false);
        AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), true);
        AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), true);
        AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), true);
        AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), true);

        return roots;
    }

    private static void AddRoot(List<SearchRoot> roots, string? path, bool recurse)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        if (!Directory.Exists(fullPath) || roots.Any(root =>
                string.Equals(root.Path, fullPath, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        roots.Add(new SearchRoot(fullPath, recurse));
    }

    private static IEnumerable<string> EnumerateProgramFiles(SearchRoot root)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = root.Recurse,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.ReparsePoint
        };

        foreach (var pattern in new[] { "*.exe", "*.lnk" })
        {
            IEnumerator<string>? enumerator = null;
            try
            {
                enumerator = Directory.EnumerateFiles(root.Path, pattern, options).GetEnumerator();
                while (true)
                {
                    string current;
                    try
                    {
                        if (!enumerator.MoveNext())
                        {
                            break;
                        }

                        current = enumerator.Current;
                    }
                    catch (Exception exception) when (IsFileSystemException(exception))
                    {
                        break;
                    }

                    yield return current;
                }
            }
            finally
            {
                enumerator?.Dispose();
            }
        }
    }

    private static ProgramCandidate? TryCreateCandidate(
        string sourcePath,
        IReadOnlySet<string> runningProcessNames)
    {
        try
        {
            var targetPath = sourcePath;
            string? shortcutWorkingDirectory = null;
            if (string.Equals(Path.GetExtension(sourcePath), ".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var shortcut = TryResolveShortcut(sourcePath);
                if (!string.IsNullOrWhiteSpace(shortcut.TargetPath) && File.Exists(shortcut.TargetPath))
                {
                    targetPath = Path.GetFullPath(shortcut.TargetPath);
                    shortcutWorkingDirectory = shortcut.WorkingDirectory;
                }
            }

            var executableName = Path.GetFileName(targetPath);
            if (string.IsNullOrWhiteSpace(executableName))
            {
                return null;
            }

            var displayName = GetFriendlyName(targetPath, sourcePath);
            var workingDirectory = !string.IsNullOrWhiteSpace(shortcutWorkingDirectory) &&
                                   Directory.Exists(shortcutWorkingDirectory)
                ? Path.GetFullPath(shortcutWorkingDirectory)
                : Path.GetDirectoryName(targetPath) ?? string.Empty;
            var processName = Path.GetFileNameWithoutExtension(targetPath);

            return new ProgramCandidate(
                displayName,
                executableName,
                targetPath,
                workingDirectory,
                runningProcessNames.Contains(processName),
                FileIconProvider.TryGetSmallIcon(targetPath));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or SecurityException or
                                           ArgumentException or NotSupportedException or PathTooLongException or
                                           COMException)
        {
            return null;
        }
    }

    internal static string GetFriendlyName(string targetPath, string? fallbackPath = null)
    {
        if (string.Equals(Path.GetExtension(targetPath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var version = FileVersionInfo.GetVersionInfo(targetPath);
                var metadataName = FirstNonEmpty(version.FileDescription, version.ProductName);
                if (!string.IsNullOrWhiteSpace(metadataName))
                {
                    return metadataName.Trim();
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               ArgumentException or NotSupportedException)
            {
                // Fall back to a file name below.
            }
        }

        return Path.GetFileNameWithoutExtension(fallbackPath ?? targetPath);
    }

    private static ShortcutTarget TryResolveShortcut(string shortcutPath)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return default;
            }

            shell = Activator.CreateInstance(shellType);
            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                binder: null,
                shell,
                [shortcutPath]);
            if (shortcut is null)
            {
                return default;
            }

            var shortcutType = shortcut.GetType();
            var targetPath = shortcutType.InvokeMember(
                "TargetPath",
                BindingFlags.GetProperty,
                binder: null,
                shortcut,
                null) as string;
            var workingDirectory = shortcutType.InvokeMember(
                "WorkingDirectory",
                BindingFlags.GetProperty,
                binder: null,
                shortcut,
                null) as string;
            return new ShortcutTarget(targetPath, workingDirectory);
        }
        catch (Exception exception) when (exception is COMException or TargetInvocationException or
                                           MemberAccessException or ArgumentException)
        {
            return default;
        }
        finally
        {
            ReleaseComObject(shortcut);
            ReleaseComObject(shell);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.FinalReleaseComObject(value);
        }
    }

    private static HashSet<string> GetRunningProcessNames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch
        {
            return names;
        }

        foreach (var process in processes)
        {
            try
            {
                names.Add(process.ProcessName);
            }
            catch
            {
                // Protected and terminating processes are irrelevant to this optional hint.
            }
            finally
            {
                process.Dispose();
            }
        }

        return names;
    }

    private static void Report(
        IProgress<ProgramDiscoveryProgress> progress,
        string location,
        int scannedFileCount,
        List<ProgramCandidate> batch)
    {
        var items = batch.Count == 0 ? [] : batch.ToArray();
        batch.Clear();
        progress.Report(new ProgramDiscoveryProgress(location, scannedFileCount, items));
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static bool IsFileSystemException(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or SecurityException or
            ArgumentException or NotSupportedException;

    private sealed record SearchRoot(string Path, bool Recurse);

    private readonly record struct ShortcutTarget(string? TargetPath, string? WorkingDirectory);
}
