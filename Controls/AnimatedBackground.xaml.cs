using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

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
        if (value is AnimatedBackground control) control.Reload();
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
        ImageElement.Stretch = ImageStretch;
        ImageElement.Opacity = Math.Clamp(ImageOpacity, 0, 1);
        if (string.IsNullOrWhiteSpace(SourcePath) || !File.Exists(SourcePath)) return;
        try
        {
            if (string.Equals(Path.GetExtension(SourcePath), ".gif", StringComparison.OrdinalIgnoreCase))
                LoadGif(SourcePath);
            else
                LoadStatic(SourcePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            ClearFrames();
        }
        UpdateAnimationState();
    }

    private void LoadStatic(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        _frames.Add(image);
        _delays.Add(TimeSpan.Zero);
        ImageElement.Source = image;
    }

    private void LoadGif(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        foreach (var source in decoder.Frames)
        {
            var frame = new WriteableBitmap(source);
            frame.Freeze();
            _frames.Add(frame);
            _delays.Add(ReadDelay(source.Metadata as BitmapMetadata));
        }
        if (_frames.Count > 0) ImageElement.Source = _frames[0];
    }

    private static TimeSpan ReadDelay(BitmapMetadata? metadata)
    {
        try
        {
            if (metadata?.GetQuery("/grctlext/Delay") is ushort delay)
                return TimeSpan.FromMilliseconds(Math.Max(20, delay * 10));
        }
        catch (NotSupportedException) { }
        return TimeSpan.FromMilliseconds(100);
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
