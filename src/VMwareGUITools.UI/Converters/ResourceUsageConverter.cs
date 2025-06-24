using System.Globalization;
using System.Windows.Data;
using VMwareGUITools.Core.Models;

namespace VMwareGUITools.UI.Converters;

/// <summary>
/// Converter to display ResourceUsage objects in a user-friendly format
/// </summary>
public class ResourceUsageConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not ResourceUsage usage) return "N/A";

        return usage.Unit switch
        {
            "MHz" => $"{usage.UsedCapacity / 1000:F1} / {usage.TotalCapacity / 1000:F1} GHz ({usage.UsagePercentage:F1}%)",
            "MB" => $"{usage.UsedCapacity / 1024:F1} / {usage.TotalCapacity / 1024:F1} GB ({usage.UsagePercentage:F1}%)",
            "GB" => $"{usage.UsedCapacity:F1} / {usage.TotalCapacity:F1} GB ({usage.UsagePercentage:F1}%)",
            _ => $"{usage.UsedCapacity} / {usage.TotalCapacity} {usage.Unit} ({usage.UsagePercentage:F1}%)"
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
} 