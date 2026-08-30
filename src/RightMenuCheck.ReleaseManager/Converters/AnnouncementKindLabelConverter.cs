using System.Globalization;
using System.Windows;
using System.Windows.Data;
using RightMenuCheck.Distribution;

namespace RightMenuCheck.ReleaseManager.Converters;

public sealed class AnnouncementKindLabelConverter : IValueConverter
{
    public object Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) => value switch
        {
            AnnouncementKind.Information => "信息",
            AnnouncementKind.Warning => "警告",
            AnnouncementKind.Maintenance => "维护",
            _ => string.Empty,
        };

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) => DependencyProperty.UnsetValue;
}
