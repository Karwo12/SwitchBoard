using System.Windows;
using System.Windows.Controls;

namespace SwitchBoard.Controls;

/// <summary>
/// Renders a themed card surface and its content as separate layers.
/// SurfaceOpacity affects only the themed background/border layer so card
/// content keeps the same readability as the Home action cards.
/// </summary>
public sealed class CardSurfaceControl : ContentControl
{
    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(
            nameof(CornerRadius),
            typeof(CornerRadius),
            typeof(CardSurfaceControl),
            new FrameworkPropertyMetadata(new CornerRadius(11)));

    public static readonly DependencyProperty SurfaceOpacityProperty =
        DependencyProperty.Register(
            nameof(SurfaceOpacity),
            typeof(double),
            typeof(CardSurfaceControl),
            new FrameworkPropertyMetadata(0.24d));

    public static readonly DependencyProperty HoverSurfaceBrushProperty =
        DependencyProperty.Register(
            nameof(HoverSurfaceBrush),
            typeof(System.Windows.Media.Brush),
            typeof(CardSurfaceControl),
            new FrameworkPropertyMetadata(System.Windows.Media.Brushes.Transparent));

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    public double SurfaceOpacity
    {
        get => (double)GetValue(SurfaceOpacityProperty);
        set => SetValue(SurfaceOpacityProperty, value);
    }

    public System.Windows.Media.Brush HoverSurfaceBrush
    {
        get => (System.Windows.Media.Brush)GetValue(HoverSurfaceBrushProperty);
        set => SetValue(HoverSurfaceBrushProperty, value);
    }
}
