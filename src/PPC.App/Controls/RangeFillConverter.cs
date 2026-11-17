using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Ppc.App.Controls
{
    /// <summary>
    /// 把滑块数值换算成「已填充轨道」的像素宽度。
    /// 绑定顺序：Value、Minimum、Maximum、轨道宽度。
    /// 只用于画填充条，不参与任何业务判断，因此异常时安全返回 0。
    /// </summary>
    public sealed class RangeFillConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
        {
            if (values is null || values.Length < 4)
            {
                return 0d;
            }

            if (!TryToDouble(values[0], out double value) ||
                !TryToDouble(values[1], out double min) ||
                !TryToDouble(values[2], out double max) ||
                !TryToDouble(values[3], out double width))
            {
                return 0d;
            }

            if (double.IsNaN(width) || double.IsInfinity(width) || width <= 0)
            {
                return 0d;
            }

            double range = max - min;
            if (double.IsNaN(range) || double.IsInfinity(range) || range <= 0)
            {
                return 0d;
            }

            double ratio = (value - min) / range;
            if (double.IsNaN(ratio) || double.IsInfinity(ratio))
            {
                return 0d;
            }

            ratio = Math.Clamp(ratio, 0d, 1d);
            return ratio * width;
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        {
            // 单向展示，不需要反向转换。
            return targetTypes.Select(_ => DependencyProperty.UnsetValue).ToArray();
        }

        private static bool TryToDouble(object? raw, out double result)
        {
            switch (raw)
            {
                case double d:
                    result = d;
                    return true;
                case float f:
                    result = f;
                    return true;
                case decimal m:
                    result = (double)m;
                    return true;
                case int i:
                    result = i;
                    return true;
                case long l:
                    result = l;
                    return true;
                case string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed):
                    result = parsed;
                    return true;
                case null:
                    result = 0d;
                    return false;
                default:
                    result = 0d;
                    return false;
            }
        }
    }
}
