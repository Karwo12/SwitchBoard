using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using SwitchBoard.Localization;
using SwitchBoard.Services.Discovery;
using SwitchBoard.Services.Windows;

namespace SwitchBoard.Views;

public partial class ServicePickerWindow : Window, INotifyPropertyChanged
{
    private readonly IWindowsServiceManager _manager;
    private readonly ILocalizationService _localization;
    private readonly ICollectionView _view;
    private string _searchText = string.Empty;
    private string _statusText = string.Empty;
    private bool _isBusy;
    private ServiceCandidate? _selectedService;

    public ServicePickerWindow(IWindowsServiceManager manager, ILocalizationService localization)
    {
        _manager = manager;
        _localization = localization;
        InitializeComponent();
        Services = [];
        _view = CollectionViewSource.GetDefaultView(Services);
        _view.Filter = MatchesSearch;
        DataContext = this;
        Loaded += async (_, _) => await RefreshAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<ServiceCandidate> Services { get; }
    public ServiceCandidate? Result { get; private set; }
    public string SearchText { get => _searchText; set { if (Set(ref _searchText, value)) { _view.Refresh(); UpdateStatus(); } } }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public ServiceCandidate? SelectedService { get => _selectedService; set => Set(ref _selectedService, value); }

    private async Task RefreshAsync()
    {
        IsBusy = true;
        StatusText = _localization.GetString("ServicePicker.Loading");
        try
        {
            Services.Clear();
            foreach (var service in await _manager.GetServicesAsync()) Services.Add(service);
            UpdateStatus();
        }
        catch (Exception exception) { StatusText = _localization.Format("ServicePicker.LoadFailed", exception.Message); }
        finally { IsBusy = false; }
    }

    private bool MatchesSearch(object item) => item is not ServiceCandidate service || string.IsNullOrWhiteSpace(SearchText) ||
        service.DisplayName.Contains(SearchText.Trim(), StringComparison.CurrentCultureIgnoreCase) ||
        service.ServiceName.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase);
    private void UpdateStatus() { if (!IsBusy) StatusText = _localization.Format("ServicePicker.ResultCount", Services.Count(MatchesSearch)); }
    private async void RefreshButton_OnClick(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void SelectButton_OnClick(object sender, RoutedEventArgs e) => Accept();
    private void CancelButton_OnClick(object sender, RoutedEventArgs e) { Result = null; DialogResult = false; }
    private void ServiceList_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => SelectButton.IsEnabled = SelectedService is not null;
    private void ServiceList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e) => Accept();
    private void Accept() { if (SelectedService is null) return; Result = SelectedService; DialogResult = true; }
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; PropertyChanged?.Invoke(this, new(name)); return true; }
}
