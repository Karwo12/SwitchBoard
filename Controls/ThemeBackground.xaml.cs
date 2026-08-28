using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SwitchBoard.Themes;

namespace SwitchBoard.Controls;

/// <summary>
/// Routes one theme asset to either the cached image/GIF renderer or the MP4
/// player. Both renderers have independent lifetimes so a future video backend
/// can replace VideoBackgroundPlayer without changing theme-editor bindings.
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

    public ThemeBackground()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdatePlayers();
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

    private static DependencyProperty Register<T>(string name, T defaultValue) => DependencyProperty.Register(
        name, typeof(T), typeof(ThemeBackground), new PropertyMetadata(defaultValue, OnSettingsChanged));

    private static void OnSettingsChanged(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        if (value is ThemeBackground control) control.UpdatePlayers();
    }

    private void UpdatePlayers()
    {
        if (!IsLoaded) return;

        var isVideo = BackgroundAssetKinds.Detect(SourcePath) == BackgroundAssetKind.Video;
        if (isVideo)
        {
            ImagePlayer.SourcePath = string.Empty;
            ImagePlayer.Visibility = Visibility.Collapsed;
            VideoPlayer.Visibility = Visibility.Visible;
            VideoPlayer.ImageStretch = ImageStretch;
            VideoPlayer.ImageOpacity = ImageOpacity;
            VideoPlayer.PlaybackSpeed = VideoPlaybackSpeed;
            VideoPlayer.AudioEnabled = VideoAudioEnabled;
            VideoPlayer.ImageFlipHorizontal = ImageFlipHorizontal;
            VideoPlayer.ImageFlipVertical = ImageFlipVertical;
            VideoPlayer.SourcePath = SourcePath;
            return;
        }

        VideoPlayer.SourcePath = string.Empty;
        VideoPlayer.Visibility = Visibility.Collapsed;
        ImagePlayer.Visibility = Visibility.Visible;
        ImagePlayer.ImageStretch = ImageStretch;
        ImagePlayer.ImageOpacity = ImageOpacity;
        ImagePlayer.GifAnimationDirection = GifAnimationDirection;
        ImagePlayer.GifAnimationSpeed = GifAnimationSpeed;
        ImagePlayer.ImageFlipHorizontal = ImageFlipHorizontal;
        ImagePlayer.ImageFlipVertical = ImageFlipVertical;
        ImagePlayer.SourcePath = SourcePath;
    }
}
