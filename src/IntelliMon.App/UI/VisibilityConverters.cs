using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace IntelliMon.App.UI;

/// <summary>
/// Shared value converters for XAML bindings.
/// </summary>
public static class VisibilityConverters
{
    public static readonly GreaterThanZeroConverter GreaterThanZero = new();
}

/// <summary>
/// Returns Visible when the bound value > 0.0, Collapsed otherwise.
/// </summary>
public sealed class GreaterThanZeroConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is double d) return d > 0.0 ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
