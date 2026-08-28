using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Threading;
using SwitchBoard.Localization;

namespace SwitchBoard.Views;

public partial class DisplayConfirmationWindow : Window, INotifyPropertyChanged
{
    private readonly ILocalizationService _localization;
    private readonly DispatcherTimer _timer;
    private readonly DateTimeOffset _deadline;
    private int _remainingSeconds;

    public DisplayConfirmationWindow(TimeSpan timeout, ILocalizationService localization)
    {
        _localization = localization;
        _remainingSeconds = Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds));
        _deadline = DateTimeOffset.UtcNow.Add(timeout);
        InitializeComponent();
        DataContext = this;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += TimerOnTick;
        Loaded += (_, _) => _timer.Start();
        Closed += (_, _) => _timer.Stop();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string CountdownText => _localization.Format("DisplayConfirmation.Countdown", _remainingSeconds);

    private void TimerOnTick(object? sender, EventArgs e)
    {
        _remainingSeconds = Math.Max(0, (int)Math.Ceiling((_deadline - DateTimeOffset.UtcNow).TotalSeconds));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountdownText)));
        if (_remainingSeconds <= 0)
        {
            _timer.Stop();
            DialogResult = false;
        }
    }

    private void KeepButton_OnClick(object sender, RoutedEventArgs e) =>
        DialogResult = DateTimeOffset.UtcNow < _deadline;

    private void RevertButton_OnClick(object sender, RoutedEventArgs e) => DialogResult = false;
}
