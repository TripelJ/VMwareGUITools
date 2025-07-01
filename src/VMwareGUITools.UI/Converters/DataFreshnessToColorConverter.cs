using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace VMwareGUITools.UI.Converters
{
    /// <summary>
    /// Converter that returns a color based on how fresh the data is
    /// </summary>
    public class DataFreshnessToColorConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Length < 2 || values[0] == null || values[1] == null)
                return Colors.Gray;

            if (values[0] is DateTime lastUpdated && values[1] is DateTime currentTime)
            {
                var timeDifference = currentTime - lastUpdated;

                // Fresh data (within 5 minutes) - Green
                if (timeDifference.TotalMinutes <= 5)
                    return Colors.LimeGreen;

                // Moderately fresh (5-15 minutes) - Yellow
                if (timeDifference.TotalMinutes <= 15)
                    return Colors.Yellow;

                // Stale data (15-60 minutes) - Orange
                if (timeDifference.TotalMinutes <= 60)
                    return Colors.Orange;

                // Very stale data (over 1 hour) - Red
                return Colors.Red;
            }

            return Colors.Gray;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
} 