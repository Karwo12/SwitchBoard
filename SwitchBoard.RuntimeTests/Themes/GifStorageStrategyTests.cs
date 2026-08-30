using SwitchBoard.Controls;
using SwitchBoard.RuntimeTests.TestInfrastructure;

namespace SwitchBoard.RuntimeTests.Themes;

[Collection("Windows runtime")]
public sealed class GifStorageStrategyTests
{
    [Theory]
    [InlineData(540, 304, 58, false)]
    [InlineData(1920, 1080, 24, false)]
    [InlineData(1920, 1080, 651, true)]
    [InlineData(7680, 4320, 2, true)]
    [Trait("Category", "Unit")]
    public void StorageSelection_BuffersSmallAnimationsAndStreamsLargeOnes(
        int width, int height, int frameCount, bool expectedStreaming)
    {
        Assert.Equal(expectedStreaming,
            ThemeImageLoader.ShouldUseStreamingStorage(width, height, frameCount));
    }

    [Fact]
    [Trait("Category", "Unit")]
    public void StorageSelection_InvalidDimensionsRemainSafe()
    {
        Assert.False(ThemeImageLoader.ShouldUseStreamingStorage(0, 1080, 100));
        Assert.False(ThemeImageLoader.ShouldUseStreamingStorage(1920, -1, 100));
        Assert.False(ThemeImageLoader.ShouldUseStreamingStorage(1920, 1080, 0));
        Assert.True(ThemeImageLoader.ShouldUseStreamingStorage(int.MaxValue, int.MaxValue, int.MaxValue));
    }

    [Fact]
    [Trait("Category", "Regression")]
    public void StreamingSequence_PreservesFramesAndDoesNotLockTheThemeAsset()
    {
        RunOnSta(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"SwitchBoard-streaming-gif-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            try
            {
                TestHelpers.CreateTestImages(root);
                var path = Path.Combine(root, "test.gif");
                using var buffered = ThemeImageLoader.Load(path);
                using var streaming = ThemeImageLoader.Load(path, forceStreaming: true);

                Assert.False(buffered.UsesStreamingStorage);
                Assert.True(streaming.UsesStreamingStorage);
                Assert.Equal(buffered.Count, streaming.Count);
                File.Delete(path);
                for (var index = 0; index < buffered.Count; index++)
                {
                    Assert.Equal(ReadPixels(buffered[index].Source), ReadPixels(streaming[index].Source));
                    Assert.Equal(buffered[index].Delay, streaming[index].Delay);
                }
                Assert.True(streaming.AreMaterializedFramesFrozen);
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        });
    }

    private static byte[] ReadPixels(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        var pixels = new byte[converted.PixelWidth * converted.PixelHeight * 4];
        converted.CopyPixels(pixels, converted.PixelWidth * 4, 0);
        return pixels;
    }

    private static void RunOnSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { error = exception; }
            finally
            {
                var dispatcher = System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread);
                if (dispatcher is { HasShutdownStarted: false }) dispatcher.InvokeShutdown();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw new InvalidOperationException("Streaming GIF test failed.", error);
    }
}
