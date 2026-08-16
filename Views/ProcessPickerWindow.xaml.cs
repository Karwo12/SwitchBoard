using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using SwitchBoard.Localization;
using SwitchBoard.Services.Discovery;

namespace SwitchBoard.Views;

public partial class ProcessPickerWindow : Window, INotifyPropertyChanged
{
    private readonly IProcessDiscoveryService _discoveryService;
    private readonly ILocalizationService _localizationService;
    private readonly ICollectionView _processView;
    private CancellationTokenSource? _refreshCancellation;
    private string _searchText = string.Empty;
    private string _statusText = string.Empty;
    private bool _isBusy;
    private ProcessCandidate? _selectedProcess;

    public ProcessPickerWindow(
        IProcessDiscoveryService discoveryService,
        ILocalizationService localizationService)
    {
        _discoveryService = discoveryService;
        _localizationService = localizationService;
        InitializeComponent();
        Processes = [];
        _processView = CollectionViewSource.GetDefaultView(Processes);
        _processView.Filter = MatchesSearch;
        DataContext = this;
        Loaded += async (_, _) => await RefreshAsync();
        Closed += (_, _) => _refreshCancellation?.Cancel();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ProcessCandidate> Processes { get; }

    public ProcessCandidate? Result { get; private set; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                _processView.Refresh();
                UpdateStatus();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public ProcessCandidate? SelectedProcess
    {
        get => _selectedProcess;
        set => SetProperty(ref _selectedProcess, value);
    }

    private async Task RefreshAsync()
    {
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = new CancellationTokenSource();
        IsBusy = true;
        Processes.Clear();
        StatusText = _localizationService.GetString("ProcessPicker.Loading");

        try
        {
            var processes = await _discoveryService.GetProcessesAsync(_refreshCancellation.Token);
            foreach (var process in processes)
            {
                Processes.Add(process);
            }

            IsBusy = false;
            UpdateStatus();
        }
        catch (OperationCanceledException)
        {
            StatusText = _localizationService.GetString("Common.Cancelled");
        }
        catch (Exception exception)
        {
            StatusText = _localizationService.Format("ProcessPicker.LoadFailed", exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool MatchesSearch(object item)
    {
        if (item is not ProcessCandidate process || string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        var search = SearchText.Trim();
        return Contains(process.DisplayName, search) ||
               Contains(process.ProcessName, search) ||
               Contains(process.WindowTitle, search) ||
               Contains(process.ExecutableName, search) ||
               Contains(process.ExecutablePath, search);
    }

    private void UpdateStatus()
    {
        if (IsBusy)
        {
            return;
        }

        var visibleCount = Processes.Count(process => MatchesSearch(process));
        StatusText = _localizationService.Format("ProcessPicker.ResultCount", visibleCount);
    }

    private static bool Contains(string? value, string search) =>
        value?.Contains(search, StringComparison.CurrentCultureIgnoreCase) == true;

    private async void RefreshButton_OnClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void SelectButton_OnClick(object sender, RoutedEventArgs e) => AcceptSelection();

    private void CancelButton_OnClick(object sender, RoutedEventArgs e)
    {
        Result = null;
        DialogResult = false;
    }

    private void ProcessList_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        SelectButton.IsEnabled = SelectedProcess is not null;

    private void ProcessList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e) => AcceptSelection();

    private void AcceptSelection()
    {
        if (SelectedProcess is null)
        {
            return;
        }

        Result = SelectedProcess;
        DialogResult = true;
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
