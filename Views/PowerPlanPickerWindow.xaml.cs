using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using SwitchBoard.Localization;
using SwitchBoard.Services.Discovery;
using SwitchBoard.Services.Windows;

namespace SwitchBoard.Views;

public partial class PowerPlanPickerWindow : Window, INotifyPropertyChanged
{
    private readonly IPowerPlanManager _manager;
    private readonly ILocalizationService _localization;
    private string _statusText = string.Empty;
    private bool _isBusy;
    private PowerPlanCandidate? _selectedPlan;

    public PowerPlanPickerWindow(IPowerPlanManager manager, ILocalizationService localization)
    {
        _manager = manager;
        _localization = localization;
        InitializeComponent();
        Plans = [];
        DataContext = this;
        Loaded += async (_, _) => await LoadAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<PowerPlanCandidate> Plans { get; }
    public PowerPlanCandidate? Result { get; private set; }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public PowerPlanCandidate? SelectedPlan { get => _selectedPlan; set => Set(ref _selectedPlan, value); }

    private async Task LoadAsync()
    {
        IsBusy = true;
        StatusText = _localization.GetString("PowerPlanPicker.Loading");
        try
        {
            foreach (var plan in await _manager.GetPlansAsync()) Plans.Add(plan);
            StatusText = _localization.Format("PowerPlanPicker.ResultCount", Plans.Count);
        }
        catch (Exception exception) { StatusText = _localization.Format("PowerPlanPicker.LoadFailed", exception.Message); }
        finally { IsBusy = false; }
    }

    private void SelectButton_OnClick(object sender, RoutedEventArgs e) => Accept();
    private void CancelButton_OnClick(object sender, RoutedEventArgs e) { Result = null; DialogResult = false; }
    private void PlanList_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => SelectButton.IsEnabled = SelectedPlan is not null;
    private void PlanList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e) => Accept();
    private void Accept() { if (SelectedPlan is null) return; Result = SelectedPlan; DialogResult = true; }
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; PropertyChanged?.Invoke(this, new(name)); return true; }
}
