using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using SwitchBoard.Models.Actions;
using SwitchBoard.ViewModels;

namespace SwitchBoard.Services.Discovery;

/// <summary>
/// Resolves only inexpensive, explicitly available icon sources. It never searches program folders
/// or disks: it uses persisted EXE paths, a matching running process, a service ImagePath or the
/// known Steam working directory/registration.
/// </summary>
internal static class ActionIconSourceResolver
{
    private const int SourceCacheCapacity = 128;
    private static readonly Lazy<string?> SteamProtocolIconSource = new(ReadSteamProtocolIconSource);
    private static readonly ConcurrentDictionary<string, Lazy<string?>> RunningProcessSources =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Lazy<string?>> ServiceSources =
        new(StringComparer.OrdinalIgnoreCase);

    public static ActionIconSourceRequest Capture(ActionItemViewModel action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return new ActionIconSourceRequest(action.Type, action.Target, action.TargetType, action.ExecutablePath,
            action.ProcessName, action.WorkingDirectory, action.ServiceName);
    }

    public static Task<string?> ResolveAsync(ActionIconSourceRequest request) =>
        // Registry and process inspection are intentionally kept off the UI thread.  This runs
        // only when an action's icon-relevant source changes, never with the status refresh.
        Task.Run(() => Resolve(request));

    private static string? Resolve(ActionIconSourceRequest request)
    {
        var directSource = GetDirectSource(request);
        if (directSource is not null) return directSource;

        if (request.Type == ActionTypeIds.ServiceSetState && !string.IsNullOrWhiteSpace(request.ServiceName))
        {
            var service = request.ServiceName.Trim();
            TrimSourceCache(ServiceSources);
            return ServiceSources.GetOrAdd(service,
                static name => new Lazy<string?>(() => ReadServiceImagePath(name))).Value;
        }

        if (!SupportsRunningProcessResolution(request.Type) || string.IsNullOrWhiteSpace(request.ProcessName))
            return null;

        var processName = NormalizeProcessName(request.ProcessName);
        if (processName.Length == 0) return null;
        TrimSourceCache(RunningProcessSources);
        return RunningProcessSources.GetOrAdd(processName,
            static name => new Lazy<string?>(() => ReadRunningProcessPath(name))).Value;
    }

