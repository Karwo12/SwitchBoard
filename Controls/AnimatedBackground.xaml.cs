using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Media;

namespace SwitchBoard.Controls;

public partial class AnimatedBackground : UserControl
{
    public static readonly DependencyProperty SourcePathProperty = DependencyProperty.Register(
        nameof(SourcePath), typeof(string), typeof(AnimatedBackground),
        new PropertyMetadata(string.Empty, OnImagePropertyChanged));
    public static readonly DependencyProperty ImageStretchProperty = DependencyProperty.Register(
        nameof(ImageStretch), typeof(Stretch), typeof(AnimatedBackground),
        new PropertyMetadata(Stretch.UniformToFill, OnImagePropertyChanged));
    public static readonly DependencyProperty ImageOpacityProperty = DependencyProperty.Register(
        nameof(ImageOpacity), typeof(double), typeof(AnimatedBackground),
        new PropertyMetadata(0d, OnImagePropertyChanged));

    private readonly DispatcherTimer _timer;
    private readonly List<BitmapSource> _frames = [];
    private readonly List<TimeSpan> _delays = [];
    private int _frameIndex;
    private Window? _window;

    public AnimatedBackground()
    {
        InitializeComponent();
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += TimerOnTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => UpdateAnimationState();
    }

    public string SourcePath { get => (string)GetValue(SourcePathProperty); set => SetValue(SourcePathProperty, value); }
    public Stretch ImageStretch { get => (Stretch)GetValue(ImageStretchProperty); set => SetValue(ImageStretchProperty, value); }
    public double ImageOpacity { get => (double)GetValue(ImageOpacityProperty); set => SetValue(ImageOpacityProperty, value); }

    private static void OnImagePropertyChanged(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        if (value is not AnimatedBackground control) return;
        if (args.Property == SourcePathProperty) control.Reload();
        else control.ApplyVisualSettings();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window is not null) _window.StateChanged += WindowOnStateChanged;
        Reload();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        if (_window is not null) _window.StateChanged -= WindowOnStateChanged;
        _window = null;
        ClearFrames();
    }

    private void WindowOnStateChanged(object? sender, EventArgs e) => UpdateAnimationState();

    private void Reload()
    {
        if (!IsLoaded) return;
        _timer.Stop();
        ClearFrames();
        ApplyVisualSettings();
        if (string.IsNullOrWhiteSpace(SourcePath) || !File.Exists(SourcePath)) return;
        try
        {
            foreach (var frame in ThemeImageLoader.Load(SourcePath))
            {
                _frames.Add(frame.Source);
                _delays.Add(frame.Delay);
            }
            if (_frames.Count > 0) ImageElement.Source = _frames[0];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            ClearFrames();
        }
        UpdateAnimationState();
    }

    private void ApplyVisualSettings()
    {
        ImageElement.Stretch = ImageStretch;
        ImageElement.Opacity = Math.Clamp(ImageOpacity, 0, 1);
    }

    private void TimerOnTick(object? sender, EventArgs e)
    {
        if (_frames.Count <= 1) { _timer.Stop(); return; }
        _frameIndex = (_frameIndex + 1) % _frames.Count;
        ImageElement.Source = _frames[_frameIndex];
        _timer.Interval = _delays[_frameIndex];
    }

    private void UpdateAnimationState()
    {
        var shouldRun = _frames.Count > 1 && IsVisible && _window?.WindowState != WindowState.Minimized;
        if (shouldRun)
        {
            _timer.Interval = _delays[Math.Clamp(_frameIndex, 0, _delays.Count - 1)];
            _timer.Start();
        }
        else _timer.Stop();
    }

    private void ClearFrames()
    {
        ImageElement.Source = null;
        _frames.Clear();
        _delays.Clear();
        _frameIndex = 0;
    }
}
