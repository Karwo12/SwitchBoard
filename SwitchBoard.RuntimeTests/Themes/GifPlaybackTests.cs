using SwitchBoard.Controls;
using SwitchBoard.Themes;

namespace SwitchBoard.RuntimeTests.Themes;

public sealed class GifPlaybackTests
{
    [Fact]
    [Trait("Category", "Unit")]
    public void GifFrameSequencer_UsesNormalReverseAndPingPongOrdersWithoutDuplicatingEndpoints()
    {
        Assert.Equal([0, 1, 2, 0, 1], ReadFrames(3, GifAnimationDirections.Normal, 5));
        Assert.Equal([2, 1, 0, 2, 1], ReadFrames(3, GifAnimationDirections.Reverse, 5));
        Assert.Equal([0, 1, 2, 1, 0, 1, 2, 1], ReadFrames(3, GifAnimationDirections.PingPong, 8));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GifFrameSequencer_PreservesEachFrameDelayAndScalesItForSpeed()
    {
        var delays = new[]
        {
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(220),
            TimeSpan.FromMilliseconds(370)
        };
        var reverse = new GifFrameSequencer(3, GifAnimationDirections.Reverse);

        Assert.Equal(TimeSpan.FromMilliseconds(370), reverse.GetCurrentDelay(delays, 1));
        reverse.MoveNext();
        Assert.Equal(TimeSpan.FromMilliseconds(220), reverse.GetCurrentDelay(delays, 1));
        reverse.MoveNext();
        Assert.Equal(TimeSpan.FromMilliseconds(100), reverse.GetCurrentDelay(delays, 1));
        Assert.Equal(TimeSpan.FromMilliseconds(185), GifAnimationTiming.ScaleDelay(delays[2], 2));
        Assert.Equal(TimeSpan.FromMilliseconds(740), GifAnimationTiming.ScaleDelay(delays[2], 0.5));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void GifFrameSequencer_StaticImageAlwaysUsesItsOnlyFrame()
    {
        Assert.Equal([0, 0, 0], ReadFrames(1, GifAnimationDirections.Reverse, 3));
        Assert.Equal([0, 0, 0], ReadFrames(1, GifAnimationDirections.PingPong, 3));
    }

    private static int[] ReadFrames(int frameCount, string direction, int count)
    {
        var sequence = new GifFrameSequencer(frameCount, direction);
        var frames = new int[count];
        for (var index = 0; index < count; index++)
        {
            frames[index] = sequence.CurrentIndex;
            sequence.MoveNext();
        }
        return frames;
    }
}
