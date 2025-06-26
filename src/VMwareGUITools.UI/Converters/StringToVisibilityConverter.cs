using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace VMwareGUITools.UI.Converters;

/// <summary>
/// Converter that converts string values to visibility (visible for non-empty strings, collapsed for empty/null)
/// </summary>
public class StringToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string stringValue)
        {
            return string.IsNullOrWhiteSpace(stringValue) ? Visibility.Collapsed : Visibility.Visible;
        }

        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
} 