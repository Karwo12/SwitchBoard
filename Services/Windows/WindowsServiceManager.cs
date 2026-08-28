using System.ComponentModel;
using System.Runtime.InteropServices;
using SwitchBoard.Models.Actions;
using SwitchBoard.Services.Discovery;

namespace SwitchBoard.Services.Windows;

public sealed class WindowsServiceManager : IWindowsServiceManager
{
    private const uint ScManagerConnect = 0x0001;
    private const uint ScManagerEnumerateService = 0x0004;
    private const uint ServiceQueryConfig = 0x0001;
    private const uint ServiceChangeConfig = 0x0002;
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint ServiceWin32 = 0x0030;
    private const uint ServiceStateAll = 0x0003;
    private const uint ServiceControlStop = 0x00000001;
    private const int ScEnumProcessInfo = 0;
    private const int ScStatusProcessInfo = 0;
    private const int ServiceConfigDescription = 1;
    private const int ServiceConfigDelayedAutoStartInfo = 3;
    private const uint ServiceNoChange = 0xFFFFFFFF;
    private const uint ServiceAutoStart = 2;
    private const uint ServiceDemandStart = 3;
    private const uint ServiceDisabled = 4;
    private const int ErrorMoreData = 234;
    private const int ErrorServiceAlreadyRunning = 1056;
    private const int ErrorServiceNotActive = 1062;
    private const uint ServiceStopped = 1;
    private const uint ServiceStartPending = 2;
    private const uint ServiceStopPending = 3;
    private const uint ServiceRunning = 4;
    private static readonly TimeSpan StabilityDelay = TimeSpan.FromSeconds(1);

    public Task<IReadOnlyList<ServiceCandidate>> GetServicesAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<ServiceCandidate>>(() => EnumerateServices(cancellationToken), cancellationToken);

    public Task<string> GetStateAsync(string serviceName, CancellationToken cancellationToken = default) =>
        Task.Run(() => GetState(serviceName, cancellationToken), cancellationToken);

    public Task<WindowsServiceSnapshot> GetSnapshotAsync(
        string serviceName,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => GetSnapshot(serviceName, cancellationToken), cancellationToken);

    public Task<WindowsServiceOperationResult> SetStateAsync(
        string serviceName,
        string desiredState,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => SetState(serviceName, desiredState, timeout, cancellationToken), cancellationToken);

    public Task<WindowsServiceConfigurationResult> SetConfigurationAsync(
        string serviceName,
        string desiredState,
        string desiredStartupType,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => SetConfiguration(serviceName, desiredState, desiredStartupType, timeout, cancellationToken),
            cancellationToken);

