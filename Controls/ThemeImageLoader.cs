using System.Buffers;
using System.Collections;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SwitchBoard.Controls;

internal readonly record struct ThemeImageFrame(BitmapSource Source, TimeSpan Delay);

internal abstract class ThemeImageSequence : IReadOnlyList<ThemeImageFrame>, IDisposable
{
    public abstract int Count { get; }
    public abstract int PixelWidth { get; }
    public abstract int PixelHeight { get; }
    public abstract ThemeImageFrame this[int index] { get; }
    internal abstract bool UsesStreamingStorage { get; }
    internal abstract bool AreMaterializedFramesFrozen { get; }

    public IEnumerator<ThemeImageFrame> GetEnumerator()
    {
        for (var index = 0; index < Count; index++) yield return this[index];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    public abstract void Dispose();
}

/// <summary>
/// Detaches theme assets from their source files. Small GIFs stay fully buffered for
/// the smoothest playback; GIFs whose expanded frames would exceed the memory budget
/// are composed lazily with bounded checkpoints instead of allocating every full frame.
/// </summary>
internal static class ThemeImageLoader
{
    internal const long MaxBufferedGifBytes = 192L * 1024 * 1024;
    private const long MaxCheckpointBytes = 64L * 1024 * 1024;
    private const int FrameCacheCapacity = 4;

    public static ThemeImageSequence Load(string path, bool forceStreaming = false) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".gif" => LoadGif(path, forceStreaming),
            ".png" or ".jpg" or ".jpeg" or ".bmp" =>
                new BufferedImageSequence([new ThemeImageFrame(LoadStatic(path), TimeSpan.Zero)]),
            _ => new BufferedImageSequence([])
        };

    internal static bool ShouldUseStreamingStorage(int width, int height, int frameCount)
    {
        if (width <= 0 || height <= 0 || frameCount <= 0) return false;
        try { return checked((long)width * height * 4 * frameCount) > MaxBufferedGifBytes; }
        catch (OverflowException) { return true; }
    }

