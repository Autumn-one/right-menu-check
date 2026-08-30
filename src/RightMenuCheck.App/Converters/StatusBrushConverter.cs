using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace RightMenuCheck.App.Converters;

public sealed class StatusBrushConverter : IValueConverter
{
    private static readonly Brush Normal = new SolidColorBrush(Color.FromRgb(55, 75, 87));
    private static readonly Brush Success = new SolidColorBrush(Color.FromRgb(0, 112, 92));
    private static readonly Brush Warning = new SolidColorBrush(Color.FromRgb(166, 93, 0));
    private static readonly Brush Danger = new SolidColorBrush(Color.FromRgb(184, 46, 46));

    public object Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture)
    {
        var text = value?.ToString() ?? string.Empty;
        if (text.Contains("失败", StringComparison.Ordinal) ||
            text.Contains("阻止", StringComparison.Ordinal) ||
            text.Contains("无效", StringComparison.Ordinal))
        {
            return Danger;
        }

        if (text.Contains("完成", StringComparison.Ordinal) ||
            text.Contains("启用", StringComparison.Ordinal) ||
            text.Contains("有效", StringComparison.Ordinal))
        {
            return Success;
        }

        if (text.Contains("隐藏", StringComparison.Ordinal) ||
            text.Contains("覆盖", StringComparison.Ordinal) ||
            text.Contains("不可", StringComparison.Ordinal) ||
            text.Contains('未'))
        {
            return Warning;
        }

        return Normal;
    }

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) =>
        throw new NotSupportedException();
}
