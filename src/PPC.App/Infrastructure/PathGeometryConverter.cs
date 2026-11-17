using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Ppc.App.Infrastructure;

/// <summary>把 SVG 风格的路径字符串转换成 WPF Geometry，用于矢量图标。</summary>
public sealed class PathGeometryConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string text || string.IsNullOrWhiteSpace(text)) return Geometry.Empty;
        try { return Geometry.Parse(text); }
        catch { return Geometry.Empty; }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
