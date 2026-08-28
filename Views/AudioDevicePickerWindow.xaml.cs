using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SwitchBoard.Localization;
using SwitchBoard.Services.Discovery;
using SwitchBoard.Services.Windows;

namespace SwitchBoard.Views;

public partial class AudioDevicePickerWindow : Window
{
    private readonly IAudioManager _manager;
    private readonly bool _input;
    private readonly ILocalizationService _localization;
    private List<AudioDeviceCandidate> _devices = [];
    public AudioDeviceCandidate? Result { get; private set; }

    public AudioDevicePickerWindow(IAudioManager manager, ILocalizationService localization, bool input)
    {
        _manager = manager; _input = input; _localization = localization;
        InitializeComponent();
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            _devices = (await _manager.GetDevicesAsync()).Where(item => item.IsInput == _input).ToList();
            ApplyFilter();
        }
        catch (Exception exception) { MessageBox.Show(this, exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private void SearchBox_OnTextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim();
        DeviceList.ItemsSource = _devices.Where(item => query.Length == 0 || item.FriendlyName.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .Select(item => new Row(item,
                item.IsInput ? _localization.GetString("Picker.Input") : _localization.GetString("Picker.Output"),
                item.IsDefaultMultimedia && item.IsDefaultCommunications ? _localization.GetString("Picker.BothRoles")
                    : item.IsDefaultMultimedia ? _localization.GetString("Picker.Multimedia")
                    : item.IsDefaultCommunications ? _localization.GetString("Picker.Communications") : string.Empty));
    }
    private void Select_OnClick(object sender, RoutedEventArgs e) => Accept();
    private void DeviceList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e) => Accept();
    private void Accept() { if (DeviceList.SelectedItem is not Row row) return; Result = row.Device; DialogResult = true; }
    private sealed record Row(AudioDeviceCandidate Device, string Direction, string DefaultText)
    { public string FriendlyName => Device.FriendlyName; }
}
