using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using SwitchBoard.Themes;

namespace SwitchBoard.Controls;

/// <summary>
/// Renders an MP4 through WPF's native MediaPlayer. The player is closed whenever
/// its source changes or the control unloads, so it does not retain media handles.
/// </summary>
public partial class VideoBackgroundPlayer : UserControl, IDisposable
{
    public static readonly DependencyProperty SourcePathProperty = DependencyProperty.Register(
        nameof(SourcePath), typeof(string), typeof(VideoBackgroundPlayer),
        new PropertyMetadata(string.Empty, OnSourceChanged));
    public static readonly DependencyProperty ImageStretchProperty = DependencyProperty.Register(
        nameof(ImageStretch), typeof(Stretch), typeof(VideoBackgroundPlayer),
        new PropertyMetadata(Stretch.UniformToFill, OnVisualChanged));
    public static readonly DependencyProperty ImageOpacityProperty = DependencyProperty.Register(
        nameof(ImageOpacity), typeof(double), typeof(VideoBackgroundPlayer),
        new PropertyMetadata(0d, OnVisualChanged));
    public static readonly DependencyProperty PlaybackSpeedProperty = DependencyProperty.Register(
        nameof(PlaybackSpeed), typeof(double), typeof(VideoBackgroundPlayer),
        new PropertyMetadata(1d, OnPlaybackChanged));
    public static readonly DependencyProperty AudioEnabledProperty = DependencyProperty.Register(
        nameof(AudioEnabled), typeof(bool), typeof(VideoBackgroundPlayer),
        new PropertyMetadata(false, OnPlaybackChanged));
    public static readonly DependencyProperty ImageFlipHorizontalProperty = DependencyProperty.Register(
        nameof(ImageFlipHorizontal), typeof(bool), typeof(VideoBackgroundPlayer),
        new PropertyMetadata(false, OnVisualChanged));
    public static readonly DependencyProperty ImageFlipVerticalProperty = DependencyProperty.Register(
        nameof(ImageFlipVertical), typeof(bool), typeof(VideoBackgroundPlayer),
        new PropertyMetadata(false, OnVisualChanged));

    private MediaPlayer? _player;
    private readonly DispatcherTimer _interactionResumeTimer;
    private readonly ScaleTransform _flipTransform = new();
    private VideoDrawing? _drawing;
    private Window? _window;
    private string? _openedSourcePath;
    private BackgroundNativeSize? _reportedNativeSize;
    private bool _mediaOpened;
    private bool _isPlaying;
    private bool _playbackRequested;
    private bool _isInteractionSuspended;
    private bool _disposed;

