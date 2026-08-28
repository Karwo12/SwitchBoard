using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SwitchBoard.Themes;

namespace SwitchBoard.Controls;

/// <summary>
/// Renders an MP4 through WPF's native MediaPlayer. The player is closed whenever
/// its source changes or the control unloads, so it does not retain media handles.
/// </summary>
public partial class VideoBackgroundPlayer : UserControl
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
    private VideoDrawing? _drawing;
    private Window? _window;

    public VideoBackgroundPlayer()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => UpdatePlaybackState();
    }

    public string SourcePath { get => (string)GetValue(SourcePathProperty); set => SetValue(SourcePathProperty, value); }
    public Stretch ImageStretch { get => (Stretch)GetValue(ImageStretchProperty); set => SetValue(ImageStretchProperty, value); }
    public double ImageOpacity { get => (double)GetValue(ImageOpacityProperty); set => SetValue(ImageOpacityProperty, value); }
    public double PlaybackSpeed { get => (double)GetValue(PlaybackSpeedProperty); set => SetValue(PlaybackSpeedProperty, value); }
    public bool AudioEnabled { get => (bool)GetValue(AudioEnabledProperty); set => SetValue(AudioEnabledProperty, value); }
    public bool ImageFlipHorizontal { get => (bool)GetValue(ImageFlipHorizontalProperty); set => SetValue(ImageFlipHorizontalProperty, value); }
    public bool ImageFlipVertical { get => (bool)GetValue(ImageFlipVerticalProperty); set => SetValue(ImageFlipVerticalProperty, value); }

    private static void OnSourceChanged(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        if (value is VideoBackgroundPlayer control) control.Reload();
    }

    private static void OnVisualChanged(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        if (value is VideoBackgroundPlayer control) control.ApplyVisualSettings();
    }

    private static void OnPlaybackChanged(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        if (value is not VideoBackgroundPlayer control) return;
        control.ApplyPlaybackSettings();
        control.UpdatePlaybackState();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window is not null) _window.StateChanged += WindowOnStateChanged;
        Reload();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_window is not null) _window.StateChanged -= WindowOnStateChanged;
        _window = null;
        ReleasePlayer();
    }

    private void WindowOnStateChanged(object? sender, EventArgs e) => UpdatePlaybackState();

    private void Reload()
    {
        ReleasePlayer();
        ApplyVisualSettings();
        if (!IsLoaded || string.IsNullOrWhiteSpace(SourcePath) || !File.Exists(SourcePath)) return;

        var player = new MediaPlayer();
        player.MediaOpened += PlayerOnMediaOpened;
        player.MediaEnded += PlayerOnMediaEnded;
        player.MediaFailed += PlayerOnMediaFailed;
        _player = player;
        _drawing = new VideoDrawing { Player = player, Rect = new Rect(0, 0, 1, 1) };
        ImageElement.Source = new DrawingImage(_drawing);
        ApplyPlaybackSettings();
        try { player.Open(new Uri(Path.GetFullPath(SourcePath), UriKind.Absolute)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            App.Logger?.Error("ThemeBackground", exception,
                $"MP4 background '{Path.GetFileName(SourcePath)}' could not be opened.");
            ReleasePlayer();
        }
    }

    private void PlayerOnMediaOpened(object? sender, EventArgs e)
    {
        var player = _player;
        if (!ReferenceEquals(sender, player) || player is null || _drawing is null) return;
        var width = Math.Max(1, player.NaturalVideoWidth);
        var height = Math.Max(1, player.NaturalVideoHeight);
        _drawing.Rect = new Rect(0, 0, width, height);
        ApplyPlaybackSettings();
        UpdatePlaybackState();
    }

    private void PlayerOnMediaEnded(object? sender, EventArgs e)
    {
        var player = _player;
        if (!ReferenceEquals(sender, player) || player is null) return;
        try
        {
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
        ImageElement.RenderTransform = new ScaleTransform(ImageFlipHorizontal ? -1 : 1, ImageFlipVertical ? -1 : 1);
    }

    private void ApplyPlaybackSettings()
    {
        if (_player is null) return;
        _player.IsMuted = !AudioEnabled;
        _player.SpeedRatio = GifAnimationSpeeds.Normalize(PlaybackSpeed);
    }

    private void UpdatePlaybackState()
    {
        if (_player is null) return;
        var shouldPlay = IsLoaded && IsVisible && (_window is null || _window.WindowState != WindowState.Minimized);
        try
        {
            if (shouldPlay) _player.Play();
            else _player.Pause();
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
        ImageElement.Source = null;
        if (player is null) return;
        player.MediaOpened -= PlayerOnMediaOpened;
        player.MediaEnded -= PlayerOnMediaEnded;
        player.MediaFailed -= PlayerOnMediaFailed;
        try { player.Stop(); }
        catch (InvalidOperationException) { }
        try { player.Close(); }
        catch (InvalidOperationException) { }
    }
}
