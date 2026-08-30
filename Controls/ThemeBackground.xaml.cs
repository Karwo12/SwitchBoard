using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SwitchBoard.Data;
using SwitchBoard.Services.Media;
using SwitchBoard.Themes;

namespace SwitchBoard.Controls;

/// <summary>
/// Owns exactly one renderer for the current background asset. The ordinary WPF
/// renderer remains the default. LibVLC is selected only when the separately
/// installed component is explicitly requested or automatic fallback needs it.
/// </summary>
public partial class ThemeBackground : UserControl
{
    public static readonly DependencyProperty SourcePathProperty = Register<string>(nameof(SourcePath), string.Empty);
    public static readonly DependencyProperty ImageStretchProperty = Register<Stretch>(nameof(ImageStretch), Stretch.UniformToFill);
    public static readonly DependencyProperty ImageOpacityProperty = Register<double>(nameof(ImageOpacity), 0d);
    public static readonly DependencyProperty GifAnimationDirectionProperty = Register<string>(nameof(GifAnimationDirection), GifAnimationDirections.Normal);
    public static readonly DependencyProperty GifAnimationSpeedProperty = Register<double>(nameof(GifAnimationSpeed), 1d);
    public static readonly DependencyProperty VideoPlaybackSpeedProperty = Register<double>(nameof(VideoPlaybackSpeed), 1d);
    public static readonly DependencyProperty VideoAudioEnabledProperty = Register<bool>(nameof(VideoAudioEnabled), false);
    public static readonly DependencyProperty ImageFlipHorizontalProperty = Register<bool>(nameof(ImageFlipHorizontal), false);
    public static readonly DependencyProperty ImageFlipVerticalProperty = Register<bool>(nameof(ImageFlipVertical), false);
    public static readonly DependencyProperty PauseWhenWindowMinimizedProperty = Register<bool>(nameof(PauseWhenWindowMinimized), true);
    public static readonly DependencyProperty PauseWhenWindowInactiveProperty = Register<bool>(nameof(PauseWhenWindowInactive), false);
    public static readonly DependencyProperty PauseDuringProfileExecutionProperty = Register<bool>(nameof(PauseDuringProfileExecution), false);
    public static readonly DependencyProperty IsProfileExecutionActiveProperty = Register<bool>(nameof(IsProfileExecutionActive), false);
    public static readonly DependencyProperty PerformanceModeProperty = Register<string>(nameof(PerformanceMode), BackgroundPerformanceModes.FullQuality);
    public static readonly DependencyProperty GifFrameRateLimitProperty = Register<string>(nameof(GifFrameRateLimit), GifFrameRateLimits.Native);
    public static readonly DependencyProperty Mp4RendererPreferenceProperty = Register<string>(nameof(Mp4RendererPreference), Mp4RendererPreferences.Automatic);

    private UIElement? _externalOverlay;
    private Panel? _externalOverlayParent;
    private int _externalOverlayIndex = -1;
    private FrameworkElement? _activeRenderer;
    private IVideoBackgroundRenderer? _activeVideoRenderer;
    private DispatcherOperation? _pendingUpdate;
    private BackgroundAssetKind _activeAssetKind;
    private VideoEngine _activeVideoEngine;
    private readonly BackgroundNativeSizeCache _nativeSizeCache = new();
    private string? _failedWmpSourcePath;
    private string? _failedLibVlcSourcePath;
    private string? _reportedProblemKey;
    private bool _overlayAttachedToVideo;

