using System.Diagnostics;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace SwitchBoard.Services.Monitoring;

/// <summary>One bounded native sampler for the Performance panel. It never resolves paths or icons while refreshing.</summary>
public sealed class PerformanceMonitoringService : IDisposable
{
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly NativeGpuSampler _gpuSampler = new();
    private SystemCpuSample? _previousSystemCpu;
    private NetworkSample? _previousNetwork;
    private Dictionary<int, ProcessSample> _previousProcesses = [];
    private readonly Dictionary<int, int?> _parentProcessIds = [];
    private readonly Dictionary<int, long?> _processStartTimes = [];
    private readonly Dictionary<int, string?> _executablePaths = [];
    private readonly Dictionary<int, DateTime> _pathRetryAfter = [];
    private readonly object _processMetadataGate = new();
    private bool _disposed;

    public async Task<PerformanceSnapshot> CaptureAsync(IReadOnlySet<string> managedProcessNames,
        CancellationToken cancellationToken = default, bool resetSamples = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _captureGate.WaitAsync(cancellationToken);
        try
        {
            if (resetSamples) { _previousSystemCpu = null; _previousNetwork = null; _previousProcesses = []; }
            return await Task.Run(() => Capture(managedProcessNames, cancellationToken), cancellationToken);
        }
        finally { _captureGate.Release(); }
    }

