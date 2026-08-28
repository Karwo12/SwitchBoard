using SwitchBoard.Themes;

namespace SwitchBoard.Controls;

/// <summary>
/// Selects the next already-decoded GIF frame without allocating or copying frames.
/// </summary>
internal sealed class GifFrameSequencer
{
    private readonly int _frameCount;
    private readonly string _direction;
    private int _step;

    public GifFrameSequencer(int frameCount, string? direction)
    {
        _frameCount = Math.Max(0, frameCount);
        _direction = GifAnimationDirections.Normalize(direction);
        CurrentIndex = _direction == GifAnimationDirections.Reverse && _frameCount > 0
            ? _frameCount - 1
            : 0;
        _step = _direction == GifAnimationDirections.Reverse ? -1 : 1;
    }

    public int CurrentIndex { get; private set; }

    public int MoveNext()
    {
        if (_frameCount <= 1) return CurrentIndex = 0;

        if (_direction == GifAnimationDirections.Normal)
            return CurrentIndex = (CurrentIndex + 1) % _frameCount;

        if (_direction == GifAnimationDirections.Reverse)
            return CurrentIndex = (CurrentIndex - 1 + _frameCount) % _frameCount;

        var next = CurrentIndex + _step;
        if (next >= _frameCount)
        {
            _step = -1;
            next = _frameCount - 2;
        }
        else if (next < 0)
        {
            _step = 1;
            next = 1;
        }
        return CurrentIndex = next;
    }

    public TimeSpan GetCurrentDelay(IReadOnlyList<TimeSpan> delays, double speed)
    {
        if (delays.Count == 0) return TimeSpan.FromMilliseconds(100);
        var delay = delays[Math.Clamp(CurrentIndex, 0, delays.Count - 1)];
        return GifAnimationTiming.ScaleDelay(delay, speed);
    }
}

internal static class GifAnimationTiming
{
    public static TimeSpan ScaleDelay(TimeSpan delay, double speed)
    {
        var normalizedSpeed = GifAnimationSpeeds.Normalize(speed);
        var milliseconds = Math.Max(1d, Math.Round(delay.TotalMilliseconds / normalizedSpeed));
        return TimeSpan.FromMilliseconds(milliseconds);
    }
}