    private static BitmapSource LoadStatic(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0) throw new InvalidDataException("The image asset contains no frames.");
        return CopyToMemory(decoder.Frames[0]);
    }

    private static ThemeImageSequence LoadGif(string path, bool forceStreaming)
    {
        using (var fileStream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var decoder = new GifBitmapDecoder(fileStream, BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.None);
            if (decoder.Frames.Count == 0) return new BufferedImageSequence([]);
            BackgroundMediaDiagnostics.GifDecoded();

            var (width, height) = ReadCanvasSize(decoder);
            if (!forceStreaming && !ShouldUseStreamingStorage(width, height, decoder.Frames.Count))
                return new BufferedImageSequence(ComposeAllFrames(decoder, width, height));
        }

        // A streaming sequence must not keep the theme asset locked. Retain only its
        // compressed bytes, then let WIC decode individual frames from that detached copy.
        var compressed = File.ReadAllBytes(path);
        var stream = new MemoryStream(compressed, writable: false);
        try
        {
            var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.None);
            if (decoder.Frames.Count == 0)
            {
                stream.Dispose();
                return new BufferedImageSequence([]);
            }

            var (width, height) = ReadCanvasSize(decoder);
            var metadata = decoder.Frames.Select(ReadFrameMetadata).ToArray();
            return new StreamingGifSequence(stream, decoder, width, height, metadata);
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static List<ThemeImageFrame> ComposeAllFrames(GifBitmapDecoder decoder, int width, int height)
    {
        var result = new List<ThemeImageFrame>(decoder.Frames.Count);
        BitmapSource canvas = CreateTransparentBitmap(width, height);
        for (var index = 0; index < decoder.Frames.Count; index++)
        {
            var frameMetadata = ReadFrameMetadata(decoder.Frames[index]);
            var before = canvas;
            var source = CopyToMemory(decoder.Frames[index]);
            var displayed = DrawGifFrame(before, source, width, height, frameMetadata.Left, frameMetadata.Top);
            result.Add(new ThemeImageFrame(displayed, frameMetadata.Delay));
            canvas = GetPostDisposalCanvas(before, displayed, source, width, height, frameMetadata);
        }
        return result;
    }

    private static (int Width, int Height) ReadCanvasSize(GifBitmapDecoder decoder)
    {
        var globalMetadata = decoder.Metadata as BitmapMetadata;
        var width = ReadInt(globalMetadata, "/logscrdesc/Width");
        var height = ReadInt(globalMetadata, "/logscrdesc/Height");
        if (width <= 0) width = ReadInt(decoder.Frames[0].Metadata as BitmapMetadata, "/logscrdesc/Width");
        if (height <= 0) height = ReadInt(decoder.Frames[0].Metadata as BitmapMetadata, "/logscrdesc/Height");
        if (width <= 0) width = decoder.Frames.Max(frame =>
            ReadInt(frame.Metadata as BitmapMetadata, "/imgdesc/Left") + frame.PixelWidth);
        if (height <= 0) height = decoder.Frames.Max(frame =>
            ReadInt(frame.Metadata as BitmapMetadata, "/imgdesc/Top") + frame.PixelHeight);
        return (Math.Max(1, width), Math.Max(1, height));
    }

    private static GifFrameMetadata ReadFrameMetadata(BitmapFrame frame)
    {
        var metadata = frame.Metadata as BitmapMetadata;
        return new GifFrameMetadata(
            ReadInt(metadata, "/imgdesc/Left"),
            ReadInt(metadata, "/imgdesc/Top"),
            ReadDisposal(metadata),
            ReadDelay(metadata));
    }

    private static BitmapSource GetPostDisposalCanvas(BitmapSource before, BitmapSource displayed,
        BitmapSource source, int width, int height, GifFrameMetadata metadata) => metadata.Disposal switch
        {
            2 => ClearGifRegion(displayed, width, height, metadata.Left, metadata.Top,
                source.PixelWidth, source.PixelHeight),
            3 => before,
            _ => displayed
        };

    private static BitmapSource CopyToMemory(BitmapSource source)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Pbgra32, null, 0);
        converted.Freeze();
        var stride = converted.PixelWidth * 4;
        var length = stride * converted.PixelHeight;
        var pixels = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            converted.CopyPixels(pixels, stride, 0);
            var copy = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96,
                PixelFormats.Pbgra32, null, pixels, stride);
            copy.Freeze();
            return copy;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixels);
        }
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
        var stride = checked(width * 4);
        var length = checked(stride * height);
        var pixels = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            Array.Clear(pixels, 0, length);
            var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixels);
        }
    }

    private static BitmapSource ClearGifRegion(BitmapSource source, int width, int height, int left, int top,
        int regionWidth, int regionHeight)
    {
        var stride = checked(width * 4);
        var length = checked(stride * height);
        var pixels = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            source.CopyPixels(pixels, stride, 0);
            ClearRect(pixels, width, height, left, top, regionWidth, regionHeight);
            var cleared = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
            cleared.Freeze();
            return cleared;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(pixels);
        }
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
        var startX = Math.Max(0, left);
        var endX = Math.Min(width, left + rectWidth);
        if (endX <= startX) return;
        for (var y = Math.Max(0, top); y < Math.Min(height, top + rectHeight); y++)
            Array.Clear(canvas, (y * width + startX) * 4, (endX - startX) * 4);
    }

    private sealed class BufferedImageSequence(List<ThemeImageFrame> frames) : ThemeImageSequence
    {
        private readonly List<ThemeImageFrame> _frames = frames;
        public override int Count => _frames.Count;
        public override int PixelWidth => _frames.Count == 0 ? 0 : _frames[0].Source.PixelWidth;
        public override int PixelHeight => _frames.Count == 0 ? 0 : _frames[0].Source.PixelHeight;
        public override ThemeImageFrame this[int index] => _frames[index];
        internal override bool UsesStreamingStorage => false;
        internal override bool AreMaterializedFramesFrozen => _frames.All(frame => frame.Source.IsFrozen);
        public override void Dispose() => _frames.Clear();
    }

    private sealed class StreamingGifSequence : ThemeImageSequence
    {
        private readonly MemoryStream _stream;
        private readonly GifBitmapDecoder _decoder;
        private readonly GifFrameMetadata[] _metadata;
        private readonly SortedDictionary<int, BitmapSource> _checkpoints = [];
        private readonly Dictionary<int, CachedGifFrame> _cache = [];
        private readonly LinkedList<int> _cacheOrder = [];
        private readonly int _checkpointInterval;
        private BitmapSource? _lastPostDisposalCanvas;
        private int _lastIndex = -1;
        private bool _disposed;

        public StreamingGifSequence(MemoryStream stream, GifBitmapDecoder decoder, int width, int height,
            GifFrameMetadata[] metadata)
        {
            _stream = stream;
            _decoder = decoder;
            _metadata = metadata;
            PixelWidth = width;
            PixelHeight = height;
            var frameBytes = Math.Max(1L, (long)width * height * 4);
            _checkpointInterval = Math.Max(1,
                (int)Math.Ceiling(metadata.Length * frameBytes / (double)MaxCheckpointBytes));
        }

        public override int Count => _metadata.Length;
        public override int PixelWidth { get; }
        public override int PixelHeight { get; }
        internal override bool UsesStreamingStorage => true;
        internal override bool AreMaterializedFramesFrozen =>
            _cache.Values.All(frame => frame.Displayed.IsFrozen && frame.PostDisposal.IsFrozen) &&
            _checkpoints.Values.All(frame => frame.IsFrozen);

        public override ThemeImageFrame this[int index]
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if ((uint)index >= (uint)Count) throw new ArgumentOutOfRangeException(nameof(index));
                return new ThemeImageFrame(GetFrame(index), _metadata[index].Delay);
            }
        }

        private BitmapSource GetFrame(int index)
        {
            if (_cache.TryGetValue(index, out var cached))
            {
                TouchCache(index);
                _lastIndex = index;
                _lastPostDisposalCanvas = cached.PostDisposal;
                return cached.Displayed;
            }

            BitmapSource canvas;
            int startIndex;
            if (_lastIndex + 1 == index && _lastPostDisposalCanvas is not null)
            {
                canvas = _lastPostDisposalCanvas;
                startIndex = index;
            }
            else
            {
                var checkpoint = _checkpoints.LastOrDefault(item => item.Key < index);
                if (checkpoint.Value is not null)
                {
                    canvas = checkpoint.Value;
                    startIndex = checkpoint.Key + 1;
                }
                else
                {
                    canvas = CreateTransparentBitmap(PixelWidth, PixelHeight);
                    startIndex = 0;
                }
            }

            for (var currentIndex = startIndex; currentIndex <= index; currentIndex++)
            {
                var frameMetadata = _metadata[currentIndex];
                var before = canvas;
                var source = CopyToMemory(_decoder.Frames[currentIndex]);
                var displayed = DrawGifFrame(before, source, PixelWidth, PixelHeight,
                    frameMetadata.Left, frameMetadata.Top);
                var postDisposal = GetPostDisposalCanvas(before, displayed, source,
                    PixelWidth, PixelHeight, frameMetadata);
                if ((currentIndex + 1) % _checkpointInterval == 0 && currentIndex < Count - 1)
                    _checkpoints[currentIndex] = postDisposal;
                AddToCache(currentIndex, new CachedGifFrame(displayed, postDisposal));
                canvas = postDisposal;
            }

            var result = _cache[index];
            _lastIndex = index;
            _lastPostDisposalCanvas = result.PostDisposal;
            return result.Displayed;
        }

        private void AddToCache(int index, CachedGifFrame frame)
        {
            if (_cache.ContainsKey(index))
            {
                _cache[index] = frame;
                TouchCache(index);
                return;
            }
            _cache[index] = frame;
            _cacheOrder.AddLast(index);
            while (_cacheOrder.Count > FrameCacheCapacity)
            {
                var oldest = _cacheOrder.First!.Value;
                _cacheOrder.RemoveFirst();
                _cache.Remove(oldest);
            }
        }

        private void TouchCache(int index)
        {
            _cacheOrder.Remove(index);
            _cacheOrder.AddLast(index);
        }

        public override void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _cache.Clear();
            _cacheOrder.Clear();
            _checkpoints.Clear();
            _lastPostDisposalCanvas = null;
            _stream.Dispose();
        }
    }

    private readonly record struct GifFrameMetadata(int Left, int Top, int Disposal, TimeSpan Delay);
    private readonly record struct CachedGifFrame(BitmapSource Displayed, BitmapSource PostDisposal);
}