    public Task<PerformanceProcessDetails?> GetProcessDetailsAsync(int processId, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var process = Process.GetProcessById(processId);
            string? path = null;
            try { path = process.MainModule?.FileName; } catch { }
            string? priority = null;
            try { priority = process.PriorityClass.ToString(); } catch { }
            return new PerformanceProcessDetails(processId, path, priority);
        }
        catch { return null; }
    }, cancellationToken);

    public Task<IReadOnlyDictionary<int, string?>> GetExecutablePathsAsync(
        IEnumerable<int> processIds, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var result = new Dictionary<int, string?>();
        foreach (var processId in processIds.Distinct())
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_processMetadataGate)
            {
                if (_executablePaths.TryGetValue(processId, out var cached)
                    && (cached is not null || (_pathRetryAfter.TryGetValue(processId, out var retryAfter) && retryAfter > DateTime.UtcNow)))
                {
                    result[processId] = cached;
                    continue;
                }
            }

            string? path = null;
            try
            {
                using var process = Process.GetProcessById(processId);
                try { path = process.MainModule?.FileName; } catch { }
            }
            catch { }

            lock (_processMetadataGate)
            {
                _executablePaths[processId] = path;
                if (path is null) _pathRetryAfter[processId] = DateTime.UtcNow.AddSeconds(30);
                else _pathRetryAfter.Remove(processId);
            }
            result[processId] = path;
        }

        return (IReadOnlyDictionary<int, string?>)result;
    }, cancellationToken);

    private PerformanceSnapshot Capture(IReadOnlySet<string> managedProcessNames, CancellationToken cancellationToken)
    {
        var now = Stopwatch.GetTimestamp();
        var gpu = _gpuSampler.Sample();
        var processes = ReadProcesses(now, managedProcessNames, gpu.ProcessGpuPercent, gpu.ProcessDedicatedBytes, cancellationToken);
        var memory = ReadMemory();
        var network = ReadNetwork(now);
        return new PerformanceSnapshot(DateTimeOffset.UtcNow, ReadSystemCpu(now), memory.UsedBytes, memory.TotalBytes,
            processes.Sum(item => item.DiskBytesPerSecond ?? 0), network.DownloadBytesPerSecond, network.UploadBytesPerSecond,
            gpu.TotalGpuPercent, gpu.UsedDedicatedBytes, gpu.TotalDedicatedBytes, processes,
            processes.FirstOrDefault(item => item.ProcessId == Environment.ProcessId));
    }

    private List<PerformanceProcessSnapshot> ReadProcesses(long now, IReadOnlySet<string> managedProcessNames,
        IReadOnlyDictionary<int, double> gpuPercent, IReadOnlyDictionary<int, long> gpuMemory, CancellationToken cancellationToken)
    {
        var current = new Dictionary<int, ProcessSample>();
        var result = new List<PerformanceProcessSnapshot>();
        Process[] processes;
        try { processes = Process.GetProcesses(); } catch { return result; }
        var activeIds = new HashSet<int>();
        foreach (var process in processes)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = process.Id;
                if (id <= 0) continue;
                activeIds.Add(id);
                var startTimeUtcTicks = TryGetStartTimeUtcTicks(process);
                if (_processStartTimes.TryGetValue(id, out var previousStartTimeUtcTicks)
                    && previousStartTimeUtcTicks is not null
                    && startTimeUtcTicks is not null
                    && previousStartTimeUtcTicks != startTimeUtcTicks)
                {
                    _previousProcesses.Remove(id);
                    _parentProcessIds.Remove(id);
                    lock (_processMetadataGate) _executablePaths.Remove(id);
                }
                _processStartTimes[id] = startTimeUtcTicks;
                var sample = new ProcessSample(now, TryReadCpuTime(process), TryReadIo(process));
                current[id] = sample;
                _previousProcesses.TryGetValue(id, out var previous);
                if (!_parentProcessIds.TryGetValue(id, out var parent)) _parentProcessIds[id] = parent = TryGetParentProcessId(process);
                gpuPercent.TryGetValue(id, out var processGpu); gpuMemory.TryGetValue(id, out var processVram);
                var processName = TryReadProcessName(process, id);
                result.Add(new PerformanceProcessSnapshot(id, parent, processName,
                    previous is null ? null : CalculateCpu(previous, sample), TryReadWorkingSet(process),
                    previous is null ? null : CalculateRate(previous.Timestamp, sample.Timestamp, previous.Io?.DiskTransferBytes, sample.Io?.DiskTransferBytes),
                    null, gpuPercent.ContainsKey(id) ? processGpu : null, gpuMemory.ContainsKey(id) ? processVram : null,
                    managedProcessNames.Contains(NormalizeProcessName(processName)), startTimeUtcTicks));
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            finally { process.Dispose(); }
        }
        _previousProcesses = current;
        foreach (var stale in _parentProcessIds.Keys.Where(id => !activeIds.Contains(id)).ToArray()) _parentProcessIds.Remove(stale);
        foreach (var stale in _processStartTimes.Keys.Where(id => !activeIds.Contains(id)).ToArray()) _processStartTimes.Remove(stale);
        lock (_processMetadataGate)
        {
            foreach (var stale in _executablePaths.Keys.Where(id => !activeIds.Contains(id)).ToArray()) _executablePaths.Remove(stale);
            foreach (var stale in _pathRetryAfter.Keys.Where(id => !activeIds.Contains(id)).ToArray()) _pathRetryAfter.Remove(stale);
        }
        return result;
    }

    private static long? TryGetStartTimeUtcTicks(Process process)
    {
        try { return process.StartTime.ToUniversalTime().Ticks; }
        catch { return null; }
    }
    private static string TryReadProcessName(Process process, int processId)
    {
        try { return process.ProcessName; }
        catch { return $"Process {processId}"; }
    }
    private static TimeSpan? TryReadCpuTime(Process process)
    {
        try { return process.TotalProcessorTime; }
        catch { return null; }
    }
    private static long? TryReadWorkingSet(Process process)
    {
        try { return process.WorkingSet64; }
        catch { return null; }
    }

    private double? ReadSystemCpu(long now)
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        var current = new SystemCpuSample(now, idle.ToUInt64(), kernel.ToUInt64() + user.ToUInt64()); var previous = _previousSystemCpu; _previousSystemCpu = current;
        if (previous is null || current.Total <= previous.Total) return null;
        var total = current.Total - previous.Total;
        return Math.Clamp((total - Math.Min(total, current.Idle - previous.Idle)) * 100d / total, 0d, 100d);
    }

    private static (long? UsedBytes, long? TotalBytes) ReadMemory()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status) || status.TotalPhysical == 0) return (null, null);
        var total = status.TotalPhysical > long.MaxValue ? long.MaxValue : (long)status.TotalPhysical;
        var available = status.AvailablePhysical > long.MaxValue ? long.MaxValue : (long)status.AvailablePhysical;
        return (Math.Max(0, total - available), total);
    }

    private (long? DownloadBytesPerSecond, long? UploadBytesPerSecond) ReadNetwork(long now)
    {
        try
        {
            long received = 0, sent = 0;
            foreach (var item in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (item.OperationalStatus != OperationalStatus.Up || item.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var stats = item.GetIPv4Statistics(); received += stats.BytesReceived; sent += stats.BytesSent;
            }
            var current = new NetworkSample(now, received, sent); var previous = _previousNetwork; _previousNetwork = current;
            return previous is null ? (null, null) : (CalculateRate(previous.Timestamp, now, previous.Received, received), CalculateRate(previous.Timestamp, now, previous.Sent, sent));
        }
        catch { return (null, null); }
    }

    private static double? CalculateCpu(ProcessSample previous, ProcessSample current)
    {
        if (previous.CpuTime is null || current.CpuTime is null) return null;
        var elapsed = (current.Timestamp - previous.Timestamp) / (double)Stopwatch.Frequency;
        var used = (current.CpuTime.Value - previous.CpuTime.Value).TotalSeconds;
        return elapsed <= 0 || used < 0 ? null : Math.Clamp(used * 100d / elapsed / Math.Max(1, Environment.ProcessorCount), 0d, 100d);
    }
    private static long? CalculateRate(long previousTicks, long currentTicks, long? previous, long? current)
    {
        if (previous is null || current is null || current < previous) return null;
        var seconds = (currentTicks - previousTicks) / (double)Stopwatch.Frequency;
        return seconds <= 0 ? null : (long)Math.Max(0, Math.Round((current.Value - previous.Value) / seconds));
    }
    private static IoSample? TryReadIo(Process process)
    {
        try
        {
            if (!GetProcessIoCounters(process.Handle, out var value)) return null;
            var bytes = value.ReadTransferCount + value.WriteTransferCount;
            return new IoSample(bytes > long.MaxValue ? long.MaxValue : (long)bytes);
        }
        catch { return null; }
    }
    private static int? TryGetParentProcessId(Process process)
    {
        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = OpenProcess(ProcessQueryLimitedInformation, false, process.Id);
            if (handle == IntPtr.Zero || NtQueryInformationProcess(handle, 0, out var value, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0) return null;
            var parent = value.InheritedFromUniqueProcessId.ToInt64(); return parent is > 0 and <= int.MaxValue ? (int)parent : null;
        }
        catch { return null; }
        finally { if (handle != IntPtr.Zero) CloseHandle(handle); }
    }
    public static string NormalizeProcessName(string? value) => Path.GetFileNameWithoutExtension(value?.Trim() ?? string.Empty).Trim().ToLowerInvariant();
    public void Dispose() { if (_disposed) return; _disposed = true; _gpuSampler.Dispose(); _captureGate.Dispose(); }

    private sealed record SystemCpuSample(long Timestamp, ulong Idle, ulong Total);
    private sealed record NetworkSample(long Timestamp, long Received, long Sent);
    private sealed record ProcessSample(long Timestamp, TimeSpan? CpuTime, IoSample? Io);
    private sealed record IoSample(long DiskTransferBytes);
    [StructLayout(LayoutKind.Sequential)] private struct FileTime { public uint Low, High; public ulong ToUInt64() => ((ulong)High << 32) | Low; }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatusEx { public uint Length, MemoryLoad; public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessBasicInformation { public IntPtr Reserved1, PebBaseAddress, Reserved2_0, Reserved2_1, UniqueProcessId, InheritedFromUniqueProcessId; }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationProcess(IntPtr handle, int informationClass, out ProcessBasicInformation information, int length, out int returned);
    private const uint ProcessQueryLimitedInformation = 0x1000;
}

public sealed record PerformanceSnapshot(DateTimeOffset CapturedAt, double? CpuPercent, long? MemoryUsedBytes, long? MemoryTotalBytes,
    long? DiskBytesPerSecond, long? DownloadBytesPerSecond, long? UploadBytesPerSecond, double? GpuPercent, long? VramUsedBytes,
    long? VramTotalBytes, IReadOnlyList<PerformanceProcessSnapshot> Processes, PerformanceProcessSnapshot? SwitchBoardProcess);
public sealed record PerformanceProcessSnapshot(int ProcessId, int? ParentProcessId, string ProcessName, double? CpuPercent, long? WorkingSetBytes,
    long? DiskBytesPerSecond, long? NetworkBytesPerSecond, double? GpuPercent, long? VramBytes, bool IsUsedBySwitchBoardProfile,
    long? ProcessStartTimeUtcTicks = null)
{
    public string CpuText => PerformanceFormatting.Percent(CpuPercent); public string WorkingSetText => PerformanceFormatting.Bytes(WorkingSetBytes);
    public string DiskText => PerformanceFormatting.Rate(DiskBytesPerSecond); public string NetworkText => PerformanceFormatting.Rate(NetworkBytesPerSecond);
    public string GpuText => PerformanceFormatting.Percent(GpuPercent); public string VramText => PerformanceFormatting.Bytes(VramBytes);
}
public sealed record PerformanceProcessDetails(int ProcessId, string? ExecutablePath, string? Priority);
public static class PerformanceFormatting
{
    public static string Percent(double? value) => value is { } item ? $"{item:0.#}%" : "—";
    public static string Rate(long? bytes) => bytes is { } item ? $"{Bytes(item)}/s" : "—";
    public static string Bytes(long? bytes) => bytes is { } item ? Bytes(item) : "—";
    public static string Bytes(long bytes) { var units = new[] { "B", "KB", "MB", "GB", "TB" }; double value = Math.Max(0, bytes); var unit = 0; while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; } return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}"; }
}

