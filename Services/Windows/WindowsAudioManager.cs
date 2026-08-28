using System.Runtime.InteropServices;
using SwitchBoard.Services.Discovery;

namespace SwitchBoard.Services.Windows;

public sealed class WindowsAudioManager : IAudioManager
{
    public Task<IReadOnlyList<AudioDeviceCandidate>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<AudioDeviceCandidate>>(() =>
        {
            try { return Enumerate(cancellationToken); }
            catch (Exception exception) when (exception.HResult == unchecked((int)0x80004002))
            {
                // Some remote/VM audio stacks expose the enumerator but no endpoint collection interface.
                return [];
            }
        }, cancellationToken);

    public Task<string?> GetDefaultDeviceIdAsync(bool input, bool communications,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        return GetDefaultId(input ? EDataFlow.Capture : EDataFlow.Render,
            communications ? ERole.Communications : ERole.Multimedia);
    }, cancellationToken);

    public Task SetDefaultDeviceAsync(string deviceId, bool multimedia, bool communications,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!multimedia && !communications) throw new InvalidOperationException("Select at least one default audio role.");
        var policy = (IPolicyConfigVista)(object)new PolicyConfigClient();
        try
        {
            if (multimedia)
            {
                Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, ERole.Console));
                Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, ERole.Multimedia));
            }
            if (communications) Marshal.ThrowExceptionForHR(policy.SetDefaultEndpoint(deviceId, ERole.Communications));
        }
        finally { Marshal.FinalReleaseComObject(policy); }
    }, cancellationToken);

    public Task<(float Volume, bool Muted)> GetMasterVolumeAsync(string? deviceId = null,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var endpoint = OpenEndpointVolume(deviceId, out var device, out var enumerator);
        try
        {
            Marshal.ThrowExceptionForHR(endpoint.GetMasterVolumeLevelScalar(out var volume));
            Marshal.ThrowExceptionForHR(endpoint.GetMute(out var muted));
            return (volume, muted);
        }
        finally { Release(endpoint, device, enumerator); }
    }, cancellationToken);

    public Task SetMasterVolumeAsync(float? volume, bool? muted, string? deviceId = null,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var endpoint = OpenEndpointVolume(deviceId, out var device, out var enumerator);
        try
        {
            var context = Guid.Empty;
            if (volume is { } value)
                Marshal.ThrowExceptionForHR(endpoint.SetMasterVolumeLevelScalar(Math.Clamp(value, 0, 1), ref context));
            if (muted is { } isMuted) Marshal.ThrowExceptionForHR(endpoint.SetMute(isMuted, ref context));
        }
        finally { Release(endpoint, device, enumerator); }
    }, cancellationToken);

    private static IReadOnlyList<AudioDeviceCandidate> Enumerate(CancellationToken cancellationToken)
    {
        var enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumerator();
        var results = new List<AudioDeviceCandidate>();
        try
        {
            var defaults = new HashSet<(string Id, ERole Role)>(StringTupleComparer.Instance);
            foreach (var flow in new[] { EDataFlow.Render, EDataFlow.Capture })
                foreach (var role in new[] { ERole.Multimedia, ERole.Communications })
                    if (TryGetDefaultId(enumerator, flow, role) is { } id) defaults.Add((id, role));
            foreach (var flow in new[] { EDataFlow.Render, EDataFlow.Capture })
            {
                Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(flow, DeviceState.Active, out var collection));
                try
                {
                    Marshal.ThrowExceptionForHR(collection.GetCount(out var count));
                    for (uint index = 0; index < count; index++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Marshal.ThrowExceptionForHR(collection.Item(index, out var device));
                        try
                        {
                            var id = ReadId(device);
                            results.Add(new AudioDeviceCandidate(id, ReadFriendlyName(device), flow == EDataFlow.Capture,
                                defaults.Contains((id, ERole.Multimedia)), defaults.Contains((id, ERole.Communications))));
                        }
                        finally { Marshal.FinalReleaseComObject(device); }
                    }
                }
                finally { Marshal.FinalReleaseComObject(collection); }
            }
        }
        finally { Marshal.FinalReleaseComObject(enumerator); }
        return results.OrderBy(item => item.IsInput).ThenBy(item => item.FriendlyName,
            StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static string? GetDefaultId(EDataFlow flow, ERole role)
    {
        var enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumerator();
        try { return TryGetDefaultId(enumerator, flow, role); }
        finally { Marshal.FinalReleaseComObject(enumerator); }
    }

    private static string? TryGetDefaultId(IMMDeviceEnumerator enumerator, EDataFlow flow, ERole role)
    {
        var hr = enumerator.GetDefaultAudioEndpoint(flow, role, out var device);
        if (hr < 0 || device is null) return null;
        try { return ReadId(device); }
        finally { Marshal.FinalReleaseComObject(device); }
    }

    private static string ReadId(IMMDevice device)
    {
        Marshal.ThrowExceptionForHR(device.GetId(out var pointer));
        try { return Marshal.PtrToStringUni(pointer) ?? string.Empty; }
        finally { Marshal.FreeCoTaskMem(pointer); }
    }

    private static string ReadFriendlyName(IMMDevice device)
    {
        Marshal.ThrowExceptionForHR(device.OpenPropertyStore(0, out var store));
        try
        {
            var key = PropertyKey.DeviceFriendlyName;
            Marshal.ThrowExceptionForHR(store.GetValue(ref key, out var value));
            try { return value.PointerValue == IntPtr.Zero ? ReadId(device) : Marshal.PtrToStringUni(value.PointerValue) ?? ReadId(device); }
            finally { PropVariantClear(ref value); }
        }
        finally { Marshal.FinalReleaseComObject(store); }
    }

    private static IAudioEndpointVolume OpenEndpointVolume(string? id, out IMMDevice device,
        out IMMDeviceEnumerator enumerator)
    {
        enumerator = (IMMDeviceEnumerator)(object)new MMDeviceEnumerator();
        var hr = string.IsNullOrWhiteSpace(id)
            ? enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out device)
            : enumerator.GetDevice(id, out device);
        Marshal.ThrowExceptionForHR(hr);
        var iid = typeof(IAudioEndpointVolume).GUID;
        Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23, IntPtr.Zero, out var endpoint));
        return (IAudioEndpointVolume)endpoint;
    }

    private static void Release(params object?[] values)
    {
        foreach (var value in values)
            if (value is not null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value);
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string Id, ERole Role)>
    {
        public static StringTupleComparer Instance { get; } = new();
        public bool Equals((string Id, ERole Role) x, (string Id, ERole Role) y) =>
            x.Role == y.Role && string.Equals(x.Id, y.Id, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode((string Id, ERole Role) obj) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Id), obj.Role);
    }

    private enum EDataFlow { Render, Capture, All }
    private enum ERole { Console, Multimedia, Communications }
    [Flags] private enum DeviceState : uint { Active = 1 }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private sealed class MMDeviceEnumerator { }
    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private sealed class PolicyConfigClient { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow flow, DeviceState stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow flow, ERole role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0BD7A1BE-7A1A-44DB-8397-C0A2D7353E39")]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint classContext, IntPtr activationParameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore properties);
        [PreserveSig] int GetId(out IntPtr id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    private interface IPolicyConfigVista
    {
        int GetMixFormat(); int GetDeviceFormat(); int ResetDeviceFormat(); int SetDeviceFormat();
        int GetProcessingPeriod(); int SetProcessingPeriod(); int GetShareMode(); int SetShareMode();
        int GetPropertyValue(); int SetPropertyValue();
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
        int SetEndpointVisibility();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
        public static PropertyKey DeviceFriendlyName => new()
        { FormatId = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"), PropertyId = 14 };
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VariantType;
        [FieldOffset(8)] public IntPtr PointerValue;
    }

    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
}
