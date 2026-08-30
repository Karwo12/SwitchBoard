using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using SwitchBoard.Controls;
using SwitchBoard.Data;
using LibVlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace SwitchBoard.LibVlcPlugin;

/// <summary>
/// Optional, separately published LibVLC implementation. This assembly is not
/// referenced by the SwitchBoard executable and is loaded only from the managed
/// component directory after a user installation.
/// </summary>
public sealed class LibVlcVideoBackgroundRendererFactory : IVideoBackgroundRendererFactory
{
    private static readonly object InitializationGate = new();
    private static string? _initializedDirectory;

    public string EngineId => Mp4RendererPreferences.LibVlc;

    public IVideoBackgroundRenderer Create(string componentDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(componentDirectory);
        EnsureInitialized(componentDirectory);
        return new LibVlcVideoBackgroundRenderer(componentDirectory);
    }

    private static void EnsureInitialized(string componentDirectory)
    {
        var normalized = Path.GetFullPath(componentDirectory);
        lock (InitializationGate)
        {
            if (_initializedDirectory is not null)
            {
                if (!string.Equals(_initializedDirectory, normalized, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("LibVLC was already initialized from a different component directory.");
                return;
            }

            Core.Initialize(normalized);
            _initializedDirectory = normalized;
        }
    }
}

internal sealed class LibVlcVideoBackgroundRenderer : UserControl, IVideoBackgroundRenderer
{
    private readonly VideoView _videoView = new();
    private readonly DispatcherTimer _interactionResumeTimer;
    private LibVLC? _libVlc;
    private LibVlcMediaPlayer? _mediaPlayer;
    private Media? _media;
    private Window? _window;
    private string? _openedSourcePath;
    private bool _mediaAttached;
    private bool _hasStartedPlayback;
    private bool _isPlaying;
    private bool _playbackRequested;
    private bool _isInteractionSuspended;
    private int _loopRestartQueued;
    private bool _disposed;
    private UIElement? _overlay;

    public LibVlcVideoBackgroundRenderer(string componentDirectory)
    {
        ComponentDirectory = componentDirectory;
        Content = _videoView;
        _interactionResumeTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(140)
        };
        _interactionResumeTimer.Tick += InteractionResumeTimerOnTick;
        _videoView.Loaded += VideoViewOnLoaded;
        _videoView.Unloaded += VideoViewOnUnloaded;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
        SizeChanged += OnSizeChanged;
    }

    public string ComponentDirectory { get; }
    public FrameworkElement View => this;
    public string SourcePath { get; set; } = string.Empty;
    public Stretch ImageStretch { get; set; } = Stretch.UniformToFill;
    public double ImageOpacity { get; set; }
    public double PlaybackSpeed { get; set; } = 1d;
    // LibVLC is deliberately always muted. This property remains in the common
    // contract so ThemeBackground can configure both renderers uniformly.
    public bool AudioEnabled { get; set; }
    public bool ImageFlipHorizontal { get; set; }
    public bool ImageFlipVertical { get; set; }
    public bool PauseWhenWindowMinimized { get; set; } = true;
    public bool PauseWhenWindowInactive { get; set; }
    public bool PauseDuringProfileExecution { get; set; }
    public bool IsProfileExecutionActive { get; set; }
    public string PerformanceMode { get; set; } = BackgroundPerformanceModes.FullQuality;

    public event EventHandler<BackgroundNativeSizeChangedEventArgs>? NativeSizeAvailable;
    public event EventHandler<VideoPlaybackFailedEventArgs>? PlaybackFailed;

    public bool TryAttachOverlay(UIElement overlay)
    {
        if (_disposed) return false;
        _overlay = overlay;
        _videoView.Content = overlay;
        return true;
    }

    public void DetachOverlay(UIElement overlay)
    {
        if (!ReferenceEquals(_overlay, overlay)) return;
        _videoView.Content = null;
        _overlay = null;
    }

    private void ApplySettings()
    {
        _videoView.Opacity = Math.Clamp(ImageOpacity, 0d, 1d);
        RenderOptions.SetBitmapScalingMode(_videoView,
            BackgroundPerformanceModes.Normalize(PerformanceMode) == BackgroundPerformanceModes.Economy
                ? BitmapScalingMode.LowQuality
                : BitmapScalingMode.HighQuality);
        // VideoView is backed by native child windows, so applying a WPF transform
        // would also transform the input overlay and can prevent the native video
        // output from being created. The optional renderer therefore keeps the
        // native surface untransformed; the WPF renderer continues to provide the
        // existing flip behaviour.
        ApplyAspectRatio();
        ApplyPlaybackSettings();
        UpdatePlaybackState();
    }

    public void Reload()
    {
        // LibVLCSharp's WPF integration requires the native VideoView host to
        // be loaded before a MediaPlayer is attached to it.
        if (!IsLoaded || !_videoView.IsLoaded || _disposed) return;
        ApplySettings();
        var sourcePath = NormalizeExisting(SourcePath);
        if (_mediaPlayer is not null && string.Equals(sourcePath, _openedSourcePath, StringComparison.OrdinalIgnoreCase))
        {
            UpdatePlaybackState();
            return;
        }

        ReleasePlayer();
        if (sourcePath is null) return;
        try
        {
            _libVlc = new LibVLC(enableDebugLogs: false, "--no-audio");
            _mediaPlayer = new LibVlcMediaPlayer(_libVlc)
            {
                Mute = true,
                EnableKeyInput = false,
                EnableMouseInput = false
            };
            _mediaPlayer.EncounteredError += MediaPlayerOnEncounteredError;
            _mediaPlayer.Playing += MediaPlayerOnPlaying;
            _mediaPlayer.EndReached += MediaPlayerOnEndReached;
            _media = new Media(_libVlc, new Uri(sourcePath, UriKind.Absolute));
            _openedSourcePath = sourcePath;
            _videoView.MediaPlayer = _mediaPlayer;
            _mediaAttached = true;
            ApplyPlaybackSettings();
            ApplyAspectRatio();
            UpdatePlaybackState();
        }
        catch (Exception exception)
        {
            ReleasePlayer();
            PlaybackFailed?.Invoke(this, new VideoPlaybackFailedEventArgs(sourcePath, exception));
        }
    }

    public void Refresh() => Reload();

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
        }
        Reload();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CancelInteractionSuspension();
        DetachWindow();
        ReleasePlayer();
    }

    private void VideoViewOnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_disposed) Reload();
    }

    private void VideoViewOnUnloaded(object sender, RoutedEventArgs e) => UpdatePlaybackState();

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdatePlaybackState();
    private void OnSizeChanged(object sender, SizeChangedEventArgs e) => ApplyAspectRatio();
    private void WindowOnStateChanged(object? sender, EventArgs e) => UpdatePlaybackState();
    private void WindowOnActivationChanged(object? sender, EventArgs e) => UpdatePlaybackState();
    private void WindowOnPreviewMouseWheel(object sender, MouseWheelEventArgs e) => SuspendForInteraction();

    private void MediaPlayerOnPlaying(object? sender, EventArgs e)
    {
        var playingPlayer = _mediaPlayer;
        if (playingPlayer is null || _disposed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        try
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
            {
                if (_disposed || !ReferenceEquals(playingPlayer, _mediaPlayer) || _openedSourcePath is null) return;
                try
                {
                    uint width = 0;
                    uint height = 0;
                    if (playingPlayer.Size(0, ref width, ref height) && width > 0 && height > 0)
                        NativeSizeAvailable?.Invoke(this, new BackgroundNativeSizeChangedEventArgs(
                            new BackgroundNativeSize(_openedSourcePath, checked((int)width), checked((int)height))));
                }
                catch (Exception)
                {
                    // Native size is only an optional auto-fit hint. Playback remains valid without it.
                }
            }));
        }
        catch (InvalidOperationException)
        {
            // The owning dispatcher can be closing while LibVLC reports playback.
        }
    }

    private void MediaPlayerOnEncounteredError(object? sender, EventArgs e)
    {
        var failedPlayer = _mediaPlayer;
        if (failedPlayer is null || _disposed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        var source = _openedSourcePath ?? SourcePath;
        try
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
            {
                if (_disposed || !ReferenceEquals(failedPlayer, _mediaPlayer)) return;
                PlaybackFailed?.Invoke(this, new VideoPlaybackFailedEventArgs(source));
            }));
        }
        catch (InvalidOperationException)
        {
            // The owning dispatcher can be closing while LibVLC reports an error.
        }
    }

    private void MediaPlayerOnEndReached(object? sender, EventArgs e)
    {
        var endingPlayer = _mediaPlayer;
        var loopMedia = _media;
        if (endingPlayer is null || loopMedia is null || _disposed || !_playbackRequested ||
            Interlocked.Exchange(ref _loopRestartQueued, 1) != 0)
            return;

        // LibVLC raises EndReached from a native worker. Reusing the current
        // player from the thread pool is the pattern recommended by LibVLCSharp
        // for consecutive playback, and avoids creating a second player/timer.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                if (_disposed || !_playbackRequested || !ReferenceEquals(endingPlayer, _mediaPlayer)) return;
                if (!endingPlayer.Play(loopMedia))
                {
                    _isPlaying = false;
                    RaisePlaybackFailed(_openedSourcePath ?? SourcePath, null);
                }
            }
            catch (Exception exception) when (exception is VLCException or ObjectDisposedException or InvalidOperationException)
            {
                _isPlaying = false;
                RaisePlaybackFailed(_openedSourcePath ?? SourcePath, exception);
            }
            finally
            {
                Interlocked.Exchange(ref _loopRestartQueued, 0);
            }
        });
    }

    private void RaisePlaybackFailed(string source, Exception? exception)
    {
        if (_disposed || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        try
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
            {
                if (!_disposed) PlaybackFailed?.Invoke(this, new VideoPlaybackFailedEventArgs(source, exception));
            }));
        }
        catch (InvalidOperationException)
        {
            // The owner window can be closing while LibVLC ends its worker thread.
        }
    }

    private void ApplyPlaybackSettings()
    {
        if (_mediaPlayer is null) return;
        _mediaPlayer.Mute = true;
        _mediaPlayer.SetRate((float)Math.Clamp(PlaybackSpeed, 0.5d, 2d));
    }

    private void ApplyAspectRatio()
    {
        if (_mediaPlayer is null) return;
        try
        {
            _mediaPlayer.AspectRatio = ImageStretch switch
            {
                Stretch.Fill when ActualWidth > 0 && ActualHeight > 0 => ToAspectRatio(ActualWidth, ActualHeight),
                Stretch.UniformToFill when ActualWidth > 0 && ActualHeight > 0 => ToAspectRatio(ActualWidth, ActualHeight),
                _ => null
            };
        }
        catch (ObjectDisposedException) { }
    }

    private void UpdatePlaybackState()
    {
        var shouldPlay = IsLoaded && _videoView.IsLoaded && ImageOpacity > 0 && IsVisible && !_isInteractionSuspended &&
                         (!PauseDuringProfileExecution || !IsProfileExecutionActive) &&
                         (_window is null || (_window.IsVisible &&
                             (!PauseWhenWindowMinimized || _window.WindowState != WindowState.Minimized) &&
                             (!PauseWhenWindowInactive || _window.IsActive)));
        _playbackRequested = shouldPlay;
        if (_mediaPlayer is null || _media is null || !_mediaAttached) return;
        if (shouldPlay == _isPlaying) return;
        try
        {
            if (shouldPlay)
            {
                if (_hasStartedPlayback)
                    _mediaPlayer.Play();
                else
                {
                    _mediaPlayer.Play(_media);
                    _hasStartedPlayback = true;
                }
                _isPlaying = true;
            }
            else
            {
                _mediaPlayer.SetPause(true);
                _isPlaying = false;
            }
        }
        catch (Exception exception) when (exception is VLCException or ObjectDisposedException or InvalidOperationException)
        {
            var source = _openedSourcePath ?? SourcePath;
            ReleasePlayer();
            PlaybackFailed?.Invoke(this, new VideoPlaybackFailedEventArgs(source, exception));
        }
    }

    private void ReleasePlayer()
    {
        var mediaPlayer = _mediaPlayer;
        _mediaPlayer = null;
        var media = _media;
        _media = null;
        var libVlc = _libVlc;
        _libVlc = null;
        _openedSourcePath = null;
        _mediaAttached = false;
        _hasStartedPlayback = false;
        _isPlaying = false;
        Interlocked.Exchange(ref _loopRestartQueued, 0);
        if (mediaPlayer is not null)
        {
            mediaPlayer.EncounteredError -= MediaPlayerOnEncounteredError;
            mediaPlayer.Playing -= MediaPlayerOnPlaying;
            mediaPlayer.EndReached -= MediaPlayerOnEndReached;
            try { mediaPlayer.Stop(); } catch (VLCException) { }
            _videoView.MediaPlayer = null;
            mediaPlayer.Dispose();
        }
        media?.Dispose();
        libVlc?.Dispose();
    }

    private void DetachWindow()
    {
        if (_window is not null)
        {
            _window.StateChanged -= WindowOnStateChanged;
            _window.Activated -= WindowOnActivationChanged;
            _window.Deactivated -= WindowOnActivationChanged;
            _window.PreviewMouseWheel -= WindowOnPreviewMouseWheel;
        }
        _window = null;
    }

    private void SuspendForInteraction()
    {
        if (_disposed || !IsLoaded) return;
        _isInteractionSuspended = true;
        UpdatePlaybackState();
        _interactionResumeTimer.Stop();
        _interactionResumeTimer.Start();
    }

    private void InteractionResumeTimerOnTick(object? sender, EventArgs e)
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

    private static string? NormalizeExisting(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            var fullPath = Path.GetFullPath(path);
            return File.Exists(fullPath) ? fullPath : null;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string ToAspectRatio(double width, double height) =>
        $"{Math.Max(1, (int)Math.Round(width))}:{Math.Max(1, (int)Math.Round(height))}";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _videoView.Loaded -= VideoViewOnLoaded;
        _videoView.Unloaded -= VideoViewOnUnloaded;
        Loaded -= OnLoaded;
        Unloaded -= OnUnloaded;
        IsVisibleChanged -= OnIsVisibleChanged;
        SizeChanged -= OnSizeChanged;
        _interactionResumeTimer.Tick -= InteractionResumeTimerOnTick;
        CancelInteractionSuspension();
        DetachWindow();
        _playbackRequested = false;
        if (_overlay is not null) DetachOverlay(_overlay);
        ReleasePlayer();
        _videoView.Dispose();
    }
}
