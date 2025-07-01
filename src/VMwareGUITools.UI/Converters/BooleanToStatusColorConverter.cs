using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace VMwareGUITools.UI.Converters
{
    /// <summary>
    /// Converter that returns green for true, red for false
    /// </summary>
    public class BooleanToStatusColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue)
            {
                return boolValue ? Colors.LimeGreen : Colors.Red;
            }

            return Colors.Gray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
} 