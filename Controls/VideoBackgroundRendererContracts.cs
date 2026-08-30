using System.Windows;
using System.Windows.Media;

namespace SwitchBoard.Controls;

/// <summary>
/// Contract implemented by MP4 renderers.  It intentionally contains only the
/// lifecycle and presentation state already owned by ThemeBackground, allowing
/// optional renderers to live in a separately loaded assembly.
/// </summary>
public interface IVideoBackgroundRenderer : IDisposable
{
    FrameworkElement View { get; }

    string SourcePath { get; set; }
    Stretch ImageStretch { get; set; }
    double ImageOpacity { get; set; }
    double PlaybackSpeed { get; set; }
    bool AudioEnabled { get; set; }
    bool ImageFlipHorizontal { get; set; }
    bool ImageFlipVertical { get; set; }
    bool PauseWhenWindowMinimized { get; set; }
    bool PauseWhenWindowInactive { get; set; }
    bool PauseDuringProfileExecution { get; set; }
    bool IsProfileExecutionActive { get; set; }
    string PerformanceMode { get; set; }

    event EventHandler<BackgroundNativeSizeChangedEventArgs>? NativeSizeAvailable;
    event EventHandler<VideoPlaybackFailedEventArgs>? PlaybackFailed;

    /// <summary>
    /// Applies the current common settings without creating another renderer.
    /// Implementations may reopen their current source only when that source has
    /// actually changed.
    /// </summary>
    void Refresh();

    /// <summary>
    /// LibVLCSharp's WPF host needs visual content placed inside its own overlay
    /// window because native child HWNDs have WPF airspace limitations. WMP simply
    /// returns false and ThemeBackground leaves the normal overlay in place.
    /// </summary>
    bool TryAttachOverlay(UIElement overlay);
    void DetachOverlay(UIElement overlay);
}

/// <summary>Entry point exposed by an optional MP4 renderer plugin.</summary>
public interface IVideoBackgroundRendererFactory
{
    string EngineId { get; }
    IVideoBackgroundRenderer Create(string componentDirectory);
}

public sealed class VideoPlaybackFailedEventArgs(string sourcePath, Exception? exception = null) : EventArgs
{
    public string SourcePath { get; } = sourcePath;
    public Exception? Exception { get; } = exception;
}

public enum VideoBackendProblemKind
{
    WindowsMediaPlayerUnavailable,
    LibVlcNotInstalled,
    LibVlcUnavailable
}

public sealed class VideoBackendProblemEventArgs(VideoBackendProblemKind kind, string sourcePath,
    bool canInstallLibVlc, Exception? exception = null) : EventArgs
{
    public VideoBackendProblemKind Kind { get; } = kind;
    public string SourcePath { get; } = sourcePath;
    public bool CanInstallLibVlc { get; } = canInstallLibVlc;
    public Exception? Exception { get; } = exception;
}
