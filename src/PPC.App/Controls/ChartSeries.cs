using System.Windows.Media;
using Ppc.App.Infrastructure;

namespace Ppc.App.Controls
{
    /// <summary>
    /// 折线图的一条数据序列。
    /// 只负责承载数据与样式，不持有任何控件；LineChart 订阅本对象的 PropertyChanged 后重绘。
    /// 数据长度大于 0 但含 null 时，null 代表「该采样点缺失」，折线在缺口处断开。
    /// </summary>
    public sealed class ChartSeries : ObservableObject
    {
        private static readonly Brush DefaultStroke = CreateDefaultStroke();

        private string _label = string.Empty;
        private Brush _stroke = DefaultStroke;
        private IReadOnlyList<double?> _values = Array.Empty<double?>();
        private IReadOnlyList<DateTimeOffset> _timestamps = Array.Empty<DateTimeOffset>();
        private double? _min;
        private double? _max;

        /// <summary>图例名称。</summary>
        public string Label
        {
            get => _label;
            set => Set(ref _label, value ?? string.Empty);
        }

        /// <summary>折线颜色。</summary>
        public Brush Stroke
        {
            get => _stroke;
            set => Set(ref _stroke, value ?? DefaultStroke);
        }

        /// <summary>按时间顺序排列的数值，null 表示缺失采样。</summary>
        public IReadOnlyList<double?> Values
        {
            get => _values;
            set => Set(ref _values, value ?? Array.Empty<double?>());
        }

        /// <summary>与 Values 一一对应的时间戳，可为空集合。</summary>
        public IReadOnlyList<DateTimeOffset> Timestamps
        {
            get => _timestamps;
            set => Set(ref _timestamps, value ?? Array.Empty<DateTimeOffset>());
        }

        /// <summary>该序列的参考下限（可选），用于与 AutoScale 结合确定坐标范围。</summary>
        public double? Min
        {
            get => _min;
            set => Set(ref _min, value);
        }

        /// <summary>该序列的参考上限（可选）。</summary>
        public double? Max
        {
            get => _max;
            set => Set(ref _max, value);
        }

        /// <summary>
        /// 通知图表刷新。
        /// 原地修改 Values 内部数组（而非整体替换引用）时必须显式调用它。
        /// </summary>
        public void Invalidate()
        {
            Raise(nameof(Values));
        }

        private static Brush CreateDefaultStroke()
        {
            var brush = new SolidColorBrush(Color.FromRgb(0x4F, 0x8B, 0xFF));
            brush.Freeze();
            return brush;
        }
    }
}
