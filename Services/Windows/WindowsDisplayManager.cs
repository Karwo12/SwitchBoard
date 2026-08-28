using System.ComponentModel;
using System.IO;
using Microsoft.Win32;
using System.Runtime.InteropServices;
using SwitchBoard.Services.Discovery;
using SwitchBoard.Services.Logging;

namespace SwitchBoard.Services.Windows;

public sealed class WindowsDisplayManager : IDisplayManager
{
    private readonly IAppLogger? _logger;

    public WindowsDisplayManager(IAppLogger? logger = null) => _logger = logger;

    private const int EnumCurrentSettings = -1;
    private const int DisplayDeviceAttachedToDesktop = 0x1;
    private const int DisplayDevicePrimaryDevice = 0x4;
    private const uint EddGetDeviceInterfaceName = 0x1;
    private const uint CdsUpdateRegistry = 0x1;
    private const uint CdsFullscreen = 0x4;
    private const int DispChangeSuccessful = 0;
    private const uint DmPosition = 0x20;
    private const uint DmBitsPerPel = 0x40000;
    private const uint DmPelsWidth = 0x80000;
    private const uint DmPelsHeight = 0x100000;
    private const uint DmDisplayFlags = 0x200000;
    private const uint DmDisplayFrequency = 0x400000;

    public Task<IReadOnlyList<DisplayCandidate>> GetDisplaysAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<DisplayCandidate>>(() => EnumerateDisplays(cancellationToken), cancellationToken);

