using System.Collections.ObjectModel;
using SwitchBoard.Localization;
using SwitchBoard.Services.Logging;
using SwitchBoard.Services.Monitoring;

namespace SwitchBoard.ViewModels.Panels;

public sealed class SystemPanelViewModel : ObservableObject, IDisposable
{
    private readonly StatusMonitoringService? _monitoring;
    private readonly Func<IEnumerable<ActionItemViewModel>> _actions;
    private readonly ILocalizationService _localization;
    private readonly IAppLogger? _logger;
    private CancellationTokenSource? _refreshCancellation;
    private SystemStatusSnapshot? _snapshot;
    private bool _isRefreshing;
    private bool _disposed;

    public SystemPanelViewModel(StatusMonitoringService? monitoring,
        Func<IEnumerable<ActionItemViewModel>> actions, ILocalizationService localization, IAppLogger? logger)
    {
        _monitoring = monitoring;
        _actions = actions;
        _localization = localization;
        _logger = logger;
        SystemDisplays = [];
        ManagedSystemTargets = [];
        RefreshSystemSummaryCommand = new AsyncRelayCommand(RefreshAsync,
            () => _monitoring is not null && !_isRefreshing && !_disposed);
    }

    public ObservableCollection<SystemDisplayStatus> SystemDisplays { get; }
    public ObservableCollection<ManagedTargetStatus> ManagedSystemTargets { get; }
    public AsyncRelayCommand RefreshSystemSummaryCommand { get; }

    public string SystemAdministratorText => _snapshot is null
        ? _localization.GetString("Common.Unavailable")
        : _snapshot.IsAdministrator
            ? _localization.GetString("System.Administrator.Yes")
            : _localization.GetString("System.Administrator.No");
    public string SystemUptimeText => _snapshot is null
        ? _localization.GetString("Common.Unavailable")
        : FormatUptime(_snapshot.Uptime);
    public string SystemActivePowerPlanText => ValueOrUnavailable(_snapshot?.ActivePowerPlanName);
    public string SystemDefaultOutputText => ValueOrUnavailable(_snapshot?.DefaultOutputDevice);
    public string SystemDefaultInputText => ValueOrUnavailable(_snapshot?.DefaultInputDevice);
    public bool HasSystemDisplays => SystemDisplays.Count > 0;
    public bool HasManagedSystemTargets => ManagedSystemTargets.Count > 0;

    public async Task RefreshAsync()
    {
        if (_monitoring is null || _disposed || _isRefreshing) return;
        _isRefreshing = true;
        RefreshSystemSummaryCommand.NotifyCanExecuteChanged();
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _refreshCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();
        try
        {
            var snapshot = await _monitoring.CaptureSystemSummaryAsync(_actions(), cancellation.Token);
            if (cancellation.IsCancellationRequested || _disposed) return;
            _snapshot = snapshot;
            Replace(SystemDisplays, snapshot.Displays);
            Replace(ManagedSystemTargets, snapshot.ManagedTargets);
            NotifySummaryChanged();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            _logger?.Error("SystemPanel", exception, "Could not collect the system summary.");
        }
        finally
        {
            if (ReferenceEquals(_refreshCancellation, cancellation))
            {
                _refreshCancellation = null;
                cancellation.Dispose();
            }
            _isRefreshing = false;
            RefreshSystemSummaryCommand.NotifyCanExecuteChanged();
        }
    }

    public void NotifyLocalizationChanged() => NotifySummaryChanged();

    private void NotifySummaryChanged()
    {
        OnPropertyChanged(nameof(SystemAdministratorText));
        OnPropertyChanged(nameof(SystemUptimeText));
        OnPropertyChanged(nameof(SystemActivePowerPlanText));
        OnPropertyChanged(nameof(SystemDefaultOutputText));
        OnPropertyChanged(nameof(SystemDefaultInputText));
        OnPropertyChanged(nameof(HasSystemDisplays));
        OnPropertyChanged(nameof(HasManagedSystemTargets));
    }

    private string ValueOrUnavailable(string? value) => string.IsNullOrWhiteSpace(value)
        ? _localization.GetString("Common.Unavailable") : value;

    private static string FormatUptime(TimeSpan uptime) => uptime.TotalDays >= 1
        ? $"{(int)uptime.TotalDays}d {uptime.Hours:00}:{uptime.Minutes:00}"
        : $"{uptime.Hours:00}:{uptime.Minutes:00}";

    private static void Replace<T>(ObservableCollection<T> destination, IEnumerable<T> source)
    {
        destination.Clear();
        foreach (var item in source) destination.Add(item);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var cancellation = Interlocked.Exchange(ref _refreshCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
        RefreshSystemSummaryCommand.NotifyCanExecuteChanged();
    }
}