    public ThemeBackground()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public string SourcePath { get => (string)GetValue(SourcePathProperty); set => SetValue(SourcePathProperty, value); }
    public Stretch ImageStretch { get => (Stretch)GetValue(ImageStretchProperty); set => SetValue(ImageStretchProperty, value); }
    public double ImageOpacity { get => (double)GetValue(ImageOpacityProperty); set => SetValue(ImageOpacityProperty, value); }
    public string GifAnimationDirection { get => (string)GetValue(GifAnimationDirectionProperty); set => SetValue(GifAnimationDirectionProperty, value); }
    public double GifAnimationSpeed { get => (double)GetValue(GifAnimationSpeedProperty); set => SetValue(GifAnimationSpeedProperty, value); }
    public double VideoPlaybackSpeed { get => (double)GetValue(VideoPlaybackSpeedProperty); set => SetValue(VideoPlaybackSpeedProperty, value); }
    public bool VideoAudioEnabled { get => (bool)GetValue(VideoAudioEnabledProperty); set => SetValue(VideoAudioEnabledProperty, value); }
    public bool ImageFlipHorizontal { get => (bool)GetValue(ImageFlipHorizontalProperty); set => SetValue(ImageFlipHorizontalProperty, value); }
    public bool ImageFlipVertical { get => (bool)GetValue(ImageFlipVerticalProperty); set => SetValue(ImageFlipVerticalProperty, value); }
    public bool PauseWhenWindowMinimized { get => (bool)GetValue(PauseWhenWindowMinimizedProperty); set => SetValue(PauseWhenWindowMinimizedProperty, value); }
    public bool PauseWhenWindowInactive { get => (bool)GetValue(PauseWhenWindowInactiveProperty); set => SetValue(PauseWhenWindowInactiveProperty, value); }
    public bool PauseDuringProfileExecution { get => (bool)GetValue(PauseDuringProfileExecutionProperty); set => SetValue(PauseDuringProfileExecutionProperty, value); }
    public bool IsProfileExecutionActive { get => (bool)GetValue(IsProfileExecutionActiveProperty); set => SetValue(IsProfileExecutionActiveProperty, value); }
    public string PerformanceMode { get => (string)GetValue(PerformanceModeProperty); set => SetValue(PerformanceModeProperty, value); }
    public string GifFrameRateLimit { get => (string)GetValue(GifFrameRateLimitProperty); set => SetValue(GifFrameRateLimitProperty, value); }
    public string Mp4RendererPreference { get => (string)GetValue(Mp4RendererPreferenceProperty); set => SetValue(Mp4RendererPreferenceProperty, value); }

    public event EventHandler<BackgroundNativeSizeChangedEventArgs>? NativeSizeChanged;
    public event EventHandler<VideoBackendProblemEventArgs>? VideoBackendProblem;
    public BackgroundNativeSize? NativeSize => _nativeSizeCache.Current;

    private static DependencyProperty Register<T>(string name, T defaultValue) => DependencyProperty.Register(
        name, typeof(T), typeof(ThemeBackground), new PropertyMetadata(defaultValue, OnSettingsChanged));

    private static void OnSettingsChanged(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        if (value is not ThemeBackground control) return;
        if (args.Property == SourcePathProperty || args.Property == Mp4RendererPreferenceProperty)
        {
            control._failedWmpSourcePath = null;
            control._failedLibVlcSourcePath = null;
            control._reportedProblemKey = null;
        }
        control.QueueUpdate();
    }

    internal BackgroundAssetKind ActiveAssetKind => _activeAssetKind;
    internal int ActiveRendererCount => _activeRenderer is null ? 0 : 1;
    internal AnimatedBackground? ActiveImageRenderer => _activeRenderer as AnimatedBackground;
    internal VideoBackgroundPlayer? ActiveVideoRenderer => _activeVideoRenderer as VideoBackgroundPlayer;
    internal bool IsUsingLibVlc => _activeVideoEngine == VideoEngine.LibVlc && _activeVideoRenderer is not null;

