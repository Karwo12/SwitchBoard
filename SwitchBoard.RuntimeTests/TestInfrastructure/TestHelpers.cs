namespace SwitchBoard.RuntimeTests.TestInfrastructure;

public static class TestHelpers
{
    public static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default, Func<string>? timeoutDetails = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(2));
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline)
            {
                var details = timeoutDetails?.Invoke();
                throw new TimeoutException(string.IsNullOrWhiteSpace(details)
                    ? "The asynchronous test condition was not reached."
                    : $"The asynchronous test condition was not reached. {details}");
            }
            await Task.Delay(20, cancellationToken);
        }
    }

    public static void CreateTestImages(string directory)
    {
        var red = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            Enumerable.Repeat(new byte[] { 0, 0, 255, 255 }, 4).SelectMany(pixel => pixel).ToArray(), 8);
        var blue = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null,
            Enumerable.Repeat(new byte[] { 255, 0, 0, 255 }, 4).SelectMany(pixel => pixel).ToArray(), 8);
        red.Freeze();
        blue.Freeze();

        SaveEncoder(new JpegBitmapEncoder(), [red], Path.Combine(directory, "test.jpg"));
        SaveEncoder(new PngBitmapEncoder(), [red], Path.Combine(directory, "test.png"));
        SaveEncoder(new BmpBitmapEncoder(), [red], Path.Combine(directory, "test.bmp"));
        SaveEncoder(new GifBitmapEncoder(), [red, blue], Path.Combine(directory, "test.gif"));
        CreateTestMp4(Path.Combine(directory, "test.mp4"));
    }

    public static void CreateTestMp4(string path) => File.WriteAllBytes(path,
        [0, 0, 0, 20, (byte)'f', (byte)'t', (byte)'y', (byte)'p',
         (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 0, 0,
         (byte)'i', (byte)'s', (byte)'o', (byte)'m']);

    private static void SaveEncoder(BitmapEncoder encoder, IReadOnlyList<BitmapSource> frames, string path)
    {
        foreach (var frame in frames) encoder.Frames.Add(BitmapFrame.Create(frame));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    public static double ContrastRatio(Color first, Color second)
    {
        static double Linear(byte channel)
        {
            var value = channel / 255d;
            return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
        }

        static double Luminance(Color color) =>
            0.2126 * Linear(color.R) + 0.7152 * Linear(color.G) + 0.0722 * Linear(color.B);

        var light = Math.Max(Luminance(first), Luminance(second));
        var dark = Math.Min(Luminance(first), Luminance(second));
        return (light + 0.05) / (dark + 0.05);
    }

    public static bool SemanticContrastIsAccessible(System.Windows.Application app)
    {
        var pairs = new[]
        {
            ("PrimaryButtonBackground", "PrimaryButtonForeground"),
            ("PrimaryButtonHoverBackground", "PrimaryButtonHoverForeground"),
            ("PrimaryButtonPressedBackground", "PrimaryButtonPressedForeground"),
            ("PrimaryButtonDisabledBackground", "PrimaryButtonDisabledForeground"),
            ("SecondaryButtonBackground", "SecondaryButtonForeground"),
            ("SecondaryButtonHoverBackground", "SecondaryButtonHoverForeground"),
            ("SecondaryButtonPressedBackground", "SecondaryButtonPressedForeground"),
            ("SecondaryButtonDisabledBackground", "SecondaryButtonDisabledForeground"),
            ("MenuBackground", "MenuForeground"),
            ("MenuHoverBackground", "MenuHoverForeground"),
            ("MenuPressedBackground", "MenuPressedForeground"),
            ("MenuDisabledBackground", "MenuDisabledForeground"),
            ("SelectedBrush", "SelectionForeground"),
            ("HoverBrush", "HoverForeground"),
            ("DisabledInputBackground", "DisabledInputForeground"),
            ("BackgroundBrush", "TextPrimaryBrush"),
            ("BackgroundBrush", "TextSecondaryBrush")
        };
        var applicationBackground = RepresentativeColor(app.TryFindResource("BackgroundBrush") as Brush, Colors.Black);
        return pairs.All(pair => app.TryFindResource(pair.Item1) is Brush background &&
                                 app.TryFindResource(pair.Item2) is SolidColorBrush foreground &&
                                 BrushColors(background).All(color =>
                                     ContrastRatio(Composite(color, applicationBackground), foreground.Color) >= 4.5));
    }

    public static bool RenderedSemanticControlsAreAccessible(System.Windows.Application app)
    {
        var secondary = new System.Windows.Controls.Button
        {
            Style = app.TryFindResource(typeof(System.Windows.Controls.Button)) as System.Windows.Style
        };
        var primary = new System.Windows.Controls.Button
        {
            Style = app.TryFindResource("AccentButton") as System.Windows.Style
        };
        var menu = new System.Windows.Controls.ContextMenu();
        var menuItem = new System.Windows.Controls.MenuItem
        {
            Header = "Theme",
            Style = app.TryFindResource(typeof(System.Windows.Controls.MenuItem)) as System.Windows.Style
        };
        menu.Items.Add(menuItem);

        // Apply the templates before inspecting trigger-driven Background/Foreground
        // values. The controls are intentionally not shown in a window in this test.
        secondary.ApplyTemplate();
        primary.ApplyTemplate();
        menuItem.ApplyTemplate();

        var normal = Matches(secondary, "SecondaryButtonBackground", "SecondaryButtonForeground") &&
                     Matches(primary, "PrimaryButtonBackground", "PrimaryButtonForeground") &&
                     Matches(menuItem, "MenuBackground", "MenuForeground");
        secondary.IsEnabled = false;
        primary.IsEnabled = false;
        menuItem.IsEnabled = false;
        return normal &&
               Matches(secondary, "SecondaryButtonDisabledBackground", "SecondaryButtonDisabledForeground") &&
               Matches(primary, "PrimaryButtonDisabledBackground", "PrimaryButtonDisabledForeground") &&
               Matches(menuItem, "MenuDisabledBackground", "MenuDisabledForeground");

        bool Matches(System.Windows.Controls.Control control, string backgroundKey, string foregroundKey) =>
            control.Background is Brush actualBackground && control.Foreground is SolidColorBrush actualForeground &&
            app.TryFindResource(backgroundKey) is Brush expectedBackground &&
            app.TryFindResource(foregroundKey) is SolidColorBrush expectedForeground &&
            BrushColors(actualBackground).SequenceEqual(BrushColors(expectedBackground)) &&
            actualForeground.Color == expectedForeground.Color;

    }

    private static IReadOnlyList<Color> BrushColors(Brush brush) => brush switch
    {
        SolidColorBrush solid => [solid.Color],
        GradientBrush gradient when gradient.GradientStops.Count > 0 => gradient.GradientStops.Select(stop => stop.Color).ToArray(),
        _ => [Colors.Black]
    };

    private static Color RepresentativeColor(Brush? brush, Color fallback)
    {
        if (brush is null) return fallback;
        var colors = BrushColors(brush);
        return Color.FromArgb((byte)Math.Round(colors.Average(value => value.A)),
            (byte)Math.Round(colors.Average(value => value.R)),
            (byte)Math.Round(colors.Average(value => value.G)),
            (byte)Math.Round(colors.Average(value => value.B)));
    }

    private static Color Composite(Color foreground, Color background)
    {
        if (foreground.A == byte.MaxValue) return foreground;
        var alpha = foreground.A / 255d;
        return Color.FromRgb(
            (byte)Math.Round(foreground.R * alpha + background.R * (1 - alpha)),
            (byte)Math.Round(foreground.G * alpha + background.G * (1 - alpha)),
            (byte)Math.Round(foreground.B * alpha + background.B * (1 - alpha)));
    }
}
