using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using SwitchBoard.Data;
using SwitchBoard.Themes;

namespace SwitchBoard.Controls;

public partial class AnimatedBackground : UserControl, IDisposable
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
    public static readonly DependencyProperty PauseWhenWindowMinimizedProperty = DependencyProperty.Register(
        nameof(PauseWhenWindowMinimized), typeof(bool), typeof(AnimatedBackground),
        new PropertyMetadata(true, OnPlaybackPropertyChanged));
    public static readonly DependencyProperty PauseWhenWindowInactiveProperty = DependencyProperty.Register(
        nameof(PauseWhenWindowInactive), typeof(bool), typeof(AnimatedBackground),
        new PropertyMetadata(false, OnPlaybackPropertyChanged));
    public static readonly DependencyProperty PauseDuringProfileExecutionProperty = DependencyProperty.Register(
        nameof(PauseDuringProfileExecution), typeof(bool), typeof(AnimatedBackground),
        new PropertyMetadata(false, OnPlaybackPropertyChanged));
    public static readonly DependencyProperty IsProfileExecutionActiveProperty = DependencyProperty.Register(
        nameof(IsProfileExecutionActive), typeof(bool), typeof(AnimatedBackground),
        new PropertyMetadata(false, OnPlaybackPropertyChanged));
    public static readonly DependencyProperty PerformanceModeProperty = DependencyProperty.Register(
        nameof(PerformanceMode), typeof(string), typeof(AnimatedBackground),
        new PropertyMetadata(BackgroundPerformanceModes.FullQuality, OnVisualPropertyChanged));
    public static readonly DependencyProperty GifFrameRateLimitProperty = DependencyProperty.Register(
        nameof(GifFrameRateLimit), typeof(string), typeof(AnimatedBackground),
        new PropertyMetadata(GifFrameRateLimits.Native, OnPlaybackPropertyChanged));

    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _interactionResumeTimer;
    private ThemeImageSequence? _frames;
    private readonly ScaleTransform _flipTransform = new();
    private GifFrameSequencer? _sequencer;
    private Window? _window;
    private string? _loadedSourcePath;
    private bool _isAnimationRunning;
    private bool _isInteractionSuspended;
    private bool _disposed;

    public AnimatedBackground()
    {
        InitializeComponent();
        BackgroundMediaDiagnostics.RendererCreated();
        // A decorative GIF must never jump ahead of mouse/keyboard input, layout or rendering.
        // DispatcherTimer does not enqueue catch-up ticks, so Background priority also avoids a
        // burst of stale frames after the UI has been busy.
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += TimerOnTick;
        _interactionResumeTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(140)
        };
        _interactionResumeTimer.Tick += InteractionResumeTimerOnTick;
        ImageElement.RenderTransform = _flipTransform;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    public string SourcePath { get => (string)GetValue(SourcePathProperty); set => SetValue(SourcePathProperty, value); }
    public Stretch ImageStretch { get => (Stretch)GetValue(ImageStretchProperty); set => SetValue(ImageStretchProperty, value); }
    public double ImageOpacity { get => (double)GetValue(ImageOpacityProperty); set => SetValue(ImageOpacityProperty, value); }
    public string GifAnimationDirection { get => (string)GetValue(GifAnimationDirectionProperty); set => SetValue(GifAnimationDirectionProperty, value); }
    public double GifAnimationSpeed { get => (double)GetValue(GifAnimationSpeedProperty); set => SetValue(GifAnimationSpeedProperty, value); }
    public bool ImageFlipHorizontal { get => (bool)GetValue(ImageFlipHorizontalProperty); set => SetValue(ImageFlipHorizontalProperty, value); }
    public bool ImageFlipVertical { get => (bool)GetValue(ImageFlipVerticalProperty); set => SetValue(ImageFlipVerticalProperty, value); }
    public bool PauseWhenWindowMinimized { get => (bool)GetValue(PauseWhenWindowMinimizedProperty); set => SetValue(PauseWhenWindowMinimizedProperty, value); }
    public bool PauseWhenWindowInactive { get => (bool)GetValue(PauseWhenWindowInactiveProperty); set => SetValue(PauseWhenWindowInactiveProperty, value); }
    public bool PauseDuringProfileExecution { get => (bool)GetValue(PauseDuringProfileExecutionProperty); set => SetValue(PauseDuringProfileExecutionProperty, value); }
    public bool IsProfileExecutionActive { get => (bool)GetValue(IsProfileExecutionActiveProperty); set => SetValue(IsProfileExecutionActiveProperty, value); }
    public string PerformanceMode { get => (string)GetValue(PerformanceModeProperty); set => SetValue(PerformanceModeProperty, value); }
    public string GifFrameRateLimit { get => (string)GetValue(GifFrameRateLimitProperty); set => SetValue(GifFrameRateLimitProperty, value); }

    public event EventHandler<BackgroundNativeSizeChangedEventArgs>? NativeSizeAvailable;

    internal int DecodedFrameCount => _frames?.Count ?? 0;
    internal bool IsAnimationRunning => _isAnimationRunning;
    internal bool IsInteractionSuspended => _isInteractionSuspended;
    internal bool IsExternalPauseRequested =>
        (PauseDuringProfileExecution && IsProfileExecutionActive) ||
        (_window is not null && ((PauseWhenWindowMinimized && _window.WindowState == WindowState.Minimized) ||
                                 (PauseWhenWindowInactive && !_window.IsActive)));
    internal bool AreDecodedFramesFrozen => _frames?.AreMaterializedFramesFrozen ?? true;
    internal bool UsesStreamingFrameStorage => _frames?.UsesStreamingStorage ?? false;

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
        if (_disposed) return;
        DetachWindow();
        _window = Window.GetWindow(this);
        if (_window is not null)
        {
            _window.StateChanged += WindowOnStateChanged;
            _window.Activated += WindowOnActivationChanged;
            _window.Deactivated += WindowOnActivationChanged;
            _window.PreviewMouseWheel += WindowOnPreviewMouseWheel;
            _window.SizeChanged += WindowOnSizeChanged;
        }
        Reload();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CancelInteractionSuspension();
        StopAnimation();
        DetachWindow();
        ClearFrames();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateAnimationState();
    private void WindowOnStateChanged(object? sender, EventArgs e) => UpdateAnimationState();
    private void WindowOnActivationChanged(object? sender, EventArgs e) => UpdateAnimationState();

    private void Reload()
    {
        if (!IsLoaded || _disposed) return;
        var sourcePath = BackgroundSourcePath.NormalizeExisting(SourcePath);
        if ((_frames?.Count ?? 0) > 0 && BackgroundSourcePath.Equals(sourcePath, _loadedSourcePath))
        {
            ApplyVisualSettings();
            UpdateAnimationState();
            return;
        }

        StopAnimation();
        ClearFrames();
        ApplyVisualSettings();
        if (sourcePath is null) return;
        try
        {
            _frames = ThemeImageLoader.Load(sourcePath);
            _loadedSourcePath = sourcePath;
            if (_frames.Count > 0)
            {
                NativeSizeAvailable?.Invoke(this, new BackgroundNativeSizeChangedEventArgs(
                    new BackgroundNativeSize(sourcePath, _frames.PixelWidth, _frames.PixelHeight)));
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
        FrameBrush.Stretch = ImageStretch;
        ImageElement.Opacity = Math.Clamp(ImageOpacity, 0, 1);
        RenderOptions.SetBitmapScalingMode(ImageElement,
            BackgroundPerformanceModes.Normalize(PerformanceMode) == BackgroundPerformanceModes.Economy
                ? BitmapScalingMode.LowQuality : BitmapScalingMode.HighQuality);
        _flipTransform.ScaleX = ImageFlipHorizontal ? -1 : 1;
        _flipTransform.ScaleY = ImageFlipVertical ? -1 : 1;
        UpdateAnimationState();
    }

    private void TimerOnTick(object? sender, EventArgs e)
    {
        var frames = _frames;
        if (frames is null || frames.Count <= 1 || _sequencer is null) { StopAnimation(); return; }
        try
        {
            FrameBrush.ImageSource = frames[_sequencer.MoveNext()].Source;
            _timer.Interval = GetFrameDelay();
        }
        catch (Exception exception) when (exception is IOException or NotSupportedException or InvalidOperationException)
        {
            StopAnimation();
            ClearFrames();
        }
    }

    private void UpdateAnimationState()
    {
        var shouldRun = (_frames?.Count ?? 0) > 1 && ImageOpacity > 0 && IsVisible && !_isInteractionSuspended &&
                        (!PauseDuringProfileExecution || !IsProfileExecutionActive) &&
                        (_window is null || (_window.IsVisible &&
                            (!PauseWhenWindowMinimized || _window.WindowState != WindowState.Minimized) &&
                            (!PauseWhenWindowInactive || _window.IsActive)));
        if (shouldRun)
        {
            _timer.Interval = GetFrameDelay();
            StartAnimation();
        }
        else StopAnimation();
    }

    private void RestartPlayback()
    {
        StopAnimation();
        var frameCount = _frames?.Count ?? 0;
        _sequencer = new GifFrameSequencer(frameCount, GifAnimationDirection);
        if (frameCount > 0) FrameBrush.ImageSource = _frames![_sequencer.CurrentIndex].Source;
        UpdateAnimationState();
    }

    private void ClearFrames()
    {
        FrameBrush.ImageSource = null;
        _frames?.Dispose();
        _frames = null;
        _sequencer = null;
        _loadedSourcePath = null;
    }

    private void StartAnimation()
    {
        if (_isAnimationRunning) return;
        _timer.Start();
        _isAnimationRunning = true;
        BackgroundMediaDiagnostics.GifTimerStarted();
    }

    private void StopAnimation()
    {
        if (!_isAnimationRunning)
        {
            _timer.Stop();
            return;
        }
        _timer.Stop();
        _isAnimationRunning = false;
        BackgroundMediaDiagnostics.GifTimerStopped();
    }

    private void DetachWindow()
    {
        if (_window is not null)
        {
            _window.StateChanged -= WindowOnStateChanged;
            _window.Activated -= WindowOnActivationChanged;
            _window.Deactivated -= WindowOnActivationChanged;
            _window.PreviewMouseWheel -= WindowOnPreviewMouseWheel;
            _window.SizeChanged -= WindowOnSizeChanged;
        }
        _window = null;
    }

    private TimeSpan GetFrameDelay() => _sequencer is null || _frames is null || _frames.Count == 0
        ? TimeSpan.FromMilliseconds(100)
        : GifFrameRateLimits.Apply(GifFrameRateLimit,
            GifAnimationTiming.ScaleDelay(
                _frames[Math.Clamp(_sequencer.CurrentIndex, 0, _frames.Count - 1)].Delay,
                GifAnimationSpeed));

    private void WindowOnPreviewMouseWheel(object sender, System.Windows.Input.MouseWheelEventArgs e) =>
        SuspendForInteraction();

    private void WindowOnSizeChanged(object sender, SizeChangedEventArgs e) => SuspendForInteraction();

    internal void SuspendForInteraction()
    {
        if (_disposed || !IsLoaded || (_frames?.Count ?? 0) <= 1) return;
        _isInteractionSuspended = true;
        StopAnimation();
        _interactionResumeTimer.Stop();
        _interactionResumeTimer.Start();
    }

    private void InteractionResumeTimerOnTick(object? sender, EventArgs e)
    {
        ResumeAfterInteraction();
    }

    internal void ResumeAfterInteraction()
    {
        _interactionResumeTimer.Stop();
        _isInteractionSuspended = false;
        UpdateAnimationState();
    }

    private void CancelInteractionSuspension()
    {
        _interactionResumeTimer.Stop();
        _isInteractionSuspended = false;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        IsVisibleChanged -= OnIsVisibleChanged;
        _timer.Tick -= TimerOnTick;
        _interactionResumeTimer.Tick -= InteractionResumeTimerOnTick;
        CancelInteractionSuspension();
        StopAnimation();
        DetachWindow();
        ClearFrames();
        BackgroundMediaDiagnostics.RendererReleased();
    }
}