    public VideoBackgroundPlayer()
    {
        InitializeComponent();
        BackgroundMediaDiagnostics.RendererCreated();
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
    public double PlaybackSpeed { get => (double)GetValue(PlaybackSpeedProperty); set => SetValue(PlaybackSpeedProperty, value); }
    public bool AudioEnabled { get => (bool)GetValue(AudioEnabledProperty); set => SetValue(AudioEnabledProperty, value); }
    public bool ImageFlipHorizontal { get => (bool)GetValue(ImageFlipHorizontalProperty); set => SetValue(ImageFlipHorizontalProperty, value); }
    public bool ImageFlipVertical { get => (bool)GetValue(ImageFlipVerticalProperty); set => SetValue(ImageFlipVerticalProperty, value); }

    public event EventHandler<BackgroundNativeSizeChangedEventArgs>? NativeSizeAvailable;

    internal bool IsPlaybackRequested => _playbackRequested;
    internal bool IsPlaying => _isPlaying;
    internal bool IsInteractionSuspended => _isInteractionSuspended;

    private static void OnSourceChanged(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        if (value is VideoBackgroundPlayer control) control.Reload();
    }

    private static void OnVisualChanged(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        if (value is not VideoBackgroundPlayer control) return;
        control.ApplyVisualSettings();
        if (args.Property == ImageOpacityProperty) control.UpdatePlaybackState();
    }

    private static void OnPlaybackChanged(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        if (value is not VideoBackgroundPlayer control) return;
        control.ApplyPlaybackSettings();
        control.UpdatePlaybackState();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_disposed) return;
        DetachWindow();
        _window = Window.GetWindow(this);
        if (_window is not null)
        {
            _window.StateChanged += WindowOnStateChanged;
            _window.PreviewMouseWheel += WindowOnPreviewMouseWheel;
            _window.SizeChanged += WindowOnSizeChanged;
        }
        Reload();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CancelInteractionSuspension();
        DetachWindow();
        ReleasePlayer();
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdatePlaybackState();
    private void WindowOnStateChanged(object? sender, EventArgs e) => UpdatePlaybackState();

    private void Reload()
    {
        if (!IsLoaded || _disposed) return;
        ApplyVisualSettings();
        var sourcePath = BackgroundSourcePath.NormalizeExisting(SourcePath);
        if (_player is not null && BackgroundSourcePath.Equals(sourcePath, _openedSourcePath))
        {
            ApplyPlaybackSettings();
            UpdatePlaybackState();
            return;
        }

        ReleasePlayer();
        if (sourcePath is null) return;

        var player = new MediaPlayer();
        player.MediaOpened += PlayerOnMediaOpened;
        player.MediaEnded += PlayerOnMediaEnded;
        player.MediaFailed += PlayerOnMediaFailed;
        _player = player;
        _openedSourcePath = sourcePath;
        BackgroundMediaDiagnostics.MediaPlayerCreated();
        _drawing = new VideoDrawing { Player = player, Rect = new Rect(0, 0, 1, 1) };
        ImageElement.Source = new DrawingImage(_drawing);
        ApplyPlaybackSettings();
        try
        {
            BackgroundMediaDiagnostics.VideoOpened();
            player.Open(new Uri(sourcePath, UriKind.Absolute));
            UpdatePlaybackState();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            App.Logger?.Error("ThemeBackground", exception,
                $"MP4 background '{Path.GetFileName(sourcePath)}' could not be opened.");
            ReleasePlayer();
        }
    }

    private void PlayerOnMediaOpened(object? sender, EventArgs e)
    {
        var player = _player;
        if (!ReferenceEquals(sender, player) || player is null || _drawing is null) return;
        _mediaOpened = true;
        var nativeWidth = player.NaturalVideoWidth;
        var nativeHeight = player.NaturalVideoHeight;
        var width = Math.Max(1, nativeWidth);
        var height = Math.Max(1, nativeHeight);
        _drawing.Rect = new Rect(0, 0, width, height);
        if (_openedSourcePath is not null && nativeWidth > 0 && nativeHeight > 0)
        {
            var nativeSize = new BackgroundNativeSize(_openedSourcePath, nativeWidth, nativeHeight);
            if (_reportedNativeSize != nativeSize)
            {
                _reportedNativeSize = nativeSize;
                NativeSizeAvailable?.Invoke(this, new BackgroundNativeSizeChangedEventArgs(nativeSize));
            }
        }
        ApplyPlaybackSettings();
        UpdatePlaybackState();
    }

    private void PlayerOnMediaEnded(object? sender, EventArgs e)
    {
        var player = _player;
        if (!ReferenceEquals(sender, player) || player is null) return;
        try
        {
            _isPlaying = false;
            player.Position = TimeSpan.Zero;
            ApplyPlaybackSettings();
            UpdatePlaybackState();
        }
        catch (InvalidOperationException)
        {
            ReleasePlayer();
        }
    }

    private void PlayerOnMediaFailed(object? sender, ExceptionEventArgs e)
    {
        if (!ReferenceEquals(sender, _player)) return;
        App.Logger?.Error("ThemeBackground", e.ErrorException,
            $"MP4 background '{Path.GetFileName(SourcePath)}' could not be decoded or played.");
        ReleasePlayer();
    }

    private void ApplyVisualSettings()
    {
        ImageElement.Stretch = ImageStretch;
        ImageElement.Opacity = Math.Clamp(ImageOpacity, 0, 1);
        ImageElement.HorizontalAlignment = ImageStretch == Stretch.None ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        ImageElement.VerticalAlignment = ImageStretch == Stretch.None ? VerticalAlignment.Center : VerticalAlignment.Stretch;
        _flipTransform.ScaleX = ImageFlipHorizontal ? -1 : 1;
        _flipTransform.ScaleY = ImageFlipVertical ? -1 : 1;
    }

    private void ApplyPlaybackSettings()
    {
        if (_player is null) return;
        _player.IsMuted = !AudioEnabled;
        _player.SpeedRatio = GifAnimationSpeeds.Normalize(PlaybackSpeed);
    }

    private void UpdatePlaybackState()
    {
        var shouldPlay = IsLoaded && ImageOpacity > 0 && IsVisible && !_isInteractionSuspended &&
                         (_window is null || _window.IsVisible && _window.WindowState != WindowState.Minimized);
        _playbackRequested = shouldPlay;
        if (_player is null || !_mediaOpened) return;
        if (shouldPlay == _isPlaying) return;
        try
        {
            if (shouldPlay)
            {
                _player.Play();
                _isPlaying = true;
            }
            else
            {
                _player.Pause();
                _isPlaying = false;
            }
        }
        catch (InvalidOperationException)
        {
            ReleasePlayer();
        }
    }

    private void ReleasePlayer()
    {
        var player = _player;
        _player = null;
        _drawing = null;
        _openedSourcePath = null;
        _reportedNativeSize = null;
        _mediaOpened = false;
        _isPlaying = false;
        ImageElement.Source = null;
        if (player is null) return;
        player.MediaOpened -= PlayerOnMediaOpened;
        player.MediaEnded -= PlayerOnMediaEnded;
        player.MediaFailed -= PlayerOnMediaFailed;
        try { player.Stop(); }
        catch (InvalidOperationException) { }
        try { player.Close(); }
        catch (InvalidOperationException) { }
        BackgroundMediaDiagnostics.MediaPlayerReleased();
    }

    private void DetachWindow()
    {
        if (_window is not null)
        {
            _window.StateChanged -= WindowOnStateChanged;
            _window.PreviewMouseWheel -= WindowOnPreviewMouseWheel;
            _window.SizeChanged -= WindowOnSizeChanged;
        }
        _window = null;
    }

    private void WindowOnPreviewMouseWheel(object sender, MouseWheelEventArgs e) => SuspendForInteraction();

    private void WindowOnSizeChanged(object sender, SizeChangedEventArgs e) => SuspendForInteraction();

    internal void SuspendForInteraction()
    {
        if (_disposed || !IsLoaded || _player is null) return;
        _isInteractionSuspended = true;
        UpdatePlaybackState();
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
        UpdatePlaybackState();
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
        _interactionResumeTimer.Tick -= InteractionResumeTimerOnTick;
        CancelInteractionSuspension();
        DetachWindow();
        _playbackRequested = false;
        ReleasePlayer();
        BackgroundMediaDiagnostics.RendererReleased();
    }
}