    private static WindowsServiceSnapshot GetSnapshot(string serviceName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero) throw CreateLastError("Windows could not open the Service Control Manager.");
        try
        {
            var service = OpenService(manager, serviceName, ServiceQueryStatus | ServiceQueryConfig);
            if (service == IntPtr.Zero) throw CreateLastError($"Windows could not open service '{serviceName}'.");
            try
            {
                return new WindowsServiceSnapshot(
                    FormatStatus(QueryStatus(service).CurrentState),
                    QueryStartupType(service));
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    private static string GetState(string serviceName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero) throw CreateLastError("Windows could not open the Service Control Manager.");
        try
        {
            var service = OpenService(manager, serviceName, ServiceQueryStatus);
            if (service == IntPtr.Zero) throw CreateLastError($"Windows could not open service '{serviceName}'.");
            try
            {
                return QueryStatus(service).CurrentState switch
                {
                    ServiceRunning => ServiceDesiredStateIds.Running,
                    ServiceStopped => ServiceDesiredStateIds.Stopped,
                    var value => throw new InvalidOperationException(
                        $"Service '{serviceName}' is in transitional state {FormatStatus(value)}.")
                };
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    private static IReadOnlyList<ServiceCandidate> EnumerateServices(CancellationToken cancellationToken)
    {
        var manager = OpenSCManager(null, null, ScManagerEnumerateService | ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            throw CreateLastError("Windows could not open the Service Control Manager.");
        }

        try
        {
            uint bytesNeeded = 0;
            uint servicesReturned = 0;
            uint resumeHandle = 0;
            EnumServicesStatusEx(
                manager,
                ScEnumProcessInfo,
                ServiceWin32,
                ServiceStateAll,
                IntPtr.Zero,
                0,
                out bytesNeeded,
                out servicesReturned,
                ref resumeHandle,
                null);
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorMoreData || bytesNeeded == 0)
            {
                throw new Win32Exception(error, "Windows could not enumerate services.");
            }

            var buffer = Marshal.AllocHGlobal((int)bytesNeeded);
            try
            {
                resumeHandle = 0;
                if (!EnumServicesStatusEx(
                        manager,
                        ScEnumProcessInfo,
                        ServiceWin32,
                        ServiceStateAll,
                        buffer,
                        bytesNeeded,
                        out bytesNeeded,
                        out servicesReturned,
                        ref resumeHandle,
                        null))
                {
                    throw CreateLastError("Windows could not enumerate services.");
                }

                var results = new List<ServiceCandidate>((int)servicesReturned);
                var itemSize = Marshal.SizeOf<EnumServiceStatusProcess>();
                for (var index = 0; index < servicesReturned; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var item = Marshal.PtrToStructure<EnumServiceStatusProcess>(buffer + (index * itemSize));
                    var details = TryReadServiceDetails(manager, item.ServiceName);
                    results.Add(new ServiceCandidate(
                        item.DisplayName,
                        item.ServiceName,
                        FormatStatus(item.Status.CurrentState),
                        details.StartupType,
                        details.Description));
                }

                return results
                    .OrderBy(service => service.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(service => service.ServiceName, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    private static WindowsServiceOperationResult SetState(
        string serviceName,
        string desiredState,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (string.Equals(desiredState, ServiceDesiredStateIds.Unchanged, StringComparison.OrdinalIgnoreCase))
        {
            return new WindowsServiceOperationResult(true, true, "The desired service state is Unchanged.");
        }

        var targetState = string.Equals(desiredState, ServiceDesiredStateIds.Running, StringComparison.OrdinalIgnoreCase)
            ? ServiceRunning
            : string.Equals(desiredState, ServiceDesiredStateIds.Stopped, StringComparison.OrdinalIgnoreCase)
                ? ServiceStopped
                : 0;
        if (targetState == 0)
        {
            return new WindowsServiceOperationResult(false, false, $"Unsupported service state '{desiredState}'.",
                ExpectedState: desiredState);
        }

        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            return FailureFromLastError("Windows could not open the Service Control Manager.");
        }

        try
        {
            // PREPARE uses a query-only handle. Asking for SERVICE_START/SERVICE_STOP before
            // checking the current state turns an otherwise valid Skipped result into Access Denied.
            var queryOnlyService = OpenService(manager, serviceName, ServiceQueryStatus);
            if (queryOnlyService == IntPtr.Zero)
                return FailureFromLastError($"Windows could not open service '{serviceName}'.");
            string preparedState;
            try
            {
                var prepared = QueryStatus(queryOnlyService);
                preparedState = FormatStatus(prepared.CurrentState);
                if (prepared.CurrentState == targetState)
                {
                    // A Skipped result is user-visible and suppresses Restore. Confirm it from a
                    // second fresh SCM query instead of trusting one observation.
                    Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).GetAwaiter().GetResult();
                    var confirmed = QueryStatus(queryOnlyService);
                    preparedState = FormatStatus(confirmed.CurrentState);
                    if (confirmed.CurrentState == targetState)
                        return new WindowsServiceOperationResult(true, true,
                            $"Service '{serviceName}' was already {FormatStatus(targetState)}.",
                            preparedState, preparedState, FormatStatus(targetState));
                }
            }
            finally { CloseServiceHandle(queryOnlyService); }

            var access = ServiceQueryStatus | (targetState == ServiceRunning ? ServiceStart : ServiceStop);
            var service = OpenService(manager, serviceName, access);
            if (service == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                return Failure(error, $"Windows could not open service '{serviceName}'.",
                    preparedState, FormatStatus(targetState));
            }

            try
            {
                var current = QueryStatus(service);
                var stateBefore = FormatStatus(current.CurrentState);
                if (current.CurrentState == targetState)
                {
                    return new WindowsServiceOperationResult(true, true,
                        $"Service '{serviceName}' was already {FormatStatus(targetState)}.",
                        stateBefore, stateBefore, FormatStatus(targetState));
                }

                if (targetState == ServiceRunning)
                {
                    if (!StartService(service, 0, null))
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error != ErrorServiceAlreadyRunning)
                        {
                            return Failure(error, $"Windows could not start service '{serviceName}'.",
                                stateBefore, FormatStatus(targetState));
                        }
                    }
                }
                else
                {
                    if (!ControlService(service, ServiceControlStop, out _))
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error != ErrorServiceNotActive)
                        {
                            return Failure(error, $"Windows could not stop service '{serviceName}'.",
                                stateBefore, FormatStatus(targetState));
                        }
                    }
                }

                var deadline = DateTime.UtcNow + timeout;
                while (DateTime.UtcNow < deadline)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    current = QueryStatus(service);
                    if (current.CurrentState == targetState)
                    {
                        // Re-query after a short stability window. Trigger-start services may reach the
                        // requested state briefly and immediately be restarted by Windows.
                        Task.Delay(StabilityDelay, cancellationToken).GetAwaiter().GetResult();
                        var stable = QueryStatus(service);
                        if (stable.CurrentState != targetState)
                        {
                            var restarted = targetState == ServiceStopped && stable.CurrentState is ServiceRunning or ServiceStartPending;
                            var message = restarted
                                ? $"Service '{serviceName}' reached Stopped, but Windows started it again. Current state: {FormatStatus(stable.CurrentState)}."
                                : $"Service '{serviceName}' did not remain {FormatStatus(targetState)}. Current state: {FormatStatus(stable.CurrentState)}.";
                            return new WindowsServiceOperationResult(false, false, message, stateBefore,
                                FormatStatus(stable.CurrentState), FormatStatus(targetState), WasRestartedByWindows: restarted);
                        }
                        return new WindowsServiceOperationResult(true, false,
                            $"Service '{serviceName}' now has state {FormatStatus(targetState)}.", stateBefore,
                            FormatStatus(stable.CurrentState), FormatStatus(targetState));
                    }

                    if (targetState == ServiceRunning && current.CurrentState is not ServiceStartPending)
                    {
                        return new WindowsServiceOperationResult(
                            false,
                            false,
                            $"Service '{serviceName}' entered state {FormatStatus(current.CurrentState)} instead of Running.",
                            stateBefore, FormatStatus(current.CurrentState), "Running");
                    }

                    if (targetState == ServiceStopped && current.CurrentState is not ServiceStopPending)
                    {
                        return new WindowsServiceOperationResult(
                            false,
                            false,
                            $"Service '{serviceName}' entered state {FormatStatus(current.CurrentState)} instead of Stopped.",
                            stateBefore, FormatStatus(current.CurrentState), "Stopped");
                    }

                    Task.Delay(150, cancellationToken).GetAwaiter().GetResult();
                }

                current = QueryStatus(service);
                return current.CurrentState == targetState
                    ? VerifyStable(service, serviceName, stateBefore, targetState, cancellationToken)
                    : new WindowsServiceOperationResult(
                        false,
                        false,
                        $"Service '{serviceName}' did not reach {FormatStatus(targetState)} within {timeout.TotalSeconds:0.#} seconds. Current state: {FormatStatus(current.CurrentState)}.",
                        stateBefore, FormatStatus(current.CurrentState), FormatStatus(targetState));
            }
            catch (Win32Exception exception)
            {
                return Failure(exception.NativeErrorCode, $"Windows could not control service '{serviceName}'.");
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(manager);
        }
    }

    private static WindowsServiceConfigurationResult SetConfiguration(
        string serviceName,
        string desiredState,
        string desiredStartupType,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        desiredState = string.IsNullOrWhiteSpace(desiredState)
            ? ServiceDesiredStateIds.Unchanged
            : desiredState;
        desiredStartupType = string.IsNullOrWhiteSpace(desiredStartupType)
            ? ServiceStartupTypeIds.Unchanged
            : desiredStartupType;

        WindowsServiceSnapshot before;
        try { before = GetSnapshot(serviceName, cancellationToken); }
        catch (Win32Exception exception)
        {
            return new WindowsServiceConfigurationResult(false, false, null, null, desiredState,
                desiredStartupType, exception.Message, exception.NativeErrorCode);
        }

        if (!IsSupportedRuntimeState(desiredState) || !IsSupportedStartupType(desiredStartupType))
        {
            return new WindowsServiceConfigurationResult(false, false, before, before, desiredState,
                desiredStartupType, "The requested service configuration is not supported.");
        }

        var stateMatches = MatchesRuntime(before, desiredState);
        var startupMatches = MatchesStartup(before, desiredStartupType);
        if (stateMatches && startupMatches)
        {
            return new WindowsServiceConfigurationResult(true, true, before, before, desiredState,
                desiredStartupType, $"Service '{serviceName}' already has the requested configuration.");
        }

        int? win32Error = null;
        string? operationError = null;
        var restarted = false;

        // A disabled service cannot be started. Apply a non-disabled requested startup type first.
        if (string.Equals(desiredState, ServiceDesiredStateIds.Running, StringComparison.OrdinalIgnoreCase) &&
            !startupMatches &&
            !string.Equals(desiredStartupType, ServiceStartupTypeIds.Unchanged, StringComparison.OrdinalIgnoreCase))
        {
            var startupResult = SetStartupType(serviceName, desiredStartupType, cancellationToken);
            if (!startupResult.IsSuccessful)
            {
                win32Error = startupResult.Win32Error;
                operationError = startupResult.Message;
            }
        }

        if (operationError is null &&
            !stateMatches &&
            !string.Equals(desiredState, ServiceDesiredStateIds.Unchanged, StringComparison.OrdinalIgnoreCase))
        {
            var stateResult = SetState(serviceName, desiredState, timeout, cancellationToken);
            if (!stateResult.IsSuccessful)
            {
                win32Error = stateResult.Win32Error;
                operationError = stateResult.Message;
                restarted = stateResult.WasRestartedByWindows;
            }
        }

        // Stop is verified before disabling. For an unchanged runtime state only startup type is touched.
        if (operationError is null &&
            !string.Equals(desiredState, ServiceDesiredStateIds.Running, StringComparison.OrdinalIgnoreCase) &&
            !startupMatches &&
            !string.Equals(desiredStartupType, ServiceStartupTypeIds.Unchanged, StringComparison.OrdinalIgnoreCase))
        {
            var startupResult = SetStartupType(serviceName, desiredStartupType, cancellationToken);
            if (!startupResult.IsSuccessful)
            {
                win32Error = startupResult.Win32Error;
                operationError = startupResult.Message;
            }
        }

        WindowsServiceSnapshot? current;
        try
        {
            // A final fresh SCM read is the verification boundary for both independently requested properties.
            current = GetSnapshot(serviceName, cancellationToken);
        }
        catch (Win32Exception exception)
        {
            return new WindowsServiceConfigurationResult(false, false, before, null, desiredState,
                desiredStartupType, operationError ?? exception.Message, win32Error ?? exception.NativeErrorCode,
                restarted);
        }

        var verified = MatchesRuntime(current, desiredState) && MatchesStartup(current, desiredStartupType);
        if (!verified)
        {
            var mismatch = $"Expected Status={DisplayRuntimeTarget(desiredState)}, StartupType={DisplayStartupTarget(desiredStartupType)}; " +
                           $"actual Status={current.RuntimeState}, StartupType={current.StartupType}.";
            operationError = string.IsNullOrWhiteSpace(operationError) ? mismatch : $"{operationError} {mismatch}";
        }

        return new WindowsServiceConfigurationResult(verified, false, before, current, desiredState,
            desiredStartupType, verified ? $"Service '{serviceName}' configuration was verified." : operationError,
            win32Error, restarted);
    }

    private static StartupTypeOperationResult SetStartupType(
        string serviceName,
        string desiredStartupType,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.Equals(desiredStartupType, ServiceStartupTypeIds.Unchanged, StringComparison.OrdinalIgnoreCase))
            return new StartupTypeOperationResult(true);

        var nativeStartType = desiredStartupType switch
        {
            ServiceStartupTypeIds.Automatic => ServiceAutoStart,
            ServiceStartupTypeIds.AutomaticDelayed => ServiceAutoStart,
            ServiceStartupTypeIds.Manual => ServiceDemandStart,
            ServiceStartupTypeIds.Disabled => ServiceDisabled,
            _ => 0u
        };
        if (nativeStartType == 0)
            return new StartupTypeOperationResult(false, $"Unsupported startup type '{desiredStartupType}'.");

        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            var error = Marshal.GetLastWin32Error();
            return new StartupTypeOperationResult(false, new Win32Exception(error).Message, error);
        }
        try
        {
            var service = OpenService(manager, serviceName,
                ServiceQueryConfig | ServiceChangeConfig);
            if (service == IntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                return new StartupTypeOperationResult(false, new Win32Exception(error).Message, error);
            }
            try
            {
                if (!ChangeServiceConfig(service, ServiceNoChange, nativeStartType, ServiceNoChange,
                        null, null, IntPtr.Zero, null, null, null, null))
                {
                    var error = Marshal.GetLastWin32Error();
                    return new StartupTypeOperationResult(false, new Win32Exception(error).Message, error);
                }

                if (nativeStartType == ServiceAutoStart)
                {
                    var delayed = new ServiceDelayedAutoStartInfo
                    {
                        DelayedAutoStart = string.Equals(desiredStartupType,
                            ServiceStartupTypeIds.AutomaticDelayed, StringComparison.OrdinalIgnoreCase)
                    };
                    var size = Marshal.SizeOf<ServiceDelayedAutoStartInfo>();
                    var buffer = Marshal.AllocHGlobal(size);
                    try
                    {
                        Marshal.StructureToPtr(delayed, buffer, false);
                        if (!ChangeServiceConfig2(service, ServiceConfigDelayedAutoStartInfo, buffer))
                        {
                            var error = Marshal.GetLastWin32Error();
                            return new StartupTypeOperationResult(false, new Win32Exception(error).Message, error);
                        }
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }

                var actual = QueryStartupType(service);
                return string.Equals(actual, DisplayStartupTarget(desiredStartupType), StringComparison.OrdinalIgnoreCase)
                    ? new StartupTypeOperationResult(true)
                    : new StartupTypeOperationResult(false,
                        $"Startup type verification failed. Expected {DisplayStartupTarget(desiredStartupType)}, actual {actual}.");
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    private static bool IsSupportedRuntimeState(string value) =>
        value is ServiceDesiredStateIds.Unchanged or ServiceDesiredStateIds.Running or ServiceDesiredStateIds.Stopped;

    private static bool IsSupportedStartupType(string value) =>
        value is ServiceStartupTypeIds.Unchanged or ServiceStartupTypeIds.Automatic or
            ServiceStartupTypeIds.AutomaticDelayed or ServiceStartupTypeIds.Manual or ServiceStartupTypeIds.Disabled;

    private static bool MatchesRuntime(WindowsServiceSnapshot snapshot, string desiredState) =>
        string.Equals(desiredState, ServiceDesiredStateIds.Unchanged, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(snapshot.RuntimeState, DisplayRuntimeTarget(desiredState), StringComparison.OrdinalIgnoreCase);

    private static bool MatchesStartup(WindowsServiceSnapshot snapshot, string desiredStartupType) =>
        string.Equals(desiredStartupType, ServiceStartupTypeIds.Unchanged, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(snapshot.StartupType, DisplayStartupTarget(desiredStartupType), StringComparison.OrdinalIgnoreCase);

    private static string DisplayRuntimeTarget(string value) => value switch
    {
        ServiceDesiredStateIds.Running => "Running",
        ServiceDesiredStateIds.Stopped => "Stopped",
        _ => "Unchanged"
    };

    private static string DisplayStartupTarget(string value) => value switch
    {
        ServiceStartupTypeIds.Automatic => "Automatic",
        ServiceStartupTypeIds.AutomaticDelayed => "Automatic (Delayed Start)",
        ServiceStartupTypeIds.Manual => "Manual",
        ServiceStartupTypeIds.Disabled => "Disabled",
        _ => "Unchanged"
    };

    private static ServiceDetails TryReadServiceDetails(IntPtr manager, string serviceName)
    {
        var service = OpenService(manager, serviceName, ServiceQueryConfig);
        if (service == IntPtr.Zero)
        {
            return new ServiceDetails(null, null);
        }

        try
        {
            var startupType = QueryStartupType(service);

            string? description = null;
            QueryServiceConfig2(service, ServiceConfigDescription, IntPtr.Zero, 0, out var needed);
            if (needed > 0)
            {
                var buffer = Marshal.AllocHGlobal((int)needed);
                try
                {
                    if (QueryServiceConfig2(service, ServiceConfigDescription, buffer, needed, out _))
                    {
                        var data = Marshal.PtrToStructure<ServiceDescription>(buffer);
                        description = data.Description == IntPtr.Zero ? null : Marshal.PtrToStringUni(data.Description);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            return new ServiceDetails(startupType, description);
        }
        finally
        {
            CloseServiceHandle(service);
        }
    }

    private static string QueryStartupType(IntPtr service)
    {
        QueryServiceConfig(service, IntPtr.Zero, 0, out var needed);
        if (needed == 0) throw CreateLastError("Windows could not query the service configuration.");
        var buffer = Marshal.AllocHGlobal((int)needed);
        try
        {
            if (!QueryServiceConfig(service, buffer, needed, out _))
                throw CreateLastError("Windows could not query the service configuration.");
            var config = Marshal.PtrToStructure<QueryServiceConfigData>(buffer);
            if (config.StartType != ServiceAutoStart)
            {
                return config.StartType switch
                {
                    0 => "Boot",
                    1 => "System",
                    ServiceDemandStart => "Manual",
                    ServiceDisabled => "Disabled",
                    _ => $"Unknown ({config.StartType})"
                };
            }

            QueryServiceConfig2(service, ServiceConfigDelayedAutoStartInfo, IntPtr.Zero, 0, out needed);
            if (needed == 0) return "Automatic";
            var delayedBuffer = Marshal.AllocHGlobal((int)needed);
            try
            {
                if (!QueryServiceConfig2(service, ServiceConfigDelayedAutoStartInfo, delayedBuffer, needed, out _))
                    return "Automatic";
                var delayed = Marshal.PtrToStructure<ServiceDelayedAutoStartInfo>(delayedBuffer);
                return delayed.DelayedAutoStart ? "Automatic (Delayed Start)" : "Automatic";
            }
            finally { Marshal.FreeHGlobal(delayedBuffer); }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static ServiceStatusProcess QueryStatus(IntPtr service)
    {
        var size = (uint)Marshal.SizeOf<ServiceStatusProcess>();
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (!QueryServiceStatusEx(service, ScStatusProcessInfo, buffer, size, out _))
            {
                throw CreateLastError("Windows could not query the service status.");
            }

            return Marshal.PtrToStructure<ServiceStatusProcess>(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static string FormatStatus(uint status) => status switch
    {
        1 => "Stopped",
        2 => "Start pending",
        3 => "Stop pending",
        4 => "Running",
        5 => "Continue pending",
        6 => "Pause pending",
        7 => "Paused",
        _ => $"Unknown ({status})"
    };

    private static WindowsServiceOperationResult FailureFromLastError(string message) =>
        Failure(Marshal.GetLastWin32Error(), message);

    private static WindowsServiceOperationResult VerifyStable(IntPtr service, string serviceName, string stateBefore,
        uint targetState, CancellationToken cancellationToken)
    {
        Task.Delay(StabilityDelay, cancellationToken).GetAwaiter().GetResult();
        var stable = QueryStatus(service);
        if (stable.CurrentState == targetState)
            return new WindowsServiceOperationResult(true, false,
                $"Service '{serviceName}' now has state {FormatStatus(targetState)}.", stateBefore,
                FormatStatus(stable.CurrentState), FormatStatus(targetState));
        var restarted = targetState == ServiceStopped && stable.CurrentState is ServiceRunning or ServiceStartPending;
        return new WindowsServiceOperationResult(false, false,
            restarted
                ? $"Service '{serviceName}' reached Stopped, but Windows started it again. Current state: {FormatStatus(stable.CurrentState)}."
                : $"Service '{serviceName}' did not remain {FormatStatus(targetState)}. Current state: {FormatStatus(stable.CurrentState)}.",
            stateBefore, FormatStatus(stable.CurrentState), FormatStatus(targetState), WasRestartedByWindows: restarted);
    }

    private static WindowsServiceOperationResult Failure(int error, string message,
        string? stateBefore = null, string? expectedState = null)
    {
        var details = new Win32Exception(error).Message;
        var permissionHint = error == 5 ? " The operation may require administrator privileges." : string.Empty;
        return new WindowsServiceOperationResult(false, false, $"{message}{permissionHint} {details}".Trim(),
            StateBefore: stateBefore, CurrentState: stateBefore, ExpectedState: expectedState, Win32Error: error);
    }

    private static Win32Exception CreateLastError(string message) =>
        new(Marshal.GetLastWin32Error(), message);

    private sealed record ServiceDetails(string? StartupType, string? Description);

    private sealed record StartupTypeOperationResult(bool IsSuccessful, string? Message = null,
        int? Win32Error = null);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct EnumServiceStatusProcess
    {
        [MarshalAs(UnmanagedType.LPWStr)] public string ServiceName;
        [MarshalAs(UnmanagedType.LPWStr)] public string DisplayName;
        public ServiceStatusProcess Status;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
        public uint ProcessId;
        public uint ServiceFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QueryServiceConfigData
    {
        public uint ServiceType;
        public uint StartType;
        public uint ErrorControl;
        public IntPtr BinaryPathName;
        public IntPtr LoadOrderGroup;
        public uint TagId;
        public IntPtr Dependencies;
        public IntPtr ServiceStartName;
        public IntPtr DisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceDescription
    {
        public IntPtr Description;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceDelayedAutoStartInfo
    {
        [MarshalAs(UnmanagedType.Bool)] public bool DelayedAutoStart;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumServicesStatusEx(
        IntPtr manager,
        int infoLevel,
        uint serviceType,
        uint serviceState,
        IntPtr services,
        uint bufferSize,
        out uint bytesNeeded,
        out uint servicesReturned,
        ref uint resumeHandle,
        string? groupName);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig(IntPtr service, IntPtr config, uint bufferSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig2(IntPtr service, int infoLevel, IntPtr buffer, uint bufferSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig(
        IntPtr service,
        uint serviceType,
        uint startType,
        uint errorControl,
        string? binaryPathName,
        string? loadOrderGroup,
        IntPtr tagId,
        string? dependencies,
        string? serviceStartName,
        string? password,
        string? displayName);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig2(IntPtr service, int infoLevel, IntPtr info);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(IntPtr service, int infoLevel, IntPtr buffer, uint bufferSize, out uint bytesNeeded);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartService(IntPtr service, uint argumentCount, string[]? arguments);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(IntPtr service, uint control, out ServiceStatusProcess status);
}
