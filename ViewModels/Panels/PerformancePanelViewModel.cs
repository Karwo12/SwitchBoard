using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using SwitchBoard.Data;
using SwitchBoard.Localization;
using SwitchBoard.Models.Actions;
using SwitchBoard.Services.Logging;
using SwitchBoard.Services.Discovery;
using SwitchBoard.Services.Monitoring;
using SwitchBoard.Themes;

namespace SwitchBoard.ViewModels.Panels;

public sealed class PerformancePanelViewModel : ObservableObject, IDisposable
{
    private readonly PerformanceMonitoringService _monitoring;
    private readonly Func<IEnumerable<ActionItemViewModel>> _actions;
    private readonly ILocalizationService _localization;
    private readonly IAppLogger? _logger;
    private readonly DispatcherTimer _timer;
    private readonly HashSet<int> _expandedGroups = [];
    private readonly Dictionary<int, MeasurementAggregate> _measurement = [];
    private readonly FileIconCache _iconCache;
    private readonly Dictionary<(int ProcessId, long? StartedAtUtcTicks), string?> _processPaths = [];
    private readonly Dictionary<(int ProcessId, long? StartedAtUtcTicks), PerformanceProcessRowViewModel> _rowsByIdentity = [];
    private CancellationTokenSource? _refreshCancellation, _detailsCancellation, _iconCancellation;
    private IReadOnlySet<string> _managedProcessNames = new HashSet<string>();
    private PerformanceSnapshot? _snapshot;
    private IReadOnlyList<PerformanceProcessSnapshot> _latestProcesses = [];
    private PerformanceProcessRowViewModel? _selectedProcess;
    private PerformanceProcessDetails? _selectedDetails;
    private string _sortColumn = "cpu";
    private bool _sortDescending = true, _resetSamplesOnNextRefresh, _isRefreshing, _isRunning, _disposed, _isMeasuring, _isLiveViewPaused;
    private DateTimeOffset? _measurementStartedAt;
    private DateTimeOffset? _lastMeasurementSampleAt;

