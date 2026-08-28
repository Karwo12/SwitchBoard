using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SwitchBoard.Converters;

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Inverse { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var isVisible = Inverse ? value is null : value is not null;
        return isVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
