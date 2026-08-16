using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using SwitchBoard.Localization;
using SwitchBoard.Services.Discovery;
using SwitchBoard.Services.Windows;

namespace SwitchBoard.Views;

public partial class DisplayPickerWindow : Window, INotifyPropertyChanged
{
    private readonly IDisplayManager _manager;
    private readonly ILocalizationService _localization;
    private string _statusText = string.Empty;
    private bool _isBusy;
    private DisplayPickerItem? _selectedDisplay;

    public DisplayPickerWindow(IDisplayManager manager, ILocalizationService localization)
    {
        _manager = manager;
        _localization = localization;
        InitializeComponent();
        Displays = [];
        DataContext = this;
        Loaded += async (_, _) => await LoadAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<DisplayPickerItem> Displays { get; }
    public DisplayCandidate? Result { get; private set; }
    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public bool IsBusy { get => _isBusy; private set => Set(ref _isBusy, value); }
    public DisplayPickerItem? SelectedDisplay { get => _selectedDisplay; set => Set(ref _selectedDisplay, value); }

    private async Task LoadAsync()
    {
        IsBusy = true;
        StatusText = _localization.GetString("DisplayPicker.Loading");
        try
        {
            Displays.Clear();
            foreach (var display in await _manager.GetDisplaysAsync())
            {
                Displays.Add(new DisplayPickerItem(display, _localization));
            }

            StatusText = _localization.Format("DisplayPicker.ResultCount", Displays.Count);
        }
        catch (Exception exception)
        {
            StatusText = _localization.Format("DisplayPicker.LoadFailed", exception.Message);
        }
        finally { IsBusy = false; }
    }

    private void SelectButton_OnClick(object sender, RoutedEventArgs e) => Accept();
    private void CancelButton_OnClick(object sender, RoutedEventArgs e) { Result = null; DialogResult = false; }
    private void DisplayList_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => SelectButton.IsEnabled = SelectedDisplay is not null;
    private void DisplayList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e) => Accept();
    private void Accept() { if (SelectedDisplay is null) return; Result = SelectedDisplay.Candidate; DialogResult = true; }
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null) { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; PropertyChanged?.Invoke(this, new(name)); return true; }
}

public sealed class DisplayPickerItem
{
    public DisplayPickerItem(DisplayCandidate candidate, ILocalizationService localization)
    {
        Candidate = candidate;
        Heading = localization.Format("DisplayPicker.MonitorTitle", candidate.MonitorNumber, candidate.DisplayName);
        PrimaryText = candidate.IsPrimary ? localization.GetString("DisplayPicker.Primary") : string.Empty;
    }

    public DisplayCandidate Candidate { get; }
    public string Heading { get; }
    public string CurrentModeText => Candidate.CurrentModeText;
    public string DeviceId => Candidate.DeviceId;
    public string PrimaryText { get; }
}
