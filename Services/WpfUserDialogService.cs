using System.IO;
using System.Windows;
using Microsoft.Win32;
using SwitchBoard.Localization;
using SwitchBoard.Services.Discovery;
using SwitchBoard.Views;
using SwitchBoard.Services.Windows;

namespace SwitchBoard.Services;

public sealed class WpfUserDialogService(
    IProcessDiscoveryService processDiscoveryService,
    IProgramDiscoveryService programDiscoveryService,
    IWindowsServiceManager windowsServiceManager,
    IPowerPlanManager powerPlanManager,
    IDisplayManager displayManager,
    IAudioManager audioManager,
    IDeviceManager deviceManager,
    ILocalizationService localizationService) : IUserDialogService
{
    public bool Confirm(string title, string message) =>
        MessageBox.Show(
            message,
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;

    public string? SelectFile(string title, string filter, string? initialPath = null)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            CheckFileExists = true,
            CheckPathExists = true,
            Filter = filter
        };

        if (!string.IsNullOrWhiteSpace(initialPath))
        {
            try
            {
                if (File.Exists(initialPath))
                {
                    dialog.FileName = Path.GetFileName(initialPath);
                    dialog.InitialDirectory = Path.GetDirectoryName(Path.GetFullPath(initialPath));
                }
                else if (Directory.Exists(initialPath))
                {
                    dialog.InitialDirectory = Path.GetFullPath(initialPath);
                }
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                // Ignore an invalid previous value and let the dialog choose its default directory.
            }
        }

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public ProcessCandidate? SelectProcess(string title)
    {
        var dialog = new ProcessPickerWindow(processDiscoveryService, localizationService)
        {
            Title = title,
            Owner = Application.Current.MainWindow
        };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public ServiceCandidate? SelectService(string title)
    {
        var dialog = new ServicePickerWindow(windowsServiceManager, localizationService)
        {
            Title = title,
            Owner = Application.Current.MainWindow
        };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public PowerPlanCandidate? SelectPowerPlan(string title)
    {
        var dialog = new PowerPlanPickerWindow(powerPlanManager, localizationService)
        {
            Title = title,
            Owner = Application.Current.MainWindow
        };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public DisplayCandidate? SelectDisplay(string title)
    {
        var dialog = new DisplayPickerWindow(displayManager, localizationService)
        {
            Title = title,
            Owner = Application.Current.MainWindow
        };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public ProgramCandidate? FindProgram(string title)
    {
        var dialog = new ProgramPickerWindow(programDiscoveryService, localizationService)
        {
            Title = title,
            Owner = Application.Current.MainWindow
        };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public AudioDeviceCandidate? SelectAudioDevice(string title, bool input)
    {
        var dialog = new AudioDevicePickerWindow(audioManager, localizationService, input)
        { Title = title, Owner = Application.Current.MainWindow };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }

    public DeviceCandidate? SelectDevice(string title)
    {
        var dialog = new DevicePickerWindow(deviceManager, localizationService)
        { Title = title, Owner = Application.Current.MainWindow };
        return dialog.ShowDialog() == true ? dialog.Result : null;
    }
}