    public PerformancePanelViewModel(PerformanceMonitoringService monitoring, Func<IEnumerable<ActionItemViewModel>> actions,
        ILocalizationService localization, IAppLogger? logger, Dispatcher dispatcher, FileIconCache? iconCache = null)
    {
        _monitoring = monitoring; _actions = actions; _localization = localization; _logger = logger;
        _iconCache = iconCache ?? FileIconCache.Shared;
        PerformanceProcesses = []; MeasurementResults = [];
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, PerformanceTimerOnTick, dispatcher);
        _timer.Stop();
        SortProcessesCommand = new RelayCommand<string>(SortProcesses);
        SelectProcessCommand = new RelayCommand<PerformanceProcessRowViewModel>(SelectProcess);
        ToggleProcessGroupCommand = new RelayCommand<PerformanceProcessRowViewModel>(ToggleProcessGroup);
        ToggleLiveViewCommand = new RelayCommand(ToggleLiveView);
        StartMeasurementCommand = new RelayCommand(StartMeasurement, () => !IsMeasuring);
        StopMeasurementCommand = new RelayCommand(StopMeasurement, () => IsMeasuring);
        ClearMeasurementCommand = new RelayCommand(ClearMeasurement, () => !IsMeasuring && MeasurementResults.Count > 0);
    }

    public ObservableCollection<PerformanceProcessRowViewModel> PerformanceProcesses { get; }
    public ObservableCollection<PerformanceMeasurementResult> MeasurementResults { get; }
    public RelayCommand<string> SortProcessesCommand { get; }
    public RelayCommand<PerformanceProcessRowViewModel> SelectProcessCommand { get; }
    public RelayCommand<PerformanceProcessRowViewModel> ToggleProcessGroupCommand { get; }
    public RelayCommand ToggleLiveViewCommand { get; }
    public RelayCommand StartMeasurementCommand { get; }
    public RelayCommand StopMeasurementCommand { get; }
    public RelayCommand ClearMeasurementCommand { get; }
    public bool IsMeasuring { get => _isMeasuring; private set { if (SetProperty(ref _isMeasuring, value)) { OnPropertyChanged(nameof(MeasurementDurationText)); StartMeasurementCommand.NotifyCanExecuteChanged(); StopMeasurementCommand.NotifyCanExecuteChanged(); ClearMeasurementCommand.NotifyCanExecuteChanged(); } } }
    public bool IsLiveViewPaused { get => _isLiveViewPaused; private set { if (SetProperty(ref _isLiveViewPaused, value)) OnPropertyChanged(nameof(LiveViewToggleText)); } }
    public string LiveViewToggleText => _localization.GetString(IsLiveViewPaused ? "Performance.LiveView.Resume" : "Performance.LiveView.Pause");
    public string MeasurementDurationText => _measurementStartedAt is null ? _localization.GetString("Performance.Measurement.NotRunning") : FormatDuration(DateTimeOffset.UtcNow - _measurementStartedAt.Value);
    public PerformanceProcessRowViewModel? SelectedPerformanceProcess { get => _selectedProcess; private set { if (!SetProperty(ref _selectedProcess, value)) return; OnPropertyChanged(nameof(HasSelectedPerformanceProcess)); } }
    public PerformanceProcessDetails? SelectedPerformanceProcessDetails { get => _selectedDetails; private set => SetProperty(ref _selectedDetails, value); }
    public bool HasSelectedPerformanceProcess => SelectedPerformanceProcess is not null;
    public string PerformanceCpuText => PerformanceFormatting.Percent(_snapshot?.CpuPercent);
    public string PerformanceMemoryText => FormatUsage(_snapshot?.MemoryUsedBytes, _snapshot?.MemoryTotalBytes);
    public string PerformanceDiskText => PerformanceFormatting.Rate(_snapshot?.DiskBytesPerSecond);
    public string PerformanceDownloadText => PerformanceFormatting.Rate(_snapshot?.DownloadBytesPerSecond);
    public string PerformanceUploadText => PerformanceFormatting.Rate(_snapshot?.UploadBytesPerSecond);
    public string PerformanceGpuText => PerformanceFormatting.Percent(_snapshot?.GpuPercent);
    public string PerformanceVramText => FormatUsage(_snapshot?.VramUsedBytes, _snapshot?.VramTotalBytes);
    public string SortNameIndicator => SortIndicator("name"); public string SortCpuIndicator => SortIndicator("cpu"); public string SortMemoryIndicator => SortIndicator("memory"); public string SortDiskIndicator => SortIndicator("disk"); public string SortGpuIndicator => SortIndicator("gpu"); public string SortVramIndicator => SortIndicator("vram");
    public bool IsSortNameActive => IsSortActive("name"); public bool IsSortCpuActive => IsSortActive("cpu"); public bool IsSortMemoryActive => IsSortActive("memory"); public bool IsSortDiskActive => IsSortActive("disk"); public bool IsSortGpuActive => IsSortActive("gpu"); public bool IsSortVramActive => IsSortActive("vram");
    public string MeasurementSortText => _localization.Format("Performance.Measurement.SortingBy", SortColumnText(),
        _localization.GetString(_sortDescending ? "Performance.Sort.Descending" : "Performance.Sort.Ascending"));
    internal bool IsRunning => _isRunning; internal bool IsRefreshInProgress => _isRefreshing; internal bool IsTimerEnabled => _timer.IsEnabled;

    public void Start() { if (_disposed || _isRunning) return; _isRunning = true; _managedProcessNames = BuildManagedProcessNames(_actions()); _resetSamplesOnNextRefresh = true; _timer.Start(); _ = RefreshAsync(); }
    public void Stop() { if (!_isRunning && !_timer.IsEnabled) return; _isRunning = false; _timer.Stop(); Cancel(ref _refreshCancellation); Cancel(ref _detailsCancellation); Cancel(ref _iconCancellation); }
    // Kept as a compatibility no-op while the window still reports media lifecycle state.
    public void NotifyLocalizationChanged() { NotifyPerformanceChanged(); OnPropertyChanged(nameof(MeasurementDurationText)); OnPropertyChanged(nameof(LiveViewToggleText)); OnPropertyChanged(nameof(MeasurementSortText)); }
    private void PerformanceTimerOnTick(object? sender, EventArgs e) { OnPropertyChanged(nameof(MeasurementDurationText)); if (!IsLiveViewPaused || IsMeasuring) _ = RefreshAsync(); }
    private async Task RefreshAsync()
    {
        if (_disposed || !_isRunning || _isRefreshing || (IsLiveViewPaused && !IsMeasuring)) return;
        _isRefreshing = true; var cancellation = new CancellationTokenSource(); Cancel(ref _refreshCancellation); _refreshCancellation = cancellation;
        try
        {
            var snapshot = await _monitoring.CaptureAsync(_managedProcessNames, cancellation.Token, _resetSamplesOnNextRefresh); _resetSamplesOnNextRefresh = false;
            if (cancellation.IsCancellationRequested || _disposed || !_isRunning) return;
            if (IsMeasuring) AddMeasurement(snapshot);
            if (IsLiveViewPaused) return;

            _snapshot = snapshot;
            _latestProcesses = snapshot.Processes;
            RebuildRows();
            NotifyPerformanceChanged();
            _ = ResolveProcessIconsAsync(snapshot);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _logger?.Error("PerformancePanel", exception, "Could not collect performance data."); }
        finally { if (ReferenceEquals(_refreshCancellation, cancellation)) { _refreshCancellation = null; cancellation.Dispose(); } _isRefreshing = false; }
    }
    private void SortProcesses(string? column)
    {
        var next = column is "name" or "cpu" or "memory" or "disk" or "gpu" or "vram" ? column : "cpu";
        _sortDescending = next == _sortColumn ? !_sortDescending : true; _sortColumn = next; RebuildRows();
        if (MeasurementResults.Count > 0) PublishMeasurement();
        NotifySortIndicators();
    }
    private void ToggleLiveView()
    {
        IsLiveViewPaused = !IsLiveViewPaused;
        if (!IsLiveViewPaused) _ = RefreshAsync();
    }
    private void ToggleProcessGroup(PerformanceProcessRowViewModel? row) { if (row is not { IsGroup: true }) return; if (!_expandedGroups.Add(row.ProcessId)) _expandedGroups.Remove(row.ProcessId); RebuildRows(); }
    private void SelectProcess(PerformanceProcessRowViewModel? row)
    {
        if (row is null) return;

        var isSameProcess = SelectedPerformanceProcess?.Identity == row.Identity;
        if (SelectedPerformanceProcess is not null)
            SelectedPerformanceProcess.IsDetailsExpanded = false;

        if (isSameProcess)
        {
            SelectedPerformanceProcess = null;
            SelectedPerformanceProcessDetails = null;
            Cancel(ref _detailsCancellation);
            return;
        }

        SelectedPerformanceProcess = row;
        SelectedPerformanceProcessDetails = null;
        row.IsDetailsExpanded = true;
        _ = LoadDetailsAsync(row);
    }
    private async Task LoadDetailsAsync(PerformanceProcessRowViewModel row)
    {
        var cancellation = new CancellationTokenSource(); Cancel(ref _detailsCancellation); _detailsCancellation = cancellation;
        try { var details = await _monitoring.GetProcessDetailsAsync(row.ProcessId, cancellation.Token); if (!cancellation.IsCancellationRequested && ReferenceEquals(SelectedPerformanceProcess, row)) SelectedPerformanceProcessDetails = details; }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _logger?.Error("PerformancePanel", exception, "Could not read selected process details."); }
        finally { if (ReferenceEquals(_detailsCancellation, cancellation)) { _detailsCancellation = null; cancellation.Dispose(); } }
    }
    private async Task ResolveProcessIconsAsync(PerformanceSnapshot snapshot)
    {
        if (_iconCancellation is not null) return;
        var cancellation = new CancellationTokenSource();
        _iconCancellation = cancellation;
        try
        {
            var paths = await _monitoring.GetExecutablePathsAsync(snapshot.Processes.Select(item => item.ProcessId), cancellation.Token);
            var snapshotsById = snapshot.Processes.ToDictionary(item => item.ProcessId);
            var effectivePaths = new Dictionary<(int ProcessId, long? StartedAtUtcTicks), string?>();
            foreach (var process in snapshot.Processes)
            {
                var identity = ProcessIdentity(process);
                var path = ResolveIconPath(process, paths, snapshotsById, new HashSet<int>());
                effectivePaths[identity] = path;
                _processPaths[identity] = path;
            }
            var uncachedPaths = effectivePaths.Values
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(path => path!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Where(path => !_iconCache.TryGetCachedIcon(path, out _))
                .ToArray();
            foreach (var path in uncachedPaths)
            {
                try { await _iconCache.GetSmallIconAsync(path, cancellation.Token); } catch (OperationCanceledException) { throw; }
            }
            if (!cancellation.IsCancellationRequested && !_disposed && ReferenceEquals(_snapshot, snapshot))
                foreach (var row in PerformanceProcesses) row.Icon = GetCachedIcon(row.Identity);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { _logger?.Error("PerformancePanel", exception, "Could not load process icons."); }
        finally { if (ReferenceEquals(_iconCancellation, cancellation)) { _iconCancellation = null; cancellation.Dispose(); } }
    }
    private string? ResolveIconPath(PerformanceProcessSnapshot process, IReadOnlyDictionary<int, string?> paths,
        IReadOnlyDictionary<int, PerformanceProcessSnapshot> snapshotsById, ISet<int> visited)
    {
        if (!visited.Add(process.ProcessId)) return null;
        if (paths.TryGetValue(process.ProcessId, out var path) && !string.IsNullOrWhiteSpace(path)) return path;
        if (process.ParentProcessId is { } parentId && snapshotsById.TryGetValue(parentId, out var parent) &&
            PerformanceProcessGrouping.IsLogicalChild(parent, process))
            return ResolveIconPath(parent, paths, snapshotsById, visited);
        return null;
    }
    private ImageSource? GetCachedIcon((int ProcessId, long? StartedAtUtcTicks) identity) =>
        _processPaths.TryGetValue(identity, out var path) && path is not null &&
        _iconCache.TryGetCachedIcon(path, out var icon) ? icon : null;
    private void RebuildRows()
    {
        var activeIdentities = _latestProcesses.Select(ProcessIdentity).ToHashSet();
        foreach (var stale in _rowsByIdentity.Keys.Where(key => !activeIdentities.Contains(key)).ToArray()) _rowsByIdentity.Remove(stale);
        foreach (var stale in _processPaths.Keys.Where(key => !activeIdentities.Contains(key)).ToArray()) _processPaths.Remove(stale);

        var children = PerformanceProcessGrouping.BuildLogicalChildren(_latestProcesses);
        var logicalChildProcessIds = PerformanceProcessGrouping.GetLogicalChildProcessIds(children);
        var displaySnapshots = BuildDisplaySnapshots(children);
        var groupProcessCounts = BuildGroupProcessCounts(children);
        var comparer = Comparer<PerformanceProcessSnapshot>.Create((left, right) => Compare(displaySnapshots[left.ProcessId], displaySnapshots[right.ProcessId]));
        var roots = _latestProcesses.Where(item => !logicalChildProcessIds.Contains(item.ProcessId))
            .OrderBy(item => item, comparer).ToList();
        var rows = new List<PerformanceProcessRowViewModel>(); foreach (var root in roots) AddRow(root, 0, children, displaySnapshots, groupProcessCounts, comparer, rows, new HashSet<int>());
        SynchronizeRows(rows);

        if (SelectedPerformanceProcess is not null && !activeIdentities.Contains(SelectedPerformanceProcess.Identity))
        {
            SelectedPerformanceProcess = null;
            SelectedPerformanceProcessDetails = null;
            Cancel(ref _detailsCancellation);
        }
    }
    private void AddRow(PerformanceProcessSnapshot snapshot, int depth, IReadOnlyDictionary<int, List<PerformanceProcessSnapshot>> children,
        IReadOnlyDictionary<int, PerformanceProcessSnapshot> displaySnapshots, IReadOnlyDictionary<int, int> groupProcessCounts,
        IComparer<PerformanceProcessSnapshot> comparer,
        ICollection<PerformanceProcessRowViewModel> output, ISet<int> visited)
    {
        if (!visited.Add(snapshot.ProcessId)) return;
        children.TryGetValue(snapshot.ProcessId, out var childItems);
        var isGroup = childItems is { Count: > 0 };
        var key = ProcessIdentity(snapshot);
        var displaySnapshot = displaySnapshots[snapshot.ProcessId];
        var groupProcessCount = groupProcessCounts.GetValueOrDefault(snapshot.ProcessId, 1);
        if (!_rowsByIdentity.TryGetValue(key, out var row))
            _rowsByIdentity[key] = row = new PerformanceProcessRowViewModel(displaySnapshot, depth, childItems?.Count ?? 0,
                groupProcessCount, isGroup, _expandedGroups.Contains(snapshot.ProcessId));
        else
            row.Update(displaySnapshot, depth, childItems?.Count ?? 0, groupProcessCount, isGroup,
                _expandedGroups.Contains(snapshot.ProcessId));

        var cachedIcon = GetCachedIcon(key);
        if (cachedIcon is not null) row.Icon = cachedIcon;
        output.Add(row);
        if (isGroup && _expandedGroups.Contains(snapshot.ProcessId)) foreach (var child in childItems!.OrderBy(item => item, comparer)) AddRow(child, depth + 1, children, displaySnapshots, groupProcessCounts, comparer, output, visited);
    }
    private void SynchronizeRows(IReadOnlyList<PerformanceProcessRowViewModel> rows)
    {
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            if (index < PerformanceProcesses.Count && ReferenceEquals(PerformanceProcesses[index], row)) continue;
            var currentIndex = PerformanceProcesses.IndexOf(row);
            if (currentIndex >= 0) PerformanceProcesses.Move(currentIndex, index);
            else PerformanceProcesses.Insert(index, row);
        }

        while (PerformanceProcesses.Count > rows.Count)
            PerformanceProcesses.RemoveAt(PerformanceProcesses.Count - 1);
    }
    private static (int ProcessId, long? StartedAtUtcTicks) ProcessIdentity(PerformanceProcessSnapshot snapshot) => (snapshot.ProcessId, snapshot.ProcessStartTimeUtcTicks);
    private IReadOnlyDictionary<int, PerformanceProcessSnapshot> BuildDisplaySnapshots(IReadOnlyDictionary<int, List<PerformanceProcessSnapshot>> children)
    {
        var output = new Dictionary<int, PerformanceProcessSnapshot>();
        foreach (var process in _latestProcesses) BuildDisplaySnapshot(process, children, output, new HashSet<int>());
        return output;
    }
    private static IReadOnlyDictionary<int, int> BuildGroupProcessCounts(IReadOnlyDictionary<int, List<PerformanceProcessSnapshot>> children)
    {
        var counts = new Dictionary<int, int>();
        foreach (var processId in children.Keys)
            CountGroupProcesses(processId, children, counts, new HashSet<int>());
        return counts;
    }
    private static int CountGroupProcesses(int processId, IReadOnlyDictionary<int, List<PerformanceProcessSnapshot>> children,
        IDictionary<int, int> counts, ISet<int> path)
    {
        if (counts.TryGetValue(processId, out var cached)) return cached;
        if (!path.Add(processId)) return 0;

        var total = 1;
        if (children.TryGetValue(processId, out var childItems))
            foreach (var child in childItems)
                total += CountGroupProcesses(child.ProcessId, children, counts, path);

        path.Remove(processId);
        return counts[processId] = total;
    }
    private static PerformanceProcessSnapshot BuildDisplaySnapshot(PerformanceProcessSnapshot process,
        IReadOnlyDictionary<int, List<PerformanceProcessSnapshot>> children, IDictionary<int, PerformanceProcessSnapshot> cache, ISet<int> path)
    {
        if (cache.TryGetValue(process.ProcessId, out var cached)) return cached;
        if (!path.Add(process.ProcessId)) return process;

        var items = new List<PerformanceProcessSnapshot> { process };
        if (children.TryGetValue(process.ProcessId, out var childItems))
            foreach (var child in childItems) items.Add(BuildDisplaySnapshot(child, children, cache, path));
        path.Remove(process.ProcessId);

        var aggregate = process with
        {
            CpuPercent = Sum(items.Select(item => item.CpuPercent)),
            WorkingSetBytes = Sum(items.Select(item => item.WorkingSetBytes)),
            DiskBytesPerSecond = Sum(items.Select(item => item.DiskBytesPerSecond)),
            // GPU Engine reports the busiest engine per process. The group uses
            // its busiest member as the same normalized 0-100% metric instead of
            // adding overlapping engine utilization across child processes.
            GpuPercent = MaxGpu(items.Select(item => item.GpuPercent)),
            VramBytes = Sum(items.Select(item => item.VramBytes)),
            IsUsedBySwitchBoardProfile = items.Any(item => item.IsUsedBySwitchBoardProfile)
        };
        cache[process.ProcessId] = aggregate;
        return aggregate;
    }
    private static double? Sum(IEnumerable<double?> values)
    {
        var available = values.Where(value => value is not null).Select(value => value!.Value).ToArray();
        return available.Length == 0 ? null : available.Sum();
    }
    private static double? MaxGpu(IEnumerable<double?> values)
    {
        var available = values.Where(value => value is not null).Select(value => value!.Value).ToArray();
        return available.Length == 0 ? null : available.Max();
    }
    private static long? Sum(IEnumerable<long?> values)
    {
        var available = values.Where(value => value is not null).Select(value => value!.Value).ToArray();
        return available.Length == 0 ? null : available.Aggregate(0L, (total, value) => total > long.MaxValue - value ? long.MaxValue : total + value);
    }
    private int Compare(PerformanceProcessSnapshot left, PerformanceProcessSnapshot right)
    {
        var result = _sortColumn switch
        {
            "name" => string.Compare(left.ProcessName, right.ProcessName, StringComparison.CurrentCultureIgnoreCase),
            "memory" => CompareMetric(left.WorkingSetBytes, right.WorkingSetBytes),
            "disk" => CompareMetric(left.DiskBytesPerSecond, right.DiskBytesPerSecond),
            "gpu" => CompareMetric(left.GpuPercent, right.GpuPercent),
            "vram" => CompareMetric(left.VramBytes, right.VramBytes),
            _ => CompareMetric(left.CpuPercent, right.CpuPercent)
        };
        if (_sortColumn == "name") result = _sortDescending ? -result : result;
        if (result != 0) return result;
        result = string.Compare(left.ProcessName, right.ProcessName, StringComparison.CurrentCultureIgnoreCase);
        return result != 0 ? result : left.ProcessId.CompareTo(right.ProcessId);
    }
    private int CompareMetric<T>(T? left, T? right) where T : struct, IComparable<T>
    {
        if (left is null) return right is null ? 0 : 1;
        if (right is null) return -1;
        var result = left.Value.CompareTo(right.Value);
        return _sortDescending ? -result : result;
    }
    private void StartMeasurement() { _measurement.Clear(); MeasurementResults.Clear(); _measurementStartedAt = DateTimeOffset.UtcNow; _lastMeasurementSampleAt = null; IsMeasuring = true; }
    private void StopMeasurement() { if (!IsMeasuring) return; IsMeasuring = false; PublishMeasurement(); }
    private void ClearMeasurement() { _measurement.Clear(); MeasurementResults.Clear(); _measurementStartedAt = null; _lastMeasurementSampleAt = null; OnPropertyChanged(nameof(MeasurementDurationText)); ClearMeasurementCommand.NotifyCanExecuteChanged(); }
    private void AddMeasurement(PerformanceSnapshot snapshot)
    {
        var seconds = _lastMeasurementSampleAt is { } previous ? Math.Clamp((snapshot.CapturedAt - previous).TotalSeconds, 0, 5) : 0d;
        _lastMeasurementSampleAt = snapshot.CapturedAt;
        foreach (var item in snapshot.Processes) { if (!_measurement.TryGetValue(item.ProcessId, out var aggregate)) _measurement[item.ProcessId] = aggregate = new MeasurementAggregate(item.ProcessId, item.ProcessName); aggregate.Add(item, seconds); }
    }
    private void PublishMeasurement()
    {
        var results = _measurement.Values.Select(item => item.ToResult()).ToList();
        results.Sort(CompareMeasurement);
        MeasurementResults.Clear();
        foreach (var item in results.Take(20)) MeasurementResults.Add(item);
        ClearMeasurementCommand.NotifyCanExecuteChanged();
    }
    private void NotifyPerformanceChanged() { foreach (var name in new[] { nameof(PerformanceCpuText), nameof(PerformanceMemoryText), nameof(PerformanceDiskText), nameof(PerformanceDownloadText), nameof(PerformanceUploadText), nameof(PerformanceGpuText), nameof(PerformanceVramText) }) OnPropertyChanged(name); }
    private void NotifySortIndicators() { foreach (var name in new[] { nameof(SortNameIndicator), nameof(SortCpuIndicator), nameof(SortMemoryIndicator), nameof(SortDiskIndicator), nameof(SortGpuIndicator), nameof(SortVramIndicator), nameof(IsSortNameActive), nameof(IsSortCpuActive), nameof(IsSortMemoryActive), nameof(IsSortDiskActive), nameof(IsSortGpuActive), nameof(IsSortVramActive), nameof(MeasurementSortText) }) OnPropertyChanged(name); }
    private string SortIndicator(string column) => _sortColumn == column ? (_sortDescending ? "▼" : "▲") : string.Empty;
    private bool IsSortActive(string column) => string.Equals(_sortColumn, column, StringComparison.Ordinal);
    private string SortColumnText() => _localization.GetString(_sortColumn switch
    {
        "name" => "Performance.Name", "memory" => "Performance.Memory", "disk" => "Performance.Disk",
        "gpu" => "Performance.Gpu", "vram" => "Performance.Vram", _ => "Performance.Cpu"
    });
    private int CompareMeasurement(PerformanceMeasurementResult left, PerformanceMeasurementResult right)
    {
        var result = _sortColumn switch
        {
            "name" => string.Compare(left.ProcessName, right.ProcessName, StringComparison.CurrentCultureIgnoreCase),
            "memory" => CompareMeasurementMetric(left.PeakMemoryBytes, right.PeakMemoryBytes),
            "disk" => CompareMeasurementMetric(left.DiskTotalBytes, right.DiskTotalBytes),
            "gpu" => CompareMeasurementMetric(left.AverageGpuPercent, right.AverageGpuPercent),
            "vram" => CompareMeasurementMetric(left.PeakVramBytes, right.PeakVramBytes),
            _ => CompareMeasurementMetric(left.AverageCpuPercent, right.AverageCpuPercent)
        };
        if (_sortColumn == "name") result = _sortDescending ? -result : result;
        if (result != 0) return result;
        result = string.Compare(left.ProcessName, right.ProcessName, StringComparison.CurrentCultureIgnoreCase);
        return result != 0 ? result : left.ProcessId.CompareTo(right.ProcessId);
    }
    private int CompareMeasurementMetric<T>(T? left, T? right) where T : struct, IComparable<T>
    {
        if (left is null) return right is null ? 0 : 1;
        if (right is null) return -1;
        var result = left.Value.CompareTo(right.Value);
        return _sortDescending ? -result : result;
    }
    private static void Cancel(ref CancellationTokenSource? source) { var item = Interlocked.Exchange(ref source, null); item?.Cancel(); item?.Dispose(); }
    private static IReadOnlySet<string> BuildManagedProcessNames(IEnumerable<ActionItemViewModel> actions) { var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase); foreach (var action in actions) { if (!action.IsEnabled || action.Type is not (ActionTypeIds.ProgramRun or ActionTypeIds.ProcessConfigure or ActionTypeIds.ProcessSetState or ActionTypeIds.WaitProcessStart or ActionTypeIds.WaitProcessExit or ActionTypeIds.WaitWindow)) continue; var name = PerformanceMonitoringService.NormalizeProcessName(action.Type == ActionTypeIds.ProgramRun ? action.Target : string.IsNullOrWhiteSpace(action.ProcessName) ? action.ExecutablePath : action.ProcessName); if (!string.IsNullOrWhiteSpace(name)) result.Add(name); } return result; }
    private string FormatUsage(long? used, long? total) => used is { } value && total is { } max && max > 0 ? $"{PerformanceFormatting.Bytes(value)} / {PerformanceFormatting.Bytes(max)} ({value * 100d / max:0.#}%)" : _localization.GetString("Common.Unavailable");
    private static string FormatDuration(TimeSpan value) => $"{(int)value.TotalMinutes:00}:{value.Seconds:00}";
    public void Dispose() { if (_disposed) return; Stop(); _disposed = true; _timer.Tick -= PerformanceTimerOnTick; }
}

