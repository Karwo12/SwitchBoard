using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Media;
using SwitchBoard.Themes;

namespace SwitchBoard.Controls;

public partial class AnimatedBackground : UserControl
{
    public static readonly DependencyProperty SourcePathProperty = DependencyProperty.Register(
        nameof(SourcePath), typeof(string), typeof(AnimatedBackground),
        new PropertyMetadata(string.Empty, OnImagePropertyChanged));
    public static readonly DependencyProperty ImageStretchProperty = DependencyProperty.Register(
        nameof(ImageStretch), typeof(Stretch), typeof(AnimatedBackground),
        new PropertyMetadata(Stretch.UniformToFill, OnVisualPropertyChanged));
    public static readonly DependencyProperty ImageOpacityProperty = DependencyProperty.Register(
        nameof(ImageOpacity), typeof(double), typeof(AnimatedBackground),
        new PropertyMetadata(0d, OnVisualPropertyChanged));
    public static readonly DependencyProperty GifAnimationDirectionProperty = DependencyProperty.Register(
        nameof(GifAnimationDirection), typeof(string), typeof(AnimatedBackground),
        new PropertyMetadata(GifAnimationDirections.Normal, OnPlaybackPropertyChanged));
    public static readonly DependencyProperty GifAnimationSpeedProperty = DependencyProperty.Register(
        nameof(GifAnimationSpeed), typeof(double), typeof(AnimatedBackground),
        new PropertyMetadata(1d, OnPlaybackPropertyChanged));
    public static readonly DependencyProperty ImageFlipHorizontalProperty = DependencyProperty.Register(
        nameof(ImageFlipHorizontal), typeof(bool), typeof(AnimatedBackground),
        new PropertyMetadata(false, OnVisualPropertyChanged));
    public static readonly DependencyProperty ImageFlipVerticalProperty = DependencyProperty.Register(
        nameof(ImageFlipVertical), typeof(bool), typeof(AnimatedBackground),
        new PropertyMetadata(false, OnVisualPropertyChanged));

    private readonly DispatcherTimer _timer;
    private readonly List<BitmapSource> _frames = [];
    private readonly List<TimeSpan> _delays = [];
    private GifFrameSequencer? _sequencer;
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
    public string GifAnimationDirection { get => (string)GetValue(GifAnimationDirectionProperty); set => SetValue(GifAnimationDirectionProperty, value); }
    public double GifAnimationSpeed { get => (double)GetValue(GifAnimationSpeedProperty); set => SetValue(GifAnimationSpeedProperty, value); }
    public bool ImageFlipHorizontal { get => (bool)GetValue(ImageFlipHorizontalProperty); set => SetValue(ImageFlipHorizontalProperty, value); }
    public bool ImageFlipVertical { get => (bool)GetValue(ImageFlipVerticalProperty); set => SetValue(ImageFlipVerticalProperty, value); }

    private static void OnImagePropertyChanged(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        if (value is not AnimatedBackground control) return;
        if (args.Property == SourcePathProperty) control.Reload();
        else control.ApplyVisualSettings();
    }

    private static void OnVisualPropertyChanged(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        if (value is AnimatedBackground control) control.ApplyVisualSettings();
    }

    private static void OnPlaybackPropertyChanged(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        if (value is not AnimatedBackground control) return;
        if (args.Property == GifAnimationDirectionProperty) control.RestartPlayback();
        else control.UpdateAnimationState();
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
            RestartPlayback();
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
        ImageElement.HorizontalAlignment = ImageStretch == Stretch.None ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        ImageElement.VerticalAlignment = ImageStretch == Stretch.None ? VerticalAlignment.Center : VerticalAlignment.Stretch;
        ImageElement.RenderTransform = new ScaleTransform(ImageFlipHorizontal ? -1 : 1, ImageFlipVertical ? -1 : 1);
    }

    private void TimerOnTick(object? sender, EventArgs e)
    {
        if (_frames.Count <= 1 || _sequencer is null) { _timer.Stop(); return; }
        ImageElement.Source = _frames[_sequencer.MoveNext()];
        _timer.Interval = _sequencer.GetCurrentDelay(_delays, GifAnimationSpeed);
    }

    private void UpdateAnimationState()
    {
        var shouldRun = _frames.Count > 1 && IsVisible && _window?.WindowState != WindowState.Minimized;
        if (shouldRun)
        {
            _timer.Interval = _sequencer?.GetCurrentDelay(_delays, GifAnimationSpeed) ?? TimeSpan.FromMilliseconds(100);
            _timer.Start();
        }
        else _timer.Stop();
    }

    private void RestartPlayback()
    {
        _timer.Stop();
        _sequencer = new GifFrameSequencer(_frames.Count, GifAnimationDirection);
        if (_frames.Count > 0) ImageElement.Source = _frames[_sequencer.CurrentIndex];
        UpdateAnimationState();
    }

    private void ClearFrames()
    {
        ImageElement.Source = null;
        _frames.Clear();
        _delays.Clear();
        _sequencer = null;
    }
}
