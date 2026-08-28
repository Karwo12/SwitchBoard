using System.Threading;

namespace SwitchBoard.Controls;

/// <summary>
/// Lightweight counters used by regression tests and performance diagnostics.
/// They deliberately count native media lifetimes separately from control instances.
/// </summary>
internal static class BackgroundMediaDiagnostics
{
    private static int _activeRenderers;
    private static int _activeGifTimers;
    private static int _activeMediaPlayers;
    private static int _gifDecodeCount;
    private static int _videoOpenCount;

    public static BackgroundMediaSnapshot Snapshot => new(
        Volatile.Read(ref _activeRenderers),
        Volatile.Read(ref _activeGifTimers),
        Volatile.Read(ref _activeMediaPlayers),
        Volatile.Read(ref _gifDecodeCount),
        Volatile.Read(ref _videoOpenCount));

    public static void RendererCreated() => Interlocked.Increment(ref _activeRenderers);
    public static void RendererReleased() => Interlocked.Decrement(ref _activeRenderers);
    public static void GifTimerStarted() => Interlocked.Increment(ref _activeGifTimers);
    public static void GifTimerStopped() => Interlocked.Decrement(ref _activeGifTimers);
    public static void MediaPlayerCreated() => Interlocked.Increment(ref _activeMediaPlayers);
    public static void MediaPlayerReleased() => Interlocked.Decrement(ref _activeMediaPlayers);
    public static void GifDecoded() => Interlocked.Increment(ref _gifDecodeCount);
    public static void VideoOpened() => Interlocked.Increment(ref _videoOpenCount);
}

internal readonly record struct BackgroundMediaSnapshot(
    int ActiveRenderers,
    int ActiveGifTimers,
    int ActiveMediaPlayers,
    int GifDecodeCount,
    int VideoOpenCount);