    private static string? GetDirectSource(ActionIconSourceRequest request)
    {
        if (request.Type == ActionTypeIds.ProgramRun &&
            string.Equals(request.TargetType, TargetTypeIds.Executable, StringComparison.OrdinalIgnoreCase) &&
            IsLocalIconSource(request.Target))
        {
            return NormalizeLocalPath(request.Target);
        }

        if (SupportsExplicitExecutablePath(request.Type) && IsLocalIconSource(request.ExecutablePath))
            return NormalizeLocalPath(request.ExecutablePath);

        if (request.Type == ActionTypeIds.ProgramRun &&
            string.Equals(request.TargetType, TargetTypeIds.Uri, StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(request.Target, UriKind.Absolute, out var targetUri) &&
            string.Equals(targetUri.Scheme, "steam", StringComparison.OrdinalIgnoreCase))
        {
            return TryGetSteamWorkingDirectoryExecutable(request) ?? SteamProtocolIconSource.Value;
        }

        return null;
    }

    private static bool SupportsExplicitExecutablePath(string type) => type is ActionTypeIds.ProgramRun or
        ActionTypeIds.ProcessConfigure or ActionTypeIds.WaitProcessStart or ActionTypeIds.WaitProcessExit or
        ActionTypeIds.WaitWindow or ActionTypeIds.ScriptRun;

    private static bool SupportsRunningProcessResolution(string type) => type is ActionTypeIds.ProgramRun or
        ActionTypeIds.ProcessConfigure or ActionTypeIds.WaitProcessStart or ActionTypeIds.WaitProcessExit or
        ActionTypeIds.WaitWindow or ActionTypeIds.ScriptRun;

    private static string? TryGetSteamWorkingDirectoryExecutable(ActionIconSourceRequest request)
    {
        var processName = NormalizeProcessName(request.ProcessName);
        if (processName.Length == 0 || string.IsNullOrWhiteSpace(request.WorkingDirectory)) return null;
        try
        {
            if (!Path.IsPathFullyQualified(request.WorkingDirectory)) return null;
            var candidate = Path.Combine(request.WorkingDirectory, processName + ".exe");
            return IsLocalIconSource(candidate) && File.Exists(candidate) ? candidate : null;
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? ReadRunningProcessPath(string processName)
    {
        Process[] processes;
        try { processes = Process.GetProcessesByName(processName); }
        catch { return null; }

        try
        {
            foreach (var process in processes)
            {
                try
                {
                    var path = process.MainModule?.FileName;
                    if (IsLocalIconSource(path) && File.Exists(path)) return path;
                }
                catch { }
            }
            return null;
        }
        finally
        {
            foreach (var process in processes)
            {
                try { process.Dispose(); } catch { }
            }
        }
    }

    private static string? ReadServiceImagePath(string serviceName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            var imagePath = key?.GetValue("ImagePath") as string;
            return ExtractExecutablePath(imagePath);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or
                                         IOException or ArgumentException)
        {
            return null;
        }
    }

    private static string? ReadSteamProtocolIconSource()
    {
        try
        {
            using (var iconKey = Registry.ClassesRoot.OpenSubKey(@"steam\DefaultIcon"))
            {
                var iconSource = ExtractExecutablePath(iconKey?.GetValue(null) as string, allowIco: true);
                if (iconSource is not null) return iconSource;
            }

            // Some Steam installations register only the relative value "steam.exe" in
            // DefaultIcon. The protocol command is still a precise, registry-provided path.
            using (var commandKey = Registry.ClassesRoot.OpenSubKey(@"steam\shell\open\command"))
            {
                var commandSource = ExtractExecutablePath(commandKey?.GetValue(null) as string);
                if (commandSource is not null) return commandSource;
            }

            using var steamKey = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return ExtractExecutablePath(steamKey?.GetValue("SteamExe") as string);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or
                                         IOException or ArgumentException)
        {
            return null;
        }
    }

    private static string? ExtractExecutablePath(string? commandLine, bool allowIco = false)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return null;
        var expanded = Environment.ExpandEnvironmentVariables(commandLine.Trim());
        var candidate = expanded.StartsWith('"')
            ? expanded[1..Math.Max(1, expanded.IndexOf('"', 1))]
            : ExtractUnquotedExecutablePath(expanded);
        if (candidate.StartsWith(@"\??\", StringComparison.Ordinal)) candidate = candidate[4..];
        return IsLocalIconSource(candidate, allowIco) ? NormalizeLocalPath(candidate) : null;
    }

    private static string ExtractUnquotedExecutablePath(string value)
    {
        var executableEnd = value.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (executableEnd >= 0) return value[..(executableEnd + 4)];
        var iconEnd = value.IndexOf(".ico", StringComparison.OrdinalIgnoreCase);
        return iconEnd >= 0 ? value[..(iconEnd + 4)] : value.Split(',', 2)[0].Trim();
    }

    private static string NormalizeProcessName(string? value) => string.IsNullOrWhiteSpace(value)
        ? string.Empty
        : Path.GetFileNameWithoutExtension(value.Trim());

    private static bool IsLocalIconSource(string? candidate, bool allowIco = false)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return false;
        try
        {
            var extension = Path.GetExtension(candidate);
            return Path.IsPathFullyQualified(candidate) &&
                   (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
                    (allowIco && extension.Equals(".ico", StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static string NormalizeLocalPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    private static void TrimSourceCache(ConcurrentDictionary<string, Lazy<string?>> cache)
    {
        // These are only source-lookup memoizers, not image caches. Prevent a long editing
        // session with many transient process/service names from retaining unbounded entries.
        if (cache.Count >= SourceCacheCapacity) cache.Clear();
    }
}

internal sealed record ActionIconSourceRequest(
    string Type,
    string Target,
    string TargetType,
    string ExecutablePath,
    string ProcessName,
    string WorkingDirectory,
    string ServiceName);