    public Task<DisplayModeState> GetCurrentStateAsync(
        string deviceId,
        string deviceName,
        CancellationToken cancellationToken = default) => Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var display = ResolveDisplay(deviceId, deviceName);
            return ReadCurrentState(display);
        }, cancellationToken);

    public Task ApplyTemporaryAsync(DisplayModeState state, CancellationToken cancellationToken = default) =>
        ApplyAsync(state, CdsFullscreen, cancellationToken);

    public Task PersistAsync(DisplayModeState state, CancellationToken cancellationToken = default) =>
        ApplyAsync(state, CdsUpdateRegistry, cancellationToken);

    public Task RestoreAsync(DisplayModeState state, CancellationToken cancellationToken = default) =>
        ApplyAsync(state, 0, cancellationToken);

    private Task ApplyAsync(DisplayModeState state, uint flags, CancellationToken cancellationToken) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var display = ResolveDisplay(state.DeviceId, state.DeviceName);
            var mode = CreateDevMode();
            if (!EnumDisplaySettingsEx(display.DeviceName, EnumCurrentSettings, ref mode, 0))
            {
                throw CreateLastError($"Windows could not read settings for '{display.DeviceName}'.");
            }

            mode.PositionX = state.PositionX;
            mode.PositionY = state.PositionY;
            mode.DisplayOrientation = (uint)state.Orientation;
            mode.DisplayFixedOutput = (uint)state.FixedOutput;
            var supportedMode = EnumerateModes(display.DeviceName, cancellationToken)
                .Where(candidate => candidate.Width == state.Width && candidate.Height == state.Height &&
                                    candidate.RefreshRate == state.RefreshRate)
                .OrderByDescending(candidate => candidate.BitsPerPixel == state.BitsPerPixel)
                .ThenByDescending(candidate => candidate.BitsPerPixel)
                .FirstOrDefault()
                ?? throw new InvalidOperationException("The selected resolution and refresh rate are no longer supported by this monitor.");
            mode.BitsPerPel = (uint)supportedMode.BitsPerPixel;
            mode.PelsWidth = (uint)state.Width;
            mode.PelsHeight = (uint)state.Height;
            mode.DisplayFrequency = (uint)state.RefreshRate;
            mode.Fields = DmPosition | DmBitsPerPel | DmPelsWidth | DmPelsHeight | DmDisplayFlags | DmDisplayFrequency;

            var result = ChangeDisplaySettingsEx(display.DeviceName, ref mode, IntPtr.Zero, flags, IntPtr.Zero);
            if (result != DispChangeSuccessful)
            {
                throw new Win32Exception(result, FormatChangeError(result));
            }
        }, cancellationToken);

    private IReadOnlyList<DisplayCandidate> EnumerateDisplays(CancellationToken cancellationToken)
    {
        var results = new List<DisplayCandidate>();
        var displayConfig = WindowsDisplayConfigReader.Read(_logger)
            .GroupBy(item => item.SourceName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        for (uint adapterIndex = 0; ; adapterIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var adapter = CreateDisplayDevice();
            if (!EnumDisplayDevices(null, adapterIndex, ref adapter, 0))
            {
                break;
            }

            if ((adapter.StateFlags & DisplayDeviceAttachedToDesktop) == 0 || string.IsNullOrWhiteSpace(adapter.DeviceName))
            {
                continue;
            }

            var current = CreateDevMode();
            if (!EnumDisplaySettingsEx(adapter.DeviceName, EnumCurrentSettings, ref current, 0))
            {
                continue;
            }

            var descriptor = CreateDescriptor(adapter, displayConfig.GetValueOrDefault(adapter.DeviceName));
            results.Add(new DisplayCandidate(
                adapter.DeviceName,
                descriptor.DeviceId,
                descriptor.DisplayName,
                results.Count + 1,
                (int)current.PelsWidth,
                (int)current.PelsHeight,
                NormalizeFrequency(current.DisplayFrequency),
                (adapter.StateFlags & DisplayDevicePrimaryDevice) != 0,
                EnumerateModes(adapter.DeviceName, cancellationToken))
            {
                MonitorDevicePath = descriptor.MonitorDevicePath,
                SourceName = descriptor.SourceName,
                DeviceDescription = descriptor.DeviceDescription,
                EdidProductName = descriptor.EdidProductName,
                DisplayNameSource = descriptor.DisplayNameSource
            });
        }

        return results;
    }

    private static IReadOnlyList<DisplayModeCandidate> EnumerateModes(
        string deviceName,
        CancellationToken cancellationToken)
    {
        var modes = new List<DisplayModeCandidate>();
        for (var index = 0; ; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mode = CreateDevMode();
            if (!EnumDisplaySettingsEx(deviceName, index, ref mode, 0))
            {
                break;
            }

            var width = (int)mode.PelsWidth;
            var height = (int)mode.PelsHeight;
            var frequency = NormalizeFrequency(mode.DisplayFrequency);
            var bits = (int)mode.BitsPerPel;
            if (width <= 0 || height <= 0 || frequency <= 0 || bits < 24)
            {
                continue;
            }

            modes.Add(new DisplayModeCandidate(width, height, frequency, bits));
        }

        return modes
            .OrderByDescending(mode => mode.BitsPerPixel)
            .DistinctBy(mode => (mode.Width, mode.Height, mode.RefreshRate))
            .OrderBy(mode => mode.Width)
            .ThenBy(mode => mode.Height)
            .ThenBy(mode => mode.RefreshRate)
            .ToList();
    }

    private DisplayDescriptor ResolveDisplay(string deviceId, string deviceName)
    {
        var displayConfig = WindowsDisplayConfigReader.Read(_logger)
            .GroupBy(item => item.SourceName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        DisplayDescriptor? deviceNameFallback = null;
        for (uint adapterIndex = 0; ; adapterIndex++)
        {
            var adapter = CreateDisplayDevice();
            if (!EnumDisplayDevices(null, adapterIndex, ref adapter, 0)) break;
            if ((adapter.StateFlags & DisplayDeviceAttachedToDesktop) == 0) continue;
            var descriptor = CreateDescriptor(adapter, displayConfig.GetValueOrDefault(adapter.DeviceName));
            if (!string.IsNullOrWhiteSpace(deviceId) && string.Equals(descriptor.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
            {
                return descriptor;
            }

            if (string.Equals(adapter.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
            {
                deviceNameFallback = descriptor;
            }
        }

        return deviceNameFallback ?? throw new InvalidOperationException("The configured monitor is no longer connected.");
    }

    private DisplayDescriptor CreateDescriptor(DisplayDevice adapter, DisplayConfigMonitorInfo? config)
    {
        var monitor = CreateDisplayDevice();
        var hasMonitor = EnumDisplayDevices(adapter.DeviceName, 0, ref monitor, 0);
        var monitorInterface = CreateDisplayDevice();
        var hasMonitorInterface = EnumDisplayDevices(adapter.DeviceName, 0, ref monitorInterface, EddGetDeviceInterfaceName);
        var monitorDevicePath = FirstNonEmpty(config?.MonitorDevicePath,
            hasMonitorInterface ? monitorInterface.DeviceId : null,
            hasMonitor ? monitor.DeviceId : null);
        var deviceId = FirstNonEmpty(hasMonitorInterface ? monitorInterface.DeviceId : null,
            hasMonitor ? monitor.DeviceId : null,
            adapter.DeviceKey, adapter.DeviceName);
        var metadata = ReadMonitorMetadata(monitorDevicePath);
        if (metadata.IsEmpty && hasMonitorInterface &&
            !string.Equals(monitorDevicePath, monitorInterface.DeviceId, StringComparison.OrdinalIgnoreCase))
        {
            metadata = ReadMonitorMetadata(monitorInterface.DeviceId);
        }
        var resolution = MonitorNameResolver.Resolve(
            config?.MonitorFriendlyDeviceName,
            metadata.FriendlyName ?? (hasMonitor ? monitor.DeviceString : null),
            metadata.DeviceDescription,
            metadata.EdidProductName);

        LogMonitorDiagnostics(adapter.DeviceName, monitorDevicePath, config?.MonitorFriendlyDeviceName,
            metadata.FriendlyName, metadata.DeviceDescription, metadata.EdidProductName, resolution);

        return new DisplayDescriptor(
            adapter.DeviceName,
            deviceId,
            resolution.DisplayName,
            monitorDevicePath,
            config?.SourceName ?? adapter.DeviceName,
            metadata.DeviceDescription ?? string.Empty,
            metadata.EdidProductName ?? string.Empty,
            resolution.Source);
    }

    private void LogMonitorDiagnostics(string sourceName, string monitorDevicePath, string? displayConfigFriendlyName,
        string? deviceFriendlyName, string? deviceDescription, string? edidProductName, MonitorNameResolution resolution)
    {
        var message = $"sourceName='{sourceName}', targetDevicePath='{monitorDevicePath}', " +
                      $"displayConfigFriendlyName='{displayConfigFriendlyName}', " +
                      $"deviceFriendlyName='{deviceFriendlyName}', " +
                      $"deviceDescription='{deviceDescription}', edidProductName='{edidProductName}', " +
                      $"finalName='{resolution.DisplayName}', nameSource='{resolution.Source}'.";
        _logger?.Info("DisplayMonitor", message);
        System.Diagnostics.Debug.WriteLine($"[DisplayMonitor] {message}");
    }

    private static string FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim() ?? string.Empty;

    private static MonitorMetadata ReadMonitorMetadata(string monitorDevicePath)
    {
        if (string.IsNullOrWhiteSpace(monitorDevicePath)) return new(null, null, null);
        var parts = monitorDevicePath.Trim().Split('#', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || !parts[0].Equals("\\\\?\\DISPLAY", StringComparison.OrdinalIgnoreCase))
            return new(null, null, null);

        try
        {
            var enumPath = $"SYSTEM\\CurrentControlSet\\Enum\\DISPLAY\\{parts[1]}\\{parts[2]}";
            using var deviceKey = Registry.LocalMachine.OpenSubKey(enumPath);
            using var deviceParameters = deviceKey?.OpenSubKey("Device Parameters");
            var friendlyName = ReadRegistryString(deviceKey, "FriendlyName");
            var description = ReadRegistryString(deviceKey, "DeviceDesc") ??
                              ReadRegistryString(deviceKey, "DeviceDescription");
            var edid = deviceParameters?.GetValue("EDID", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as byte[];
            var edidName = MonitorNameResolver.ExtractEdidProductName(edid);
            return new(friendlyName, description, edidName);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           System.Security.SecurityException)
        {
            return new(null, null, null);
        }
    }

    private static string? ReadRegistryString(RegistryKey? key, string name)
    {
        var value = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        if (string.IsNullOrWhiteSpace(value)) return null;
        var separator = value.LastIndexOf(';');
        if (value.StartsWith('@') && separator >= 0) value = value[(separator + 1)..];
        return value.Trim();
    }

    private static DisplayModeState ReadCurrentState(DisplayDescriptor display)
    {
        var mode = CreateDevMode();
        if (!EnumDisplaySettingsEx(display.DeviceName, EnumCurrentSettings, ref mode, 0))
        {
            throw CreateLastError($"Windows could not read settings for '{display.DeviceName}'.");
        }

        return new DisplayModeState(
            display.DeviceName,
            display.DeviceId,
            display.DisplayName,
            (int)mode.PelsWidth,
            (int)mode.PelsHeight,
            NormalizeFrequency(mode.DisplayFrequency),
            (int)mode.BitsPerPel,
            mode.PositionX,
            mode.PositionY,
            (int)mode.DisplayOrientation,
            (int)mode.DisplayFixedOutput);
    }

    private static int NormalizeFrequency(uint frequency) => frequency is 0 or 1 ? 60 : (int)frequency;

    private static string FormatChangeError(int result) => result switch
    {
        -1 => "Windows could not apply the display mode.",
        -2 => "The selected display mode is not supported.",
        -3 => "The display driver could not apply the requested flags.",
        -4 => "The display mode could not be saved to the registry.",
        -5 => "Changing this display mode requires restarting Windows.",
        -6 => "The display device is not valid.",
        _ => $"Windows returned display configuration error {result}."
    };

    private static Win32Exception CreateLastError(string message) => new(Marshal.GetLastWin32Error(), message);

    private static DisplayDevice CreateDisplayDevice() => new() { Size = Marshal.SizeOf<DisplayDevice>() };

    private static DevMode CreateDevMode() => new() { Size = (ushort)Marshal.SizeOf<DevMode>() };

    private sealed record DisplayDescriptor(
        string DeviceName,
        string DeviceId,
        string DisplayName,
        string MonitorDevicePath,
        string SourceName,
        string DeviceDescription,
        string EdidProductName,
        string DisplayNameSource);

    private sealed record MonitorMetadata(string? FriendlyName, string? DeviceDescription, string? EdidProductName)
    {
        public bool IsEmpty => string.IsNullOrWhiteSpace(FriendlyName) &&
                               string.IsNullOrWhiteSpace(DeviceDescription) &&
                               string.IsNullOrWhiteSpace(EdidProductName);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        public ushort SpecVersion;
        public ushort DriverVersion;
        public ushort Size;
        public ushort DriverExtra;
        public uint Fields;
        public int PositionX;
        public int PositionY;
        public uint DisplayOrientation;
        public uint DisplayFixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TTOption;
        public short Collate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string FormName;
        public ushort LogPixels;
        public uint BitsPerPel;
        public uint PelsWidth;
        public uint PelsHeight;
        public uint DisplayFlags;
        public uint DisplayFrequency;
        public uint ICMMethod;
        public uint ICMIntent;
        public uint MediaType;
        public uint DitherType;
        public uint Reserved1;
        public uint Reserved2;
        public uint PanningWidth;
        public uint PanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? device, uint deviceNumber, ref DisplayDevice displayDevice, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsEx(string deviceName, int modeNumber, ref DevMode devMode, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsEx(string deviceName, ref DevMode devMode, IntPtr window, uint flags, IntPtr parameters);
}
