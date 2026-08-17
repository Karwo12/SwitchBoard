using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using SwitchBoard.Services.Discovery;

namespace SwitchBoard.Services.Windows;

public sealed class WindowsDeviceManager : IDeviceManager
{
    private static readonly HashSet<string> CriticalClasses = new(StringComparer.OrdinalIgnoreCase)
    { "System", "DiskDrive", "Volume", "Keyboard", "Mouse", "Display", "Processor", "Computer", "Firmware", "SCSIAdapter", "HDC" };

    public Task<IReadOnlyList<DeviceCandidate>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<DeviceCandidate>>(() => EnumerateDevices(cancellationToken), cancellationToken);

    public async Task<DeviceCandidate?> GetDeviceAsync(string instanceId, CancellationToken cancellationToken = default) =>
        (await GetDevicesAsync(cancellationToken)).FirstOrDefault(item =>
            string.Equals(item.InstanceId, instanceId, StringComparison.OrdinalIgnoreCase));

    public async Task SetEnabledAsync(string instanceId, bool enabled, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(instanceId)) throw new ArgumentException("Device Instance ID is required.", nameof(instanceId));
        var device = await GetDeviceAsync(instanceId, cancellationToken) ??
                     throw new InvalidOperationException("The selected Windows device is not present.");
        if (!enabled && device.IsCritical)
            throw new InvalidOperationException("SwitchBoard blocked disabling this critical Windows device.");
        if (device.IsEnabled == enabled) return;
        await RunAsync("pnputil.exe", [enabled ? "/enable-device" : "/disable-device", instanceId], cancellationToken);
    }

    private static bool IsCritical(string id, string deviceClass) =>
        CriticalClasses.Contains(deviceClass) || id.StartsWith("ROOT\\", StringComparison.OrdinalIgnoreCase) ||
        id.StartsWith("ACPI\\", StringComparison.OrdinalIgnoreCase) &&
        !deviceClass.Equals("Bluetooth", StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<DeviceCandidate> EnumerateDevices(CancellationToken cancellationToken)
    {
        var set = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, 0x00000002 | 0x00000004);
        if (set == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not enumerate devices.");
        try
        {
            var results = new List<DeviceCandidate>();
            for (uint index = 0; ; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var data = new SpDevInfoData { Size = (uint)Marshal.SizeOf<SpDevInfoData>() };
                if (!SetupDiEnumDeviceInfo(set, index, ref data))
                {
                    var error = Marshal.GetLastWin32Error();
                    if (error == 259) break;
                    throw new Win32Exception(error, "Windows could not enumerate a device.");
                }
                var id = ReadInstanceId(set, ref data);
                if (string.IsNullOrWhiteSpace(id)) continue;
                var deviceClass = ReadProperty(set, ref data, 7);
                var name = ReadProperty(set, ref data, 12);
                if (string.IsNullOrWhiteSpace(name)) name = ReadProperty(set, ref data, 0);
                var enabled = CM_Get_DevNode_Status(out var status, out _, data.DevInst, 0) == 0 && (status & 0x00000008) != 0;
                results.Add(new DeviceCandidate(id, string.IsNullOrWhiteSpace(name) ? id : name,
                    string.IsNullOrWhiteSpace(deviceClass) ? "Other" : deviceClass, enabled,
                    IsCritical(id, deviceClass)));
            }
            return results.OrderBy(item => item.DeviceClass).ThenBy(item => item.FriendlyName,
                StringComparer.CurrentCultureIgnoreCase).ToList();
        }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }

    private static string ReadInstanceId(IntPtr set, ref SpDevInfoData data)
    {
        SetupDiGetDeviceInstanceId(set, ref data, null, 0, out var required);
        if (required == 0) return string.Empty;
        var builder = new StringBuilder((int)required);
        return SetupDiGetDeviceInstanceId(set, ref data, builder, required, out _) ? builder.ToString() : string.Empty;
    }

    private static string ReadProperty(IntPtr set, ref SpDevInfoData data, uint property)
    {
        SetupDiGetDeviceRegistryProperty(set, ref data, property, out _, null, 0, out var required);
        if (required == 0) return string.Empty;
        var buffer = new byte[required];
        if (!SetupDiGetDeviceRegistryProperty(set, ref data, property, out _, buffer, required, out _)) return string.Empty;
        return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
    }

    private static async Task<string> RunAsync(string fileName, IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Windows could not start {fileName}.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var output = await outputTask;
        var error = await errorTask;
        if (process.ExitCode != 0)
        {
            var permission = process.ExitCode == 5 || error.Contains("Access", StringComparison.OrdinalIgnoreCase)
                ? " Administrator privileges are required." : string.Empty;
            throw new InvalidOperationException($"Windows device operation failed.{permission} {error}".Trim());
        }
        return output;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public uint Size;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string? enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SpDevInfoData data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref SpDevInfoData data,
        StringBuilder? id, uint size, out uint required);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref SpDevInfoData data,
        uint property, out uint type, byte[]? buffer, uint size, out uint required);
    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_Status(out uint status, out uint problem, uint deviceInstance, uint flags);
}
