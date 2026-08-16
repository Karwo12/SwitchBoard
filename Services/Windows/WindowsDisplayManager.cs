using System.ComponentModel;
using System.Runtime.InteropServices;
using SwitchBoard.Services.Discovery;

namespace SwitchBoard.Services.Windows;

public sealed class WindowsDisplayManager : IDisplayManager
{
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

    private static Task ApplyAsync(DisplayModeState state, uint flags, CancellationToken cancellationToken) =>
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

    private static IReadOnlyList<DisplayCandidate> EnumerateDisplays(CancellationToken cancellationToken)
    {
        var results = new List<DisplayCandidate>();
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

            var monitor = CreateDisplayDevice();
            var hasMonitor = EnumDisplayDevices(adapter.DeviceName, 0, ref monitor, EddGetDeviceInterfaceName);
            var current = CreateDevMode();
            if (!EnumDisplaySettingsEx(adapter.DeviceName, EnumCurrentSettings, ref current, 0))
            {
                continue;
            }

            var displayName = hasMonitor && !IsGenericName(monitor.DeviceString)
                ? monitor.DeviceString.Trim()
                : !string.IsNullOrWhiteSpace(adapter.DeviceString)
                    ? adapter.DeviceString.Trim()
                    : adapter.DeviceName;
            var deviceId = hasMonitor && !string.IsNullOrWhiteSpace(monitor.DeviceId)
                ? monitor.DeviceId.Trim()
                : adapter.DeviceKey?.Trim() ?? adapter.DeviceName;
            results.Add(new DisplayCandidate(
                adapter.DeviceName,
                deviceId,
                displayName,
                results.Count + 1,
                (int)current.PelsWidth,
                (int)current.PelsHeight,
                NormalizeFrequency(current.DisplayFrequency),
                (adapter.StateFlags & DisplayDevicePrimaryDevice) != 0,
                EnumerateModes(adapter.DeviceName, cancellationToken)));
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

    private static DisplayDescriptor ResolveDisplay(string deviceId, string deviceName)
    {
        DisplayDescriptor? deviceNameFallback = null;
        for (uint adapterIndex = 0; ; adapterIndex++)
        {
            var adapter = CreateDisplayDevice();
            if (!EnumDisplayDevices(null, adapterIndex, ref adapter, 0)) break;
            if ((adapter.StateFlags & DisplayDeviceAttachedToDesktop) == 0) continue;
            var monitor = CreateDisplayDevice();
            var hasMonitor = EnumDisplayDevices(adapter.DeviceName, 0, ref monitor, EddGetDeviceInterfaceName);
            var candidateId = hasMonitor && !string.IsNullOrWhiteSpace(monitor.DeviceId)
                ? monitor.DeviceId.Trim()
                : adapter.DeviceKey?.Trim() ?? adapter.DeviceName;
            var descriptor = new DisplayDescriptor(
                adapter.DeviceName,
                candidateId,
                hasMonitor && !string.IsNullOrWhiteSpace(monitor.DeviceString) ? monitor.DeviceString.Trim() : adapter.DeviceString.Trim());
            if (!string.IsNullOrWhiteSpace(deviceId) && string.Equals(candidateId, deviceId, StringComparison.OrdinalIgnoreCase))
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

    private static bool IsGenericName(string? value) => string.IsNullOrWhiteSpace(value) ||
        value.Contains("Generic", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("Rodzajowy", StringComparison.OrdinalIgnoreCase);

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

    private sealed record DisplayDescriptor(string DeviceName, string DeviceId, string DisplayName);

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
