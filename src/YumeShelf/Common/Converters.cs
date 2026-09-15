using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace YumeShelf.Common;

public sealed class BooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility.Collapsed;
}

public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is not true;
}

