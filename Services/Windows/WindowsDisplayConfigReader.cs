using System.ComponentModel;
using System.Runtime.InteropServices;
using SwitchBoard.Services.Logging;

namespace SwitchBoard.Services.Windows;

public sealed record DisplayConfigMonitorInfo(
    string SourceName,
    string MonitorDevicePath,
    string MonitorFriendlyDeviceName);

internal static class WindowsDisplayConfigReader
{
    private const uint QdcOnlyActivePaths = 0x00000002;
    private const int ErrorInsufficientBuffer = 122;
    private const uint DisplayConfigDeviceInfoGetSourceName = 1;
    private const uint DisplayConfigDeviceInfoGetTargetName = 2;

    public static IReadOnlyList<DisplayConfigMonitorInfo> Read(IAppLogger? logger)
    {
        try
        {
            var bufferResult = GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out var pathCount, out var modeCount);
            if (bufferResult != 0)
            {
                Log(logger, $"GetDisplayConfigBufferSizes failed with Win32 error {bufferResult}.");
                return [];
            }
            for (var attempt = 0; attempt < 2; attempt++)
            {
                var paths = new DisplayConfigPathInfo[pathCount];
                var modes = new DisplayConfigModeInfo[modeCount];
                var result = QueryDisplayConfig(QdcOnlyActivePaths, ref pathCount, paths,
                    ref modeCount, modes, IntPtr.Zero);
                if (result == ErrorInsufficientBuffer)
                {
                    bufferResult = GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out pathCount, out modeCount);
                    if (bufferResult != 0)
                    {
                        Log(logger, $"GetDisplayConfigBufferSizes retry failed with Win32 error {bufferResult}.");
                        return [];
                    }
                    continue;
                }
                if (result != 0)
                {
                    Log(logger, $"QueryDisplayConfig failed with Win32 error {result}.");
                    return [];
                }

                var resultItems = new List<DisplayConfigMonitorInfo>();
                foreach (var path in paths.Take((int)pathCount))
                {
                    var sourceName = ReadSourceName(path.SourceInfo);
                    if (string.IsNullOrWhiteSpace(sourceName)) continue;
                    var target = ReadTargetName(path.TargetInfo);
                    if (target is not { } targetValue || string.IsNullOrWhiteSpace(targetValue.MonitorDevicePath)) continue;
                    resultItems.Add(new(sourceName, targetValue.MonitorDevicePath.Trim(),
                        targetValue.MonitorFriendlyDeviceName?.Trim() ?? string.Empty));
                }

                return resultItems
                    .GroupBy(item => $"{item.SourceName}\n{item.MonitorDevicePath}", StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToList();
            }
        }
        catch (Win32Exception exception)
        {
            Log(logger, $"QueryDisplayConfig failed: {exception.Message}");
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException or
                                           BadImageFormatException or MarshalDirectiveException)
        {
            Log(logger, $"QueryDisplayConfig is unavailable: {exception.Message}");
        }

        return [];
    }

    private static string? ReadSourceName(DisplayConfigPathSourceInfo sourceInfo)
    {
        var request = new DisplayConfigSourceDeviceName
        {
            Header = CreateHeader(DisplayConfigDeviceInfoGetSourceName,
                Marshal.SizeOf<DisplayConfigSourceDeviceName>(), sourceInfo.AdapterId, sourceInfo.Id)
        };
        return DisplayConfigGetDeviceInfo(ref request) == 0 ? request.ViewGdiDeviceName : null;
    }

    private static DisplayConfigTargetDeviceName? ReadTargetName(DisplayConfigPathTargetInfo targetInfo)
    {
        var request = new DisplayConfigTargetDeviceName
        {
            Header = CreateHeader(DisplayConfigDeviceInfoGetTargetName,
                Marshal.SizeOf<DisplayConfigTargetDeviceName>(), targetInfo.AdapterId, targetInfo.Id)
        };
        return DisplayConfigGetDeviceInfo(ref request) == 0 ? request : null;
    }

    private static DisplayConfigDeviceInfoHeader CreateHeader(uint type, int size, Luid adapterId, uint id) => new()
    {
        Type = type,
        Size = (uint)size,
        AdapterId = adapterId,
        Id = id
    };

    private static void Log(IAppLogger? logger, string message)
    {
        logger?.Warning("DisplayConfig", message);
        System.Diagnostics.Debug.WriteLine($"[DisplayConfig] {message}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathInfo
    {
        public DisplayConfigPathSourceInfo SourceInfo;
        public DisplayConfigPathTargetInfo TargetInfo;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathSourceInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigPathTargetInfo
    {
        public Luid AdapterId;
        public uint Id;
        public uint ModeInfoIdx;
        public uint OutputTechnology;
        public uint Rotation;
        public uint Scaling;
        public DisplayConfigRational RefreshRate;
        public uint ScanLineOrdering;
        public uint TargetAvailable;
        public uint StatusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigRational
    {
        public uint Numerator;
        public uint Denominator;
    }

    // DISPLAYCONFIG_MODE_INFO is a 64-byte tagged union. QueryDisplayConfig only
    // needs the correctly sized buffer here; the monitor names come from the target API.
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    private struct DisplayConfigModeInfo
    {
        [FieldOffset(0)] public uint InfoType;
        [FieldOffset(4)] public Luid AdapterId;
        [FieldOffset(12)] public uint Id;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DisplayConfigDeviceInfoHeader
    {
        public uint Type;
        public uint Size;
        public Luid AdapterId;
        public uint Id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigSourceDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string ViewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayConfigTargetDeviceName
    {
        public DisplayConfigDeviceInfoHeader Header;
        public uint Flags;
        public uint OutputTechnology;
        public ushort EdidManufactureId;
        public ushort EdidProductCodeId;
        public uint ConnectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string MonitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string MonitorDevicePath;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements,
        out uint numModeInfoArrayElements);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int QueryDisplayConfig(uint flags, ref uint numPathArrayElements,
        [Out] DisplayConfigPathInfo[] pathInfoArray, ref uint numModeInfoArrayElements,
        [Out] DisplayConfigModeInfo[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", SetLastError = true)]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigSourceDeviceName requestPacket);

    [DllImport("user32.dll", EntryPoint = "DisplayConfigGetDeviceInfo", SetLastError = true)]
    private static extern int DisplayConfigGetDeviceInfo(ref DisplayConfigTargetDeviceName requestPacket);
}
