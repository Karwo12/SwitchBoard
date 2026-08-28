using System.Windows;
using SwitchBoard.Controls;
using SwitchBoard.Views;

namespace SwitchBoard.RuntimeTests.Views;

public sealed class BackgroundWindowAutoSizeTests
{
    [Theory]
    [InlineData("background.jpg")]
    [InlineData("background.gif")]
    [InlineData("background.mp4")]
    public void Native1920x1080_MapsToTheSamePhysicalBackgroundForEverySupportedRenderer(string sourcePath)
    {
        var fit = Calculate(sourcePath, new Size(1920, 1080), new DpiScale(1, 1));

        Assert.NotNull(fit);
        Assert.Equal(new Size(1920, 1080), fit.Value.BackgroundPixels);
        Assert.Equal(new Size(1920, 1080), fit.Value.BackgroundDips);
        Assert.Equal(1, fit.Value.AppliedScale, 3);
    }

    [Theory]
    [InlineData(1.25, 1536, 864)]
    [InlineData(1.5, 1280, 720)]
    public void NativePixels_AreConvertedToDipsUsingTheCurrentMonitorDpi(double scale, double expectedWidth,
        double expectedHeight)
    {
        var fit = Calculate("background.png", new Size(1920, 1080), new DpiScale(scale, scale));

        Assert.NotNull(fit);
        Assert.Equal(expectedWidth, fit.Value.BackgroundDips.Width, 3);
        Assert.Equal(expectedHeight, fit.Value.BackgroundDips.Height, 3);
        Assert.Equal(new Size(1920, 1080), fit.Value.BackgroundPixels);
    }

    [Fact]
    public void OversizedAsset_IsReducedToWorkingAreaWithoutChangingAspectRatio()
    {
        var fit = Calculate("background.mp4", new Size(3840, 2160), new DpiScale(1, 1),
            workingArea: new Size(1920, 1080));

        Assert.NotNull(fit);
        Assert.Equal(new Size(1920, 1080), fit.Value.BackgroundPixels);
        Assert.Equal(16d / 9d, fit.Value.BackgroundPixels.Width / fit.Value.BackgroundPixels.Height, 3);
        Assert.Equal(0.5, fit.Value.AppliedScale, 3);
    }

    [Fact]
    public void LargerMonitor_DoesNotUpscaleTheNativeAsset()
    {
        var fit = Calculate("background.jpg", new Size(1920, 1080), new DpiScale(1, 1),
            workingArea: new Size(3840, 2160));

        Assert.NotNull(fit);
        Assert.Equal(new Size(1920, 1080), fit.Value.BackgroundPixels);
        Assert.Equal(1, fit.Value.AppliedScale, 3);
    }

    [Fact]
    public void ChangingThemeBackground_RecalculatesTheNextWindowTarget()
    {
        var first = Calculate("first.jpg", new Size(1920, 1080), new DpiScale(1, 1));
        var second = Calculate("second.gif", new Size(1600, 900), new DpiScale(1, 1));

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first.Value.WindowDips, second.Value.WindowDips);
        Assert.Equal(new Size(1600, 900), second.Value.BackgroundPixels);
    }

    [Fact]
    public void NoBackground_ProducesNoResizeInstruction()
    {
        var fit = BackgroundWindowAutoSize.Calculate(default, new Size(1920, 1080), new Size(1200, 800),
            new Size(1200, 800), new DpiScale(1, 1));

        Assert.Null(fit);
    }

    [Fact]
    public void SourceSizeCache_ReportsOnlySourceOrDimensionChanges_NotAnimationFrames()
    {
        var cache = new BackgroundNativeSizeCache();
        var gif = new BackgroundNativeSize(@"C:\themes\animated.gif", 1920, 1080);

        Assert.True(cache.TryUpdate(gif));
        Assert.False(cache.TryUpdate(gif));
        Assert.True(cache.TryUpdate(gif with { PixelWidth = 1600 }));
        cache.ClearWhenSourceChanges(@"C:\themes\other.gif");
        Assert.Null(cache.Current);
    }

    private static BackgroundWindowAutoSizeResult? Calculate(string sourcePath, Size asset, DpiScale dpi,
        Size? workingArea = null) => BackgroundWindowAutoSize.Calculate(
        new BackgroundNativeSize(sourcePath, (int)asset.Width, (int)asset.Height),
        workingArea ?? new Size(3840, 2160), new Size(1200, 800), new Size(1200, 800), dpi);
}