/// <summary>Vendor-neutral best effort reader for WDDM PDH counters.</summary>
internal sealed class NativeGpuSampler : IDisposable
{
    private static readonly Regex ProcessIdPattern = new(@"(?:^|_)pid_(?<id>\d+)(?:_|$)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private readonly PdhWildcardCounter _engine = new(@"\GPU Engine(*)\Utilization Percentage");
    private readonly PdhWildcardCounter _processMemory = new(@"\GPU Process Memory(*)\Dedicated Usage");
    private readonly PdhWildcardCounter _adapterUsed = new(@"\GPU Adapter Memory(*)\Dedicated Usage");
    private readonly PdhWildcardCounter _adapterLimit = new(@"\GPU Adapter Memory(*)\Dedicated Limit");
    public GpuSample Sample()
    {
        var engines = _engine.Read(); var processMemory = _processMemory.Read(); var used = _adapterUsed.Read(); var limits = _adapterLimit.Read();
        return new GpuSample(engines.Count == 0 ? null : Math.Min(100, engines.Values.Sum()), used.Count == 0 ? null : (long)Math.Max(0, used.Values.Sum()), limits.Count == 0 ? null : (long)Math.Max(0, limits.Values.Sum()), AggregateGpu(engines), AggregateMemory(processMemory));
    }
    private static IReadOnlyDictionary<int, double> AggregateGpu(IReadOnlyDictionary<string, double> source) => Aggregate(source).ToDictionary(item => item.Key, item => Math.Min(100, item.Value));
    private static IReadOnlyDictionary<int, long> AggregateMemory(IReadOnlyDictionary<string, double> source) => Aggregate(source).ToDictionary(item => item.Key, item => (long)Math.Max(0, item.Value));
    private static Dictionary<int, double> Aggregate(IReadOnlyDictionary<string, double> source)
    {
        var result = new Dictionary<int, double>(); foreach (var (name, value) in source) { var match = ProcessIdPattern.Match(name); if (match.Success && int.TryParse(match.Groups["id"].Value, out var id)) result[id] = result.GetValueOrDefault(id) + value; } return result;
    }
    public void Dispose() { _engine.Dispose(); _processMemory.Dispose(); _adapterUsed.Dispose(); _adapterLimit.Dispose(); }
}
internal sealed record GpuSample(double? TotalGpuPercent, long? UsedDedicatedBytes, long? TotalDedicatedBytes, IReadOnlyDictionary<int, double> ProcessGpuPercent, IReadOnlyDictionary<int, long> ProcessDedicatedBytes);
internal sealed class PdhWildcardCounter : IDisposable
{
    private const uint PdhFmtDouble = 0x00000200, PdhMoreData = 0x800007D2;
    private readonly string _path; private IntPtr _query; private readonly List<IntPtr> _counters = []; private DateTime _nextRebuild = DateTime.MinValue;
    public PdhWildcardCounter(string path) => _path = path;
    public IReadOnlyDictionary<string, double> Read()
    {
        try { if (DateTime.UtcNow >= _nextRebuild) Rebuild(); if (_query == IntPtr.Zero || _counters.Count == 0) return new Dictionary<string, double>(); PdhCollectQueryData(_query); var output = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase); foreach (var counter in _counters) foreach (var item in ReadCounter(counter)) output[item.Key] = item.Value; return output; }
        catch { return new Dictionary<string, double>(); }
    }
    private void Rebuild()
    {
        DisposeQuery(); _nextRebuild = DateTime.UtcNow.AddSeconds(30); if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) { _query = IntPtr.Zero; return; }
        foreach (var path in Expand(_path)) if (PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out var counter) == 0) _counters.Add(counter); PdhCollectQueryData(_query);
    }
    private static IEnumerable<string> Expand(string path)
    {
        uint length = 0; if (PdhExpandWildCardPathW(null, path, null, ref length, 0) != PdhMoreData || length == 0) return [];
        var buffer = new char[length]; return PdhExpandWildCardPathW(null, path, buffer, ref length, 0) == 0 ? new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries) : [];
    }
    private static IReadOnlyDictionary<string, double> ReadCounter(IntPtr counter)
    {
        uint bytes = 0, count = 0; if (PdhGetFormattedCounterArrayW(counter, PdhFmtDouble, ref bytes, ref count, IntPtr.Zero) != PdhMoreData || bytes == 0) return new Dictionary<string, double>();
        var memory = Marshal.AllocHGlobal((int)bytes); try { if (PdhGetFormattedCounterArrayW(counter, PdhFmtDouble, ref bytes, ref count, memory) != 0) return new Dictionary<string, double>(); var result = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase); var size = Marshal.SizeOf<PdhFmtCounterValueItem>(); for (var i = 0; i < count; i++) { var item = Marshal.PtrToStructure<PdhFmtCounterValueItem>(memory + i * size); if (item.Value.Status == 0 && !double.IsNaN(item.Value.DoubleValue)) result[Marshal.PtrToStringUni(item.Name) ?? string.Empty] = item.Value.DoubleValue; } return result; } finally { Marshal.FreeHGlobal(memory); }
    }
    public void Dispose() => DisposeQuery(); private void DisposeQuery() { if (_query != IntPtr.Zero) { PdhCloseQuery(_query); _query = IntPtr.Zero; } _counters.Clear(); }
    [StructLayout(LayoutKind.Sequential)] private struct PdhFmtCounterValue { public uint Status; public double DoubleValue; }
    [StructLayout(LayoutKind.Sequential)] private struct PdhFmtCounterValueItem { public IntPtr Name; public PdhFmtCounterValue Value; }
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhOpenQueryW(string? source, IntPtr userData, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern uint PdhExpandWildCardPathW(string? dataSource, string path, [Out] char[]? expanded, ref uint size, uint flags);
    [DllImport("pdh.dll")] private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bytes, ref uint count, IntPtr buffer);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
}
