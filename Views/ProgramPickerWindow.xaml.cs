using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using SwitchBoard.Localization;
using SwitchBoard.Services.Discovery;

namespace SwitchBoard.Views;

public partial class ProgramPickerWindow : Window, INotifyPropertyChanged
{
    private readonly IProgramDiscoveryService _discoveryService;
    private readonly ILocalizationService _localizationService;
    private readonly ICollectionView _programView;
    private CancellationTokenSource? _searchCancellation;
    private string _searchText = string.Empty;
    private string _statusText = string.Empty;
    private bool _isBusy;
    private ProgramCandidate? _selectedProgram;

    public ProgramPickerWindow(
        IProgramDiscoveryService discoveryService,
        ILocalizationService localizationService)
    {
        _discoveryService = discoveryService;
        _localizationService = localizationService;
        InitializeComponent();
        Programs = [];
        _programView = CollectionViewSource.GetDefaultView(Programs);
        _programView.Filter = MatchesSearch;
        _programView.SortDescriptions.Add(new SortDescription(
            nameof(ProgramCandidate.DisplayName),
            ListSortDirection.Ascending));
        DataContext = this;
        Loaded += async (_, _) => await StartSearchAsync(ProgramSearchMode.CommonLocations);
        Closed += (_, _) => _searchCancellation?.Cancel();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ProgramCandidate> Programs { get; }

    public ProgramCandidate? Result { get; private set; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                _programView.Refresh();
                UpdateCompletedStatus();
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
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsNotBusy)));
            }
        }
    }

    public bool IsNotBusy => !IsBusy;

    public ProgramCandidate? SelectedProgram
    {
        get => _selectedProgram;
        set => SetProperty(ref _selectedProgram, value);
    }

    private async Task StartSearchAsync(ProgramSearchMode mode)
    {
        _searchCancellation?.Cancel();
        _searchCancellation?.Dispose();
        _searchCancellation = new CancellationTokenSource();
        Programs.Clear();
        IsBusy = true;
        StatusText = _localizationService.GetString("ProgramPicker.StartingSearch");

        var progress = new Progress<ProgramDiscoveryProgress>(update =>
        {
            foreach (var program in update.NewItems)
            {
                Programs.Add(program);
            }

            if (!string.IsNullOrWhiteSpace(update.CurrentLocation))
            {
                StatusText = _localizationService.Format(
                    "ProgramPicker.SearchProgress",
                    update.ScannedFileCount,
                    Programs.Count,
                    update.CurrentLocation);
            }
        });

        try
        {
            await _discoveryService.SearchAsync(mode, progress, _searchCancellation.Token);
            IsBusy = false;
            UpdateCompletedStatus();
        }
        catch (OperationCanceledException)
        {
            StatusText = _localizationService.Format("ProgramPicker.SearchCancelled", Programs.Count);
        }
        catch (Exception exception)
        {
            StatusText = _localizationService.Format("ProgramPicker.SearchFailed", exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool MatchesSearch(object item)
    {
        if (item is not ProgramCandidate program || string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        var search = SearchText.Trim();
        return Contains(program.DisplayName, search) ||
               Contains(program.ExecutableName, search) ||
               Contains(program.TargetPath, search);
    }

    private void UpdateCompletedStatus()
    {
        if (IsBusy)
        {
            return;
        }

        var visibleCount = Programs.Count(program => MatchesSearch(program));
        StatusText = _localizationService.Format("ProgramPicker.ResultCount", visibleCount);
    }

    private static bool Contains(string? value, string search) =>
        value?.Contains(search, StringComparison.CurrentCultureIgnoreCase) == true;

    private async void QuickSearchButton_OnClick(object sender, RoutedEventArgs e) =>
        await StartSearchAsync(ProgramSearchMode.CommonLocations);

    private async void ExtendedSearchButton_OnClick(object sender, RoutedEventArgs e) =>
        await StartSearchAsync(ProgramSearchMode.SystemDrive);

    private void CancelScanButton_OnClick(object sender, RoutedEventArgs e) => _searchCancellation?.Cancel();

    private void SelectButton_OnClick(object sender, RoutedEventArgs e) => AcceptSelection();

    private void CloseButton_OnClick(object sender, RoutedEventArgs e)
    {
        Result = null;
        DialogResult = false;
    }

    private void ProgramList_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) =>
        SelectButton.IsEnabled = SelectedProgram is not null;

    private void ProgramList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e) => AcceptSelection();

    private void AcceptSelection()
    {
        if (SelectedProgram is null)
        {
            return;
        }

        Result = SelectedProgram;
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
