using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using SwitchBoard.Services.Discovery;

namespace SwitchBoard.Services.Windows;

public sealed class WindowsPowerPlanManager : IPowerPlanManager
{
    private const uint AccessScheme = 16;
    private const uint ErrorMoreData = 234;
    private const uint ErrorNoMoreItems = 259;

    public Task<IReadOnlyList<PowerPlanCandidate>> GetPlansAsync(CancellationToken cancellationToken = default) =>
        Task.Run<IReadOnlyList<PowerPlanCandidate>>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var active = GetActivePlan();
            var plans = new List<PowerPlanCandidate>();
            for (uint index = 0; ; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                uint size = 0;
                var result = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, AccessScheme, index, null, ref size);
                if (result == ErrorNoMoreItems)
                {
                    break;
                }

                if (result != ErrorMoreData || size != 16)
                {
                    throw new Win32Exception((int)result, "Windows could not enumerate power plans.");
                }

                var buffer = new byte[size];
                result = PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, AccessScheme, index, buffer, ref size);
                if (result != 0)
                {
                    throw new Win32Exception((int)result, "Windows could not enumerate power plans.");
                }

                var id = new Guid(buffer);
                plans.Add(new PowerPlanCandidate(id, ReadFriendlyName(id), id == active));
            }

            return plans.OrderBy(plan => plan.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
        }, cancellationToken);

    public Task<Guid> GetActivePlanAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            return GetActivePlan();
        }, cancellationToken);

    public Task SetActivePlanAsync(Guid planId, CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = PowerSetActiveScheme(IntPtr.Zero, ref planId);
            if (result != 0)
            {
                throw new Win32Exception((int)result, $"Windows could not activate power plan '{planId:D}'.");
            }
        }, cancellationToken);

    private static Guid GetActivePlan()
    {
        var result = PowerGetActiveScheme(IntPtr.Zero, out var pointer);
        if (result != 0)
        {
            throw new Win32Exception((int)result, "Windows could not read the active power plan.");
        }

        try
        {
            return Marshal.PtrToStructure<Guid>(pointer);
        }
        finally
        {
            LocalFree(pointer);
        }
    }

    private static string ReadFriendlyName(Guid id)
    {
        uint size = 0;
        var result = PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, null, ref size);
        if ((result != 0 && result != ErrorMoreData) || size < 2)
        {
            return id.ToString("D");
        }

        var buffer = new byte[size];
        result = PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, buffer, ref size);
        if (result != 0)
        {
            return id.ToString("D");
        }

        var friendlyName = Encoding.Unicode.GetString(buffer, 0, (int)Math.Min(size, (uint)buffer.Length)).TrimEnd('\0').Trim();
        return string.IsNullOrWhiteSpace(friendlyName) ? id.ToString("D") : friendlyName;
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerEnumerate(
        IntPtr rootPowerKey,
        IntPtr schemeGuid,
        IntPtr subgroupGuid,
        uint accessFlags,
        uint index,
        byte[]? buffer,
        ref uint bufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(
        IntPtr rootPowerKey,
        ref Guid schemeGuid,
        IntPtr subgroupGuid,
        IntPtr powerSettingGuid,
        byte[]? buffer,
        ref uint bufferSize);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
