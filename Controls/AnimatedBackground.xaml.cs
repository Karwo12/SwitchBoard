using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Windows.Media;

namespace SwitchBoard.Controls;

public partial class AnimatedBackground : UserControl
{
    public static readonly DependencyProperty SourcePathProperty = DependencyProperty.Register(
        nameof(SourcePath), typeof(string), typeof(AnimatedBackground),
        new PropertyMetadata(string.Empty, OnImagePropertyChanged));
    public static readonly DependencyProperty ImageStretchProperty = DependencyProperty.Register(
        nameof(ImageStretch), typeof(Stretch), typeof(AnimatedBackground),
        new PropertyMetadata(Stretch.UniformToFill, OnImagePropertyChanged));
    public static readonly DependencyProperty ImageOpacityProperty = DependencyProperty.Register(
        nameof(ImageOpacity), typeof(double), typeof(AnimatedBackground),
        new PropertyMetadata(0d, OnImagePropertyChanged));

    private readonly DispatcherTimer _timer;
    private readonly List<BitmapSource> _frames = [];
    private readonly List<TimeSpan> _delays = [];
    private int _frameIndex;
    private Window? _window;

    public AnimatedBackground()
    {
        InitializeComponent();
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(100) };
        _timer.Tick += TimerOnTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => UpdateAnimationState();
    }

    public string SourcePath { get => (string)GetValue(SourcePathProperty); set => SetValue(SourcePathProperty, value); }
    public Stretch ImageStretch { get => (Stretch)GetValue(ImageStretchProperty); set => SetValue(ImageStretchProperty, value); }
    public double ImageOpacity { get => (double)GetValue(ImageOpacityProperty); set => SetValue(ImageOpacityProperty, value); }

    private static void OnImagePropertyChanged(DependencyObject value, DependencyPropertyChangedEventArgs args)
    {
        if (value is not AnimatedBackground control) return;
        if (args.Property == SourcePathProperty) control.Reload();
        else control.ApplyVisualSettings();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _window = Window.GetWindow(this);
        if (_window is not null) _window.StateChanged += WindowOnStateChanged;
        Reload();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        if (_window is not null) _window.StateChanged -= WindowOnStateChanged;
        _window = null;
        ClearFrames();
    }

    private void WindowOnStateChanged(object? sender, EventArgs e) => UpdateAnimationState();

    private void Reload()
    {
        if (!IsLoaded) return;
        _timer.Stop();
        ClearFrames();
        ApplyVisualSettings();
        if (string.IsNullOrWhiteSpace(SourcePath) || !File.Exists(SourcePath)) return;
        try
        {
            var extension = Path.GetExtension(SourcePath);
            if (string.Equals(extension, ".gif", StringComparison.OrdinalIgnoreCase))
                LoadGif(SourcePath);
            else if (extension is ".png" or ".jpg" or ".jpeg" or ".bmp")
                LoadStatic(SourcePath);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            ClearFrames();
        }
        UpdateAnimationState();
    }

    private void LoadStatic(string path)
    {
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        image.UriSource = new Uri(path, UriKind.Absolute);
        image.EndInit();
        image.Freeze();
        _frames.Add(image);
        _delays.Add(TimeSpan.Zero);
        ImageElement.Source = image;
    }

    private void LoadGif(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var decoder = new GifBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        if (decoder.Frames.Count == 0) return;
        var globalMetadata = decoder.Metadata as BitmapMetadata;
        var width = ReadInt(globalMetadata, "/logscrdesc/Width");
        var height = ReadInt(globalMetadata, "/logscrdesc/Height");
        if (width <= 0) width = ReadInt(decoder.Frames[0].Metadata as BitmapMetadata, "/logscrdesc/Width");
        if (height <= 0) height = ReadInt(decoder.Frames[0].Metadata as BitmapMetadata, "/logscrdesc/Height");
        if (width <= 0) width = decoder.Frames.Max(frame => ReadInt(frame.Metadata as BitmapMetadata, "/imgdesc/Left") + frame.PixelWidth);
        if (height <= 0) height = decoder.Frames.Max(frame => ReadInt(frame.Metadata as BitmapMetadata, "/imgdesc/Top") + frame.PixelHeight);
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        BitmapSource canvas = CreateTransparentBitmap(width, height);
        BitmapSource? previous = null;
        foreach (var source in decoder.Frames)
        {
            var disposal = ReadDisposal(source.Metadata as BitmapMetadata);
            if (disposal == 3) previous = canvas;
            var left = ReadInt(source.Metadata as BitmapMetadata, "/imgdesc/Left");
            var top = ReadInt(source.Metadata as BitmapMetadata, "/imgdesc/Top");
            canvas = DrawGifFrame(canvas, source, width, height, left, top);
            _frames.Add(canvas);
            _delays.Add(ReadDelay(source.Metadata as BitmapMetadata));
            if (disposal == 2)
                canvas = ClearGifRegion(canvas, width, height, left, top, source.PixelWidth, source.PixelHeight);
            else if (disposal == 3 && previous is not null)
                canvas = previous;
        }
        if (_frames.Count > 0) ImageElement.Source = _frames[0];
    }

    private static BitmapSource DrawGifFrame(BitmapSource canvas, BitmapSource source, int width, int height, int left, int top)
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

    private static BitmapSource CreateTransparentBitmap(int width, int height) =>
        BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null,
            new byte[width * height * 4], width * 4);

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

    private void ApplyVisualSettings()
    {
        ImageElement.Stretch = ImageStretch;
        ImageElement.Opacity = Math.Clamp(ImageOpacity, 0, 1);
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

    private static void ClearRect(byte[] canvas, int width, int height, int left, int top, int rectWidth, int rectHeight)
    {
        for (var y = Math.Max(0, top); y < Math.Min(height, top + rectHeight); y++)
            Array.Clear(canvas, (y * width + Math.Max(0, left)) * 4, (Math.Min(width, left + rectWidth) - Math.Max(0, left)) * 4);
    }

    private void TimerOnTick(object? sender, EventArgs e)
    {
        if (_frames.Count <= 1) { _timer.Stop(); return; }
        _frameIndex = (_frameIndex + 1) % _frames.Count;
        ImageElement.Source = _frames[_frameIndex];
        _timer.Interval = _delays[_frameIndex];
    }

    private void UpdateAnimationState()
    {
        var shouldRun = _frames.Count > 1 && IsVisible && _window?.WindowState != WindowState.Minimized;
        if (shouldRun)
        {
            _timer.Interval = _delays[Math.Clamp(_frameIndex, 0, _delays.Count - 1)];
            _timer.Start();
        }
        else _timer.Stop();
    }

    private void ClearFrames()
    {
        ImageElement.Source = null;
        _frames.Clear();
        _delays.Clear();
        _frameIndex = 0;
    }
}
