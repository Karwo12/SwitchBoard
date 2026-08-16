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
    private const uint ServiceQueryStatus = 0x0004;
    private const uint ServiceStart = 0x0010;
    private const uint ServiceStop = 0x0020;
    private const uint ServiceWin32 = 0x0030;
    private const uint ServiceStateAll = 0x0003;
    private const uint ServiceControlStop = 0x00000001;
    private const int ScEnumProcessInfo = 0;
    private const int ScStatusProcessInfo = 0;
    private const int ServiceConfigDescription = 1;
    private const int ErrorMoreData = 234;
    private const int ErrorServiceAlreadyRunning = 1056;
    private const int ErrorServiceNotActive = 1062;
    private const uint ServiceStopped = 1;
    private const uint ServiceStartPending = 2;
    private const uint ServiceStopPending = 3;
    private const uint ServiceRunning = 4;

    public Task<IReadOnlyList<ServiceCandidate>> GetServicesAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<ServiceCandidate>>(() => EnumerateServices(cancellationToken), cancellationToken);

    public Task<string> GetStateAsync(string serviceName, CancellationToken cancellationToken = default) =>
        Task.Run(() => GetState(serviceName, cancellationToken), cancellationToken);

    public Task<WindowsServiceOperationResult> SetStateAsync(
        string serviceName,
        string desiredState,
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        Task.Run(() => SetState(serviceName, desiredState, timeout, cancellationToken), cancellationToken);

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
            return new WindowsServiceOperationResult(false, false, $"Unsupported service state '{desiredState}'.");
        }

        var manager = OpenSCManager(null, null, ScManagerConnect);
        if (manager == IntPtr.Zero)
        {
            return FailureFromLastError("Windows could not open the Service Control Manager.");
        }

        try
        {
            var access = ServiceQueryStatus | (targetState == ServiceRunning ? ServiceStart : ServiceStop);
            var service = OpenService(manager, serviceName, access);
            if (service == IntPtr.Zero)
            {
                return FailureFromLastError($"Windows could not open service '{serviceName}'.");
            }

            try
            {
                var current = QueryStatus(service);
                if (current.CurrentState == targetState)
                {
                    return new WindowsServiceOperationResult(true, true, "The service is already in the requested state.");
                }

                if (targetState == ServiceRunning)
                {
                    if (!StartService(service, 0, null))
                    {
                        var error = Marshal.GetLastWin32Error();
                        if (error != ErrorServiceAlreadyRunning)
                        {
                            return Failure(error, $"Windows could not start service '{serviceName}'.");
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
                            return Failure(error, $"Windows could not stop service '{serviceName}'.");
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
                        return new WindowsServiceOperationResult(true, false);
                    }

                    if (targetState == ServiceRunning && current.CurrentState is not ServiceStartPending)
                    {
                        return new WindowsServiceOperationResult(
                            false,
                            false,
                            $"Service '{serviceName}' entered state {FormatStatus(current.CurrentState)} instead of Running.");
                    }

                    if (targetState == ServiceStopped && current.CurrentState is not ServiceStopPending)
                    {
                        return new WindowsServiceOperationResult(
                            false,
                            false,
                            $"Service '{serviceName}' entered state {FormatStatus(current.CurrentState)} instead of Stopped.");
                    }

                    Task.Delay(150, cancellationToken).GetAwaiter().GetResult();
                }

                current = QueryStatus(service);
                return current.CurrentState == targetState
                    ? new WindowsServiceOperationResult(true, false)
                    : new WindowsServiceOperationResult(
                        false,
                        false,
                        $"Service '{serviceName}' did not reach {FormatStatus(targetState)} within {timeout.TotalSeconds:0.#} seconds.");
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

    private static ServiceDetails TryReadServiceDetails(IntPtr manager, string serviceName)
    {
        var service = OpenService(manager, serviceName, ServiceQueryConfig);
        if (service == IntPtr.Zero)
        {
            return new ServiceDetails(null, null);
        }

        try
        {
            string? startupType = null;
            QueryServiceConfig(service, IntPtr.Zero, 0, out var needed);
            if (needed > 0)
            {
                var buffer = Marshal.AllocHGlobal((int)needed);
                try
                {
                    if (QueryServiceConfig(service, buffer, needed, out _))
                    {
                        var config = Marshal.PtrToStructure<QueryServiceConfigData>(buffer);
                        startupType = config.StartType switch
                        {
                            0 => "Boot",
                            1 => "System",
                            2 => "Automatic",
                            3 => "Manual",
                            4 => "Disabled",
                            _ => null
                        };
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            string? description = null;
            QueryServiceConfig2(service, ServiceConfigDescription, IntPtr.Zero, 0, out needed);
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

    private static WindowsServiceOperationResult Failure(int error, string message)
    {
        var details = new Win32Exception(error).Message;
        var permissionHint = error == 5 ? " The operation may require administrator privileges." : string.Empty;
        return new WindowsServiceOperationResult(false, false, $"{message}{permissionHint} {details}".Trim());
    }

    private static Win32Exception CreateLastError(string message) =>
        new(Marshal.GetLastWin32Error(), message);

    private sealed record ServiceDetails(string? StartupType, string? Description);

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