public sealed class PerformanceProcessRowViewModel : ObservableObject
{
    private static readonly HashSet<string> KnownSystemProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "system idle process", "smss", "csrss", "wininit", "services", "lsass", "winlogon",
        "svchost", "dwm", "conhost", "dllhost", "fontdrvhost", "sihost", "audiodg", "searchhost",
        "startmenuexperiencehost", "applicationframehost", "shellexperiencehost", "runtimebroker", "taskhostw"
    };
    private PerformanceProcessSnapshot _snapshot;
    private int _depth;
    private int _childCount;
    private int _groupProcessCount;
    private bool _isGroup;
    private bool _isExpanded;
    private bool _isDetailsExpanded;
    private ImageSource? _icon;
    public PerformanceProcessRowViewModel(PerformanceProcessSnapshot snapshot, int depth, int childCount, int groupProcessCount, bool isGroup, bool isExpanded) { _snapshot = snapshot; _depth = depth; _childCount = childCount; _groupProcessCount = groupProcessCount; _isGroup = isGroup; _isExpanded = isExpanded; }
    public PerformanceProcessSnapshot Snapshot => _snapshot;
    public (int ProcessId, long? StartedAtUtcTicks) Identity => (ProcessId, Snapshot.ProcessStartTimeUtcTicks);
    public int ProcessId => Snapshot.ProcessId; public string ProcessName => Snapshot.ProcessName; public string DisplayName => IsGroup && _groupProcessCount > 1 ? $"{ProcessName} ({_groupProcessCount})" : ProcessName; public int Depth => _depth; public int ChildCount => _childCount; public bool IsGroup => _isGroup; public bool IsExpanded => _isExpanded; public double Indent => Depth * 16d; public string CpuText => Snapshot.CpuText; public string MemoryText => Snapshot.WorkingSetText; public string DiskText => Snapshot.DiskText; public string GpuText => Snapshot.GpuText; public string VramText => Snapshot.VramText; public bool IsUsedByProfile => Snapshot.IsUsedBySwitchBoardProfile;
    public bool IsSystemProcess => ProcessId <= 4 || KnownSystemProcesses.Contains(ProcessName);
    public void Update(PerformanceProcessSnapshot snapshot, int depth, int childCount, int groupProcessCount, bool isGroup, bool isExpanded)
    {
        _snapshot = snapshot;
        _depth = depth;
        _childCount = childCount;
        _groupProcessCount = groupProcessCount;
        _isGroup = isGroup;
        _isExpanded = isExpanded;
        foreach (var name in new[] { nameof(Snapshot), nameof(ProcessName), nameof(DisplayName), nameof(Depth), nameof(ChildCount), nameof(IsGroup), nameof(IsExpanded), nameof(Indent), nameof(CpuText), nameof(MemoryText), nameof(DiskText), nameof(GpuText), nameof(VramText), nameof(IsUsedByProfile) }) OnPropertyChanged(name);
    }
    public ImageSource? Icon { get => _icon; set { if (SetProperty(ref _icon, value)) OnPropertyChanged(nameof(HasIcon)); } }
    public bool HasIcon => Icon is not null;
    public bool IsDetailsExpanded { get => _isDetailsExpanded; set => SetProperty(ref _isDetailsExpanded, value); }
}
public sealed record PerformanceMeasurementResult(string ProcessName, int ProcessId, double? AverageCpuPercent,
    double? PeakCpuPercent, long? PeakMemoryBytes, long? DiskTotalBytes, double? AverageGpuPercent, long? PeakVramBytes)
{
    public string AverageCpuText => PerformanceFormatting.Percent(AverageCpuPercent);
    public string PeakCpuText => PerformanceFormatting.Percent(PeakCpuPercent);
    public string PeakMemoryText => PerformanceFormatting.Bytes(PeakMemoryBytes);
    public string DiskTotalText => PerformanceFormatting.Bytes(DiskTotalBytes);
    public string AverageGpuText => PerformanceFormatting.Percent(AverageGpuPercent);
    public string PeakVramText => PerformanceFormatting.Bytes(PeakVramBytes);
}
internal sealed class MeasurementAggregate(int processId, string processName)
{
    private int _cpuSamples, _gpuSamples; private double _cpuTotal, _gpuTotal, _peakCpu; private long _diskTotal, _peakMemory, _peakVram; private bool _hasMemory, _hasDisk, _hasVram;
    public int ProcessId { get; } = processId; public string ProcessName { get; } = processName;
    public void Add(PerformanceProcessSnapshot item, double seconds)
    {
        if (item.CpuPercent is { } cpu) { _cpuSamples++; _cpuTotal += cpu; _peakCpu = Math.Max(_peakCpu, cpu); }
        if (item.GpuPercent is { } gpu) { _gpuSamples++; _gpuTotal += gpu; }
        if (item.WorkingSetBytes is { } memory) { _hasMemory = true; _peakMemory = Math.Max(_peakMemory, memory); }
        if (item.VramBytes is { } vram) { _hasVram = true; _peakVram = Math.Max(_peakVram, vram); }
        if (item.DiskBytesPerSecond is { } disk) { _hasDisk = true; _diskTotal += (long)Math.Round(disk * seconds); }
    }
    public PerformanceMeasurementResult ToResult() => new(ProcessName, ProcessId,
        _cpuSamples == 0 ? null : _cpuTotal / _cpuSamples, _cpuSamples == 0 ? null : _peakCpu,
        _hasMemory ? _peakMemory : null, _hasDisk ? _diskTotal : null,
        _gpuSamples == 0 ? null : _gpuTotal / _gpuSamples, _hasVram ? _peakVram : null);
}
