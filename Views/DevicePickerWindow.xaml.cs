using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SwitchBoard.Localization;
using SwitchBoard.Services.Discovery;
using SwitchBoard.Services.Windows;

namespace SwitchBoard.Views;

public partial class DevicePickerWindow : Window
{
    private readonly IDeviceManager _manager;
    private readonly ILocalizationService _localization;
    private List<DeviceCandidate> _devices = [];
    public DeviceCandidate? Result { get; private set; }
    public DevicePickerWindow(IDeviceManager manager, ILocalizationService localization)
    {
        _manager = manager; _localization = localization; InitializeComponent(); Loaded += async (_, _) => await LoadAsync();
    }
    private async Task LoadAsync()
    {
        try { _devices = (await _manager.GetDevicesAsync()).ToList(); ApplyFilter(); }
        catch (Exception exception) { MessageBox.Show(this, exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private void SearchBox_OnTextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();
    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim();
        DeviceList.ItemsSource = _devices.Where(item => query.Length == 0 || item.FriendlyName.Contains(query, StringComparison.CurrentCultureIgnoreCase) || item.DeviceClass.Contains(query, StringComparison.CurrentCultureIgnoreCase) || item.InstanceId.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(item => new Row(item, _localization.GetString(item.IsEnabled ? "DeviceState.Enabled" : "DeviceState.Disabled")));
    }
    private void Select_OnClick(object sender, RoutedEventArgs e) => Accept();
    private void DeviceList_OnMouseDoubleClick(object sender, MouseButtonEventArgs e) => Accept();
    private void Accept() { if (DeviceList.SelectedItem is not Row row) return; Result = row.Device; DialogResult = true; }
    private sealed record Row(DeviceCandidate Device, string Status)
    { public string InstanceId => Device.InstanceId; public string FriendlyName => Device.FriendlyName; public string DeviceClass => Device.DeviceClass; }
}
