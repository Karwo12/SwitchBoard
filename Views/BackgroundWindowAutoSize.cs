using System.Windows;
using SwitchBoard.Controls;

namespace SwitchBoard.Views;

/// <summary>
/// Converts native media pixels into a target WPF window size. All dimensions passed
/// to the calculator are physical pixels except the result, which is expressed in DIP.
/// Keeping the arithmetic outside Window makes DPI and working-area behavior testable.
/// </summary>
internal static class BackgroundWindowAutoSize
{
    public static BackgroundWindowAutoSizeResult? Calculate(
        BackgroundNativeSize nativeSize,
        Size workingAreaPixels,
        Size windowPixels,
        Size backgroundPixels,
        DpiScale dpi)
    {
        if (!nativeSize.IsValid || workingAreaPixels.Width <= 0 || workingAreaPixels.Height <= 0 ||
            windowPixels.Width <= 0 || windowPixels.Height <= 0 ||
            backgroundPixels.Width <= 0 || backgroundPixels.Height <= 0 ||
            dpi.DpiScaleX <= 0 || dpi.DpiScaleY <= 0)
            return null;

        // WindowChrome is already inside the root visual, but an OS resize border can
        // still make the outer HWND fractionally larger. Measure that difference rather
        // than assuming or adding a title-bar height.
        var nonBackgroundWidth = Math.Max(0, windowPixels.Width - backgroundPixels.Width);
        var nonBackgroundHeight = Math.Max(0, windowPixels.Height - backgroundPixels.Height);
        var availableBackgroundWidth = Math.Max(1, workingAreaPixels.Width - nonBackgroundWidth);
        var availableBackgroundHeight = Math.Max(1, workingAreaPixels.Height - nonBackgroundHeight);

        // Never enlarge above the native asset. Large media is reduced uniformly until
        // the complete outer window fits the current monitor work area.
        var scale = Math.Min(1d, Math.Min(
            availableBackgroundWidth / nativeSize.PixelWidth,
            availableBackgroundHeight / nativeSize.PixelHeight));
        var targetBackgroundPixels = new Size(
            Math.Max(1, Math.Round(nativeSize.PixelWidth * scale)),
            Math.Max(1, Math.Round(nativeSize.PixelHeight * scale)));
        var targetWindowPixels = new Size(
            targetBackgroundPixels.Width + nonBackgroundWidth,
            targetBackgroundPixels.Height + nonBackgroundHeight);

        return new BackgroundWindowAutoSizeResult(
            new Size(targetBackgroundPixels.Width / dpi.DpiScaleX,
                targetBackgroundPixels.Height / dpi.DpiScaleY),
            new Size(targetWindowPixels.Width / dpi.DpiScaleX,
                targetWindowPixels.Height / dpi.DpiScaleY),
            targetBackgroundPixels,
            scale);
    }
}

internal readonly record struct BackgroundWindowAutoSizeResult(
    Size BackgroundDips,
    Size WindowDips,
    Size BackgroundPixels,
    double AppliedScale);
