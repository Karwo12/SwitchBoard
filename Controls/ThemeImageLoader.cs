using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SwitchBoard.Controls;

internal readonly record struct ThemeImageFrame(BitmapSource Source, TimeSpan Delay);

/// <summary>
/// Loads theme images completely into memory so the source file can be replaced or
/// deleted immediately after this method returns.
/// </summary>
internal static class ThemeImageLoader
{
    public static IReadOnlyList<ThemeImageFrame> Load(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".gif" => LoadGif(path),
            ".png" or ".jpg" or ".jpeg" or ".bmp" =>
                [new ThemeImageFrame(LoadStatic(path), TimeSpan.Zero)],
            _ => []
        };

    private static BitmapSource LoadStatic(string path)
    {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) throw new InvalidDataException("The image asset contains no frames.");
            return CopyToMemory(decoder.Frames[0]);
        }
    }

    private static IReadOnlyList<ThemeImageFrame> LoadGif(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0) return [];

        var globalMetadata = decoder.Metadata as BitmapMetadata;
        var width = ReadInt(globalMetadata, "/logscrdesc/Width");
        var height = ReadInt(globalMetadata, "/logscrdesc/Height");
        if (width <= 0) width = ReadInt(decoder.Frames[0].Metadata as BitmapMetadata, "/logscrdesc/Width");
        if (height <= 0) height = ReadInt(decoder.Frames[0].Metadata as BitmapMetadata, "/logscrdesc/Height");
        if (width <= 0) width = decoder.Frames.Max(frame => ReadInt(frame.Metadata as BitmapMetadata, "/imgdesc/Left") + frame.PixelWidth);
        if (height <= 0) height = decoder.Frames.Max(frame => ReadInt(frame.Metadata as BitmapMetadata, "/imgdesc/Top") + frame.PixelHeight);
        width = Math.Max(1, width);
        height = Math.Max(1, height);

        var result = new List<ThemeImageFrame>(decoder.Frames.Count);
        BitmapSource canvas = CreateTransparentBitmap(width, height);
        BitmapSource? previous = null;
        foreach (var decodedFrame in decoder.Frames)
        {
            var metadata = decodedFrame.Metadata as BitmapMetadata;
            var disposal = ReadDisposal(metadata);
            if (disposal == 3) previous = canvas;

            // Copy the decoded frame before composing it. The returned frames then
            // contain no reference to the decoder or its source stream.
            var source = CopyToMemory(decodedFrame);
            var left = ReadInt(metadata, "/imgdesc/Left");
            var top = ReadInt(metadata, "/imgdesc/Top");
            canvas = DrawGifFrame(canvas, source, width, height, left, top);
            result.Add(new ThemeImageFrame(canvas, ReadDelay(metadata)));

            if (disposal == 2)
                canvas = ClearGifRegion(canvas, width, height, left, top, source.PixelWidth, source.PixelHeight);
            else if (disposal == 3 && previous is not null)
                canvas = previous;
        }

        return result;
    }

    private static BitmapSource CopyToMemory(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        converted.Freeze();
        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);
        var copy = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96,
            PixelFormats.Pbgra32, null, pixels, stride);
        copy.Freeze();
        return copy;
    }

    private static BitmapSource DrawGifFrame(BitmapSource canvas, BitmapSource source,
        int width, int height, int left, int top)
    {
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawImage(canvas, new Rect(0, 0, width, height));
            drawing.DrawImage(source, new Rect(left, top, source.PixelWidth, source.PixelHeight));
        }
        var rendered = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(visual);
        rendered.Freeze();
        return rendered;
    }

    private static BitmapSource CreateTransparentBitmap(int width, int height)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null,
            new byte[width * height * 4], width * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource ClearGifRegion(BitmapSource source, int width, int height, int left, int top,
        int regionWidth, int regionHeight)
    {
        var pixels = new byte[width * height * 4];
        source.CopyPixels(pixels, width * 4, 0);
        ClearRect(pixels, width, height, left, top, regionWidth, regionHeight);
        var cleared = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, width * 4);
        cleared.Freeze();
        return cleared;
    }

    private static TimeSpan ReadDelay(BitmapMetadata? metadata)
    {
        try
        {
            if (metadata?.GetQuery("/grctlext/Delay") is ushort delay)
                return TimeSpan.FromMilliseconds(Math.Max(20, delay * 10));
        }
        catch (NotSupportedException) { }
        return TimeSpan.FromMilliseconds(100);
    }

    private static int ReadInt(BitmapMetadata? metadata, string query)
    {
        try { return metadata?.GetQuery(query) is IConvertible value ? Convert.ToInt32(value) : 0; }
        catch (NotSupportedException) { return 0; }
    }

    private static int ReadDisposal(BitmapMetadata? metadata)
    {
        try { return metadata?.GetQuery("/grctlext/Disposal") is IConvertible value ? Convert.ToInt32(value) : 0; }
        catch (NotSupportedException) { return 0; }
    }

    private static void ClearRect(byte[] canvas, int width, int height, int left, int top,
        int rectWidth, int rectHeight)
    {
        for (var y = Math.Max(0, top); y < Math.Min(height, top + rectHeight); y++)
            Array.Clear(canvas, (y * width + Math.Max(0, left)) * 4,
                (Math.Min(width, left + rectWidth) - Math.Max(0, left)) * 4);
    }
}
