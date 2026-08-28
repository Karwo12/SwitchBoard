using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SwitchBoard.Themes;

namespace SwitchBoard.Controls;

/// <summary>
/// Owns exactly one renderer for the current asset. Source changes are coalesced
/// on the dispatcher so a synchronous theme-resource replacement cannot expose
/// a transient empty path and reload unchanged media.
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

    private FrameworkElement? _activeRenderer;
    private DispatcherOperation? _pendingUpdate;
    private BackgroundAssetKind _activeAssetKind;
    private readonly BackgroundNativeSizeCache _nativeSizeCache = new();

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

    public event EventHandler<BackgroundNativeSizeChangedEventArgs>? NativeSizeChanged;
    public BackgroundNativeSize? NativeSize => _nativeSizeCache.Current;

    private static DependencyProperty Register<T>(string name, T defaultValue) => DependencyProperty.Register(
        name, typeof(T), typeof(ThemeBackground), new PropertyMetadata(defaultValue, OnSettingsChanged));

    private static void OnSettingsChanged(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        if (value is ThemeBackground control) control.QueueUpdate();
    }

    internal BackgroundAssetKind ActiveAssetKind => _activeAssetKind;
    internal int ActiveRendererCount => _activeRenderer is null ? 0 : 1;
    internal AnimatedBackground? ActiveImageRenderer => _activeRenderer as AnimatedBackground;
    internal VideoBackgroundPlayer? ActiveVideoRenderer => _activeRenderer as VideoBackgroundPlayer;

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
        var hasCorrectRenderer = (needsVideo && _activeRenderer is VideoBackgroundPlayer) ||
                                 (needsImage && _activeRenderer is AnimatedBackground);

        if (!hasCorrectRenderer) ReleaseActiveRenderer();

        if (!needsVideo && !needsImage)
        {
            _activeAssetKind = BackgroundAssetKind.None;
            return;
        }

        if (_activeRenderer is null)
        {
            _activeRenderer = needsVideo ? new VideoBackgroundPlayer() : new AnimatedBackground();
            SubscribeToNativeSize(_activeRenderer);
            ApplySettings(_activeRenderer);
            RendererHost.Children.Add(_activeRenderer);
        }
        else
        {
            ApplySettings(_activeRenderer);
        }

        _activeAssetKind = assetKind;
    }

    private void ApplySettings(FrameworkElement renderer)
    {
        if (renderer is VideoBackgroundPlayer video)
        {
            video.ImageStretch = ImageStretch;
            video.ImageOpacity = ImageOpacity;
            video.PlaybackSpeed = VideoPlaybackSpeed;
            video.AudioEnabled = VideoAudioEnabled;
            video.ImageFlipHorizontal = ImageFlipHorizontal;
            video.ImageFlipVertical = ImageFlipVertical;
            video.SourcePath = SourcePath;
            return;
        }

        if (renderer is not AnimatedBackground image) return;
        image.ImageStretch = ImageStretch;
        image.ImageOpacity = ImageOpacity;
        image.GifAnimationDirection = GifAnimationDirection;
        image.GifAnimationSpeed = GifAnimationSpeed;
        image.ImageFlipHorizontal = ImageFlipHorizontal;
        image.ImageFlipVertical = ImageFlipVertical;
        image.SourcePath = SourcePath;
    }

    private void ReleaseActiveRenderer()
    {
        var renderer = _activeRenderer;
        _activeRenderer = null;
        _activeAssetKind = BackgroundAssetKind.None;
        if (renderer is null) return;

        UnsubscribeFromNativeSize(renderer);

        // Clear the source while the renderer is still loaded so native handles,
        // timers and frame references are released before another type is created.
        if (renderer is VideoBackgroundPlayer video) video.SourcePath = string.Empty;
        else if (renderer is AnimatedBackground image) image.SourcePath = string.Empty;
        RendererHost.Children.Remove(renderer);
        if (renderer is IDisposable disposable) disposable.Dispose();
    }

    private void SubscribeToNativeSize(FrameworkElement renderer)
    {
        if (renderer is VideoBackgroundPlayer video) video.NativeSizeAvailable += RendererOnNativeSizeAvailable;
        else if (renderer is AnimatedBackground image) image.NativeSizeAvailable += RendererOnNativeSizeAvailable;
    }

    private void UnsubscribeFromNativeSize(FrameworkElement renderer)
    {
        if (renderer is VideoBackgroundPlayer video) video.NativeSizeAvailable -= RendererOnNativeSizeAvailable;
        else if (renderer is AnimatedBackground image) image.NativeSizeAvailable -= RendererOnNativeSizeAvailable;
    }

    private void RendererOnNativeSizeAvailable(object? sender, BackgroundNativeSizeChangedEventArgs e)
    {
        var currentSource = BackgroundSourcePath.NormalizeExisting(SourcePath);
        if (!BackgroundSourcePath.Equals(currentSource, e.Size.SourcePath) || !_nativeSizeCache.TryUpdate(e.Size)) return;
        NativeSizeChanged?.Invoke(this, e);
    }
}
