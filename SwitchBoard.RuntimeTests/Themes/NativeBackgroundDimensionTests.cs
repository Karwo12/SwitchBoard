using System.Windows.Media.Imaging;
using SwitchBoard.Controls;

namespace SwitchBoard.RuntimeTests.Themes;

[Collection("Windows runtime")]
public sealed class NativeBackgroundDimensionTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public void JpgPngBmpAndGif_LoadAtTheirNative1920x1080PixelDimensions()
    {
        RunOnSta(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), $"SwitchBoard-native-background-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(root);
                var frame = CreateFrame(1920, 1080);
                Save(new JpegBitmapEncoder(), frame, Path.Combine(root, "background.jpg"));
                Save(new PngBitmapEncoder(), frame, Path.Combine(root, "background.png"));
                Save(new BmpBitmapEncoder(), frame, Path.Combine(root, "background.bmp"));
                Save(new GifBitmapEncoder(), frame, Path.Combine(root, "background.gif"));

                foreach (var extension in new[] { ".jpg", ".png", ".bmp", ".gif" })
                {
                    var frames = ThemeImageLoader.Load(Path.Combine(root, $"background{extension}"));
                    var first = Assert.Single(frames);
                    Assert.Equal(1920, first.Source.PixelWidth);
                    Assert.Equal(1080, first.Source.PixelHeight);
                }
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        });
    }

    private static BitmapSource CreateFrame(int width, int height)
    {
        var pixels = new byte[width * height * 4];
        for (var index = 3; index < pixels.Length; index += 4) pixels[index] = byte.MaxValue;
        var frame = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        frame.Freeze();
        return frame;
    }

    private static void Save(BitmapEncoder encoder, BitmapSource frame, string path)
    {
        encoder.Frames.Add(BitmapFrame.Create(frame));
        using var stream = File.Create(path);
        encoder.Save(stream);
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
        if (error is not null) throw new InvalidOperationException("Native background dimension test failed.", error);
    }
}