    /// <summary>
    /// Registers the existing main-window visual tree as the LibVLC overlay. It
    /// remains a normal sibling of this control for WPF MediaPlayer/GIF/image
    /// rendering and is moved inside VideoView only while LibVLC is active.
    /// </summary>
    public void SetExternalOverlay(UIElement overlay)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        if (ReferenceEquals(_externalOverlay, overlay)) return;
        if (_activeVideoRenderer is not null) DetachOverlayFromVideo(_activeVideoRenderer);
        _externalOverlay = overlay;
        if (VisualTreeHelper.GetParent(overlay) is Panel panel)
        {
            _externalOverlayParent = panel;
            _externalOverlayIndex = panel.Children.IndexOf(overlay);
        }
        QueueUpdate();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => QueueUpdate();

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_pendingUpdate?.Status == DispatcherOperationStatus.Pending) _pendingUpdate.Abort();
        _pendingUpdate = null;
        ReleaseActiveRenderer();
    }

    private void QueueUpdate()
    {
        if (!IsLoaded || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished ||
            _pendingUpdate?.Status == DispatcherOperationStatus.Pending) return;
        _pendingUpdate = Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
        {
            _pendingUpdate = null;
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            UpdateRenderer();
        }));
    }

    private void UpdateRenderer()
    {
        if (!IsLoaded) return;

        var assetKind = BackgroundAssetKinds.Detect(SourcePath);
        var sourcePath = BackgroundSourcePath.NormalizeExisting(SourcePath);
        _nativeSizeCache.ClearWhenSourceChanges(sourcePath);
        var needsVideo = assetKind == BackgroundAssetKind.Video;
        var needsImage = assetKind is BackgroundAssetKind.Image or BackgroundAssetKind.Gif;

        if (!needsVideo && !needsImage)
        {
            ReleaseActiveRenderer();
            return;
        }

        if (needsImage)
        {
            if (_activeRenderer is not AnimatedBackground) ReleaseActiveRenderer();
            if (_activeRenderer is null)
            {
                var image = new AnimatedBackground { IsHitTestVisible = false };
                _activeRenderer = image;
                SubscribeToNativeSize(image);
                RendererHost.Children.Add(image);
            }
            ApplyImageSettings((AnimatedBackground)_activeRenderer);
            _activeAssetKind = assetKind;
            return;
        }

        var selection = SelectVideoEngine(sourcePath);
        if (selection.Engine == VideoEngine.None)
        {
            // Keep the historical WPF host alive after its media pipeline reports
            // a source failure when no optional fallback is installed. This avoids
            // recreating/retrying a broken player on every unrelated setting
            // update, while Automatic still switches to LibVLC immediately once it
            // is available.
            if (_activeVideoEngine == VideoEngine.WindowsMediaPlayer && _activeVideoRenderer is not null &&
                selection.Problem?.Kind == VideoBackendProblemKind.WindowsMediaPlayerUnavailable)
            {
                ApplyVideoSettings(_activeVideoRenderer, reloadSource: false);
                _activeAssetKind = BackgroundAssetKind.Video;
                ReportProblem(selection.Problem);
                return;
            }
            ReleaseActiveRenderer();
            _activeAssetKind = BackgroundAssetKind.Video;
            if (selection.Problem is not null) ReportProblem(selection.Problem);
            return;
        }

        var hasCorrectRenderer = _activeVideoRenderer is not null && _activeVideoEngine == selection.Engine;
        if (!hasCorrectRenderer) ReleaseActiveRenderer();

        if (_activeVideoRenderer is null)
        {
            var video = CreateVideoRenderer(selection, sourcePath);
            if (video is null)
            {
                _activeAssetKind = BackgroundAssetKind.Video;
                return;
            }

            _activeVideoRenderer = video;
            _activeVideoEngine = selection.Engine;
            _activeRenderer = video.View;
            video.View.IsHitTestVisible = false;
            SubscribeToNativeSize(video);
            RendererHost.Children.Add(video.View);
            if (selection.Engine == VideoEngine.LibVlc) AttachOverlayToVideo(video);
        }

        ApplyVideoSettings(_activeVideoRenderer);
        _activeAssetKind = BackgroundAssetKind.Video;
    }

    private VideoSelection SelectVideoEngine(string? sourcePath)
    {
        var preference = Mp4RendererPreferences.Normalize(Mp4RendererPreference);
        var wmpFailedForSource = !string.IsNullOrWhiteSpace(_failedWmpSourcePath) &&
                                  BackgroundSourcePath.Equals(sourcePath, _failedWmpSourcePath);
        var libVlcFailedForSource = !string.IsNullOrWhiteSpace(_failedLibVlcSourcePath) &&
                                     BackgroundSourcePath.Equals(sourcePath, _failedLibVlcSourcePath);
        if (string.Equals(preference, Mp4RendererPreferences.WindowsMediaPlayer, StringComparison.Ordinal))
        {
            if (wmpFailedForSource)
                return VideoSelection.Unavailable(new VideoBackendProblemEventArgs(
                    VideoBackendProblemKind.WindowsMediaPlayerUnavailable, SourcePath, true));
            return new(VideoEngine.WindowsMediaPlayer);
        }

        if (string.Equals(preference, Mp4RendererPreferences.LibVlc, StringComparison.Ordinal))
        {
            if (!IsLibVlcInstalled())
                return VideoSelection.Unavailable(new VideoBackendProblemEventArgs(
                    VideoBackendProblemKind.LibVlcNotInstalled, SourcePath, true));
            if (libVlcFailedForSource)
                return VideoSelection.Unavailable(new VideoBackendProblemEventArgs(
                    VideoBackendProblemKind.LibVlcUnavailable, SourcePath, false));
            return new(VideoEngine.LibVlc);
        }

        if (!wmpFailedForSource)
            return new(VideoEngine.WindowsMediaPlayer);
        if (!IsLibVlcInstalled())
            return VideoSelection.Unavailable(new VideoBackendProblemEventArgs(
                VideoBackendProblemKind.WindowsMediaPlayerUnavailable, SourcePath, true));
        if (libVlcFailedForSource)
            return VideoSelection.Unavailable(new VideoBackendProblemEventArgs(
                VideoBackendProblemKind.LibVlcUnavailable, SourcePath, false));
        return new(VideoEngine.LibVlc);
    }

    private static bool IsLibVlcInstalled() => App.LibVlcPluginLoader?.IsInstalled == true;

    private IVideoBackgroundRenderer? CreateVideoRenderer(VideoSelection selection, string? sourcePath)
    {
        if (selection.Engine == VideoEngine.WindowsMediaPlayer) return new VideoBackgroundPlayer();
        IVideoBackgroundRenderer? renderer = null;
        Exception? error = null;
        if (App.LibVlcPluginLoader is { } loader && loader.TryCreateRenderer(out renderer, out error) && renderer is not null)
            return renderer;

        _failedLibVlcSourcePath = sourcePath;
        ReportProblem(new VideoBackendProblemEventArgs(VideoBackendProblemKind.LibVlcUnavailable, SourcePath, false, error));
        return null;
    }

    private void ApplyVideoSettings(IVideoBackgroundRenderer video, bool reloadSource = true)
    {
        video.ImageStretch = ImageStretch;
        video.ImageOpacity = ImageOpacity;
        video.PlaybackSpeed = VideoPlaybackSpeed;
        // The optional renderer must never emit audio. The default WPF renderer
        // retains its existing per-theme setting and default-false behaviour.
        video.AudioEnabled = _activeVideoEngine == VideoEngine.LibVlc ? false : VideoAudioEnabled;
        video.ImageFlipHorizontal = ImageFlipHorizontal;
        video.ImageFlipVertical = ImageFlipVertical;
        video.PauseWhenWindowMinimized = PauseWhenWindowMinimized;
        video.PauseWhenWindowInactive = PauseWhenWindowInactive;
        video.PauseDuringProfileExecution = PauseDuringProfileExecution;
        video.IsProfileExecutionActive = IsProfileExecutionActive;
        video.PerformanceMode = PerformanceMode;
        video.SourcePath = SourcePath;
        if (reloadSource) video.Refresh();
    }

    private void ApplyImageSettings(AnimatedBackground image)
    {
        image.ImageStretch = ImageStretch;
        image.ImageOpacity = ImageOpacity;
        image.GifAnimationDirection = GifAnimationDirection;
        image.GifAnimationSpeed = GifAnimationSpeed;
        image.ImageFlipHorizontal = ImageFlipHorizontal;
        image.ImageFlipVertical = ImageFlipVertical;
        image.PauseWhenWindowMinimized = PauseWhenWindowMinimized;
        image.PauseWhenWindowInactive = PauseWhenWindowInactive;
        image.PauseDuringProfileExecution = PauseDuringProfileExecution;
        image.IsProfileExecutionActive = IsProfileExecutionActive;
        image.PerformanceMode = PerformanceMode;
        image.GifFrameRateLimit = GifFrameRateLimit;
        image.SourcePath = SourcePath;
    }

    private void ReleaseActiveRenderer()
    {
        var renderer = _activeRenderer;
        var video = _activeVideoRenderer;
        _activeRenderer = null;
        _activeVideoRenderer = null;
        _activeVideoEngine = VideoEngine.None;
        _activeAssetKind = BackgroundAssetKind.None;
        if (renderer is null) return;

        if (video is not null)
        {
            UnsubscribeFromNativeSize(video);
            DetachOverlayFromVideo(video);
            video.SourcePath = string.Empty;
            video.Refresh();
        }
        else if (renderer is AnimatedBackground image)
        {
            UnsubscribeFromNativeSize(image);
            image.SourcePath = string.Empty;
        }

        RendererHost.Children.Remove(renderer);
        if (renderer is IDisposable disposable) disposable.Dispose();
        if (video is not null) RestoreExternalOverlay();
    }

    private void AttachOverlayToVideo(IVideoBackgroundRenderer video)
    {
        if (_externalOverlay is null || _overlayAttachedToVideo) return;
        RememberExternalOverlayParent(_externalOverlay);
        DetachFromParent(_externalOverlay);
        if (video.TryAttachOverlay(_externalOverlay))
        {
            _overlayAttachedToVideo = true;
            video.View.IsHitTestVisible = true;
            return;
        }
        RestoreExternalOverlay();
    }

    private void DetachOverlayFromVideo(IVideoBackgroundRenderer video)
    {
        if (!_overlayAttachedToVideo || _externalOverlay is null) return;
        video.DetachOverlay(_externalOverlay);
        _overlayAttachedToVideo = false;
    }

    private void RememberExternalOverlayParent(UIElement overlay)
    {
        if (VisualTreeHelper.GetParent(overlay) is not Panel parent) return;
        _externalOverlayParent = parent;
        _externalOverlayIndex = parent.Children.IndexOf(overlay);
    }

    private void RestoreExternalOverlay()
    {
        if (_externalOverlay is null || _externalOverlayParent is null) return;
        if (_externalOverlayParent.Children.Contains(_externalOverlay)) return;
        if (HasParent(_externalOverlay)) DetachFromParent(_externalOverlay);
        if (HasParent(_externalOverlay)) return;
        var index = Math.Clamp(_externalOverlayIndex, 0, _externalOverlayParent.Children.Count);
        _externalOverlayParent.Children.Insert(index, _externalOverlay);
    }

    private static void DetachFromParent(UIElement element)
    {
        var parent = VisualTreeHelper.GetParent(element) ?? LogicalTreeHelper.GetParent(element);
        switch (parent)
        {
            case Panel panel:
                panel.Children.Remove(element);
                break;
            case Decorator decorator when ReferenceEquals(decorator.Child, element):
                decorator.Child = null;
                break;
            case ContentControl control when ReferenceEquals(control.Content, element):
                control.Content = null;
                break;
        }
    }

    private static bool HasParent(UIElement element) =>
        VisualTreeHelper.GetParent(element) is not null || LogicalTreeHelper.GetParent(element) is not null;

    private void SubscribeToNativeSize(IVideoBackgroundRenderer video)
    {
        video.NativeSizeAvailable += RendererOnNativeSizeAvailable;
        video.PlaybackFailed += VideoRendererOnPlaybackFailed;
    }

    private void UnsubscribeFromNativeSize(IVideoBackgroundRenderer video)
    {
        video.NativeSizeAvailable -= RendererOnNativeSizeAvailable;
        video.PlaybackFailed -= VideoRendererOnPlaybackFailed;
    }

    private void SubscribeToNativeSize(AnimatedBackground image) => image.NativeSizeAvailable += RendererOnNativeSizeAvailable;

    private void UnsubscribeFromNativeSize(AnimatedBackground image) => image.NativeSizeAvailable -= RendererOnNativeSizeAvailable;

    private void VideoRendererOnPlaybackFailed(object? sender, VideoPlaybackFailedEventArgs e)
    {
        if (sender is not IVideoBackgroundRenderer video || !ReferenceEquals(video, _activeVideoRenderer)) return;
        var sourcePath = BackgroundSourcePath.NormalizeExisting(e.SourcePath) ?? BackgroundSourcePath.NormalizeExisting(SourcePath);
        if (sourcePath is null) return;

        if (_activeVideoEngine == VideoEngine.WindowsMediaPlayer)
            _failedWmpSourcePath = sourcePath;
        else if (_activeVideoEngine == VideoEngine.LibVlc)
            _failedLibVlcSourcePath = sourcePath;

        _ = Dispatcher.BeginInvoke(DispatcherPriority.DataBind, new Action(() =>
        {
            if (!ReferenceEquals(video, _activeVideoRenderer)) return;
            QueueUpdate();
        }));
    }

    private void ReportProblem(VideoBackendProblemEventArgs problem)
    {
        var key = $"{problem.Kind}|{Mp4RendererPreference}|{problem.SourcePath}";
        if (string.Equals(_reportedProblemKey, key, StringComparison.Ordinal)) return;
        _reportedProblemKey = key;
        VideoBackendProblem?.Invoke(this, problem);
    }

    private void RendererOnNativeSizeAvailable(object? sender, BackgroundNativeSizeChangedEventArgs e)
    {
        var currentSource = BackgroundSourcePath.NormalizeExisting(SourcePath);
        if (!BackgroundSourcePath.Equals(currentSource, e.Size.SourcePath) || !_nativeSizeCache.TryUpdate(e.Size)) return;
        NativeSizeChanged?.Invoke(this, e);
    }

    private enum VideoEngine
    {
        None,
        WindowsMediaPlayer,
        LibVlc
    }

    private sealed record VideoSelection(VideoEngine Engine, VideoBackendProblemEventArgs? Problem = null)
    {
        public static VideoSelection Unavailable(VideoBackendProblemEventArgs problem) => new(VideoEngine.None, problem);
    }
}
