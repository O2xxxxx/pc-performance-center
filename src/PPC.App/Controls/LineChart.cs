using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Ppc.Core.Services;

namespace Ppc.App.Controls
{
    /// <summary>
    /// 历史折线图：网格 + 渐变面积 + 折线 + 图例，全部用 DrawingContext 与 StreamGeometry 绘制。
    /// 数据来自 ChartSeries 集合；空数据、全 null、单点、NaN、量程非法都安全降级，不会抛异常。
    /// </summary>
    public sealed class LineChart : FrameworkElement
    {
        private const double PadLeft = 46d;
        private const double PadRight = 14d;
        private const double PadTop = 12d;
        private const double PadBottom = 24d;
        private const double LegendHeight = 18d;

        private static readonly Typeface LabelTypeface = new(
            new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI"),
            FontStyles.Normal,
            FontWeights.Normal,
            FontStretches.Normal);

        private static readonly Typeface LegendTypeface = new(
            new FontFamily("Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI"),
            FontStyles.Normal,
            FontWeights.SemiBold,
            FontStretches.Normal);

        private readonly Dictionary<ChartSeries, PropertyChangedEventHandler> _itemSubscriptions = new();
        private INotifyCollectionChanged? _collectionSource;
        private NotifyCollectionChangedEventHandler? _collectionHandler;
        private double _pixelsPerDip = 1d;
        private bool _renderErrorLogged;

        public LineChart()
        {
            MinHeight = 140d;
            ClipToBounds = true;
        }

        public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
            nameof(Series), typeof(IEnumerable<ChartSeries>), typeof(LineChart),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSeriesPropertyChanged));

        public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
            nameof(Unit), typeof(string), typeof(LineChart),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty FixedMinProperty = DependencyProperty.Register(
            nameof(FixedMin), typeof(double?), typeof(LineChart),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty FixedMaxProperty = DependencyProperty.Register(
            nameof(FixedMax), typeof(double?), typeof(LineChart),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ShowGridProperty = DependencyProperty.Register(
            nameof(ShowGrid), typeof(bool), typeof(LineChart),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ShowLegendProperty = DependencyProperty.Register(
            nameof(ShowLegend), typeof(bool), typeof(LineChart),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty AutoScaleProperty = DependencyProperty.Register(
            nameof(AutoScale), typeof(bool), typeof(LineChart),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>图例名称。</summary>
        public IEnumerable<ChartSeries>? Series
        {
            get => (IEnumerable<ChartSeries>?)GetValue(SeriesProperty);
            set => SetValue(SeriesProperty, value);
        }

        /// <summary>纵轴单位，例如 °C、%、MHz。</summary>
        public string Unit
        {
            get => (string)GetValue(UnitProperty);
            set => SetValue(UnitProperty, value);
        }

        /// <summary>固定纵轴下限；null 表示按数据自动计算。</summary>
        public double? FixedMin
        {
            get => (double?)GetValue(FixedMinProperty);
            set => SetValue(FixedMinProperty, value);
        }

        /// <summary>固定纵轴上限；null 表示按数据自动计算。</summary>
        public double? FixedMax
        {
            get => (double?)GetValue(FixedMaxProperty);
            set => SetValue(FixedMaxProperty, value);
        }

        /// <summary>是否绘制网格线。</summary>
        public bool ShowGrid
        {
            get => (bool)GetValue(ShowGridProperty);
            set => SetValue(ShowGridProperty, value);
        }

        /// <summary>是否绘制图例。</summary>
        public bool ShowLegend
        {
            get => (bool)GetValue(ShowLegendProperty);
            set => SetValue(ShowLegendProperty, value);
        }

        /// <summary>为 true 时按实际数据缩放纵轴；显式设置了 FixedMin/FixedMax 的一端始终优先。</summary>
        public bool AutoScale
        {
            get => (bool)GetValue(AutoScaleProperty);
            set => SetValue(AutoScaleProperty, value);
        }

        private static void OnSeriesPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is LineChart chart)
            {
                chart.AttachSeries(e.OldValue as IEnumerable<ChartSeries>, e.NewValue as IEnumerable<ChartSeries>);
            }
        }

        private void AttachSeries(IEnumerable<ChartSeries>? oldSeries, IEnumerable<ChartSeries>? newSeries)
        {
            if (oldSeries is INotifyCollectionChanged oldCollection && _collectionHandler is not null)
            {
                oldCollection.CollectionChanged -= _collectionHandler;
            }

            _collectionSource = null;
            _collectionHandler = null;

            foreach (KeyValuePair<ChartSeries, PropertyChangedEventHandler> pair in _itemSubscriptions)
            {
                pair.Key.PropertyChanged -= pair.Value;
            }

            _itemSubscriptions.Clear();

            if (newSeries is null)
            {
                InvalidateVisual();
                return;
            }

            if (newSeries is INotifyCollectionChanged collection)
            {
                _collectionHandler = (_, _) =>
                {
                    SubscribeItems(newSeries);
                    InvalidateVisual();
                };
                collection.CollectionChanged += _collectionHandler;
                _collectionSource = collection;
            }

            SubscribeItems(newSeries);
            InvalidateVisual();
        }

        private void SubscribeItems(IEnumerable<ChartSeries>? series)
        {
            if (series is null)
            {
                return;
            }

            try
            {
                foreach (ChartSeries item in series)
                {
                    if (item is null || _itemSubscriptions.ContainsKey(item))
                    {
                        continue;
                    }

                    PropertyChangedEventHandler handler = (_, _) => InvalidateVisual();
                    item.PropertyChanged += handler;
                    _itemSubscriptions[item] = handler;
                }
            }
            catch (Exception ex)
            {
                LogOnce("LineChart 订阅序列失败：" + ex.Message, ex);
            }
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double w = double.IsInfinity(availableSize.Width) ? 320d : availableSize.Width;
            double h = double.IsInfinity(availableSize.Height) ? 160d : availableSize.Height;
            return new Size(Math.Max(0d, w), Math.Max(0d, h));
        }

        protected override void OnRender(DrawingContext dc)
        {
            if (dc is null)
            {
                return;
            }

            try
            {
                RenderCore(dc);
            }
            catch (Exception ex)
            {
                LogOnce("LineChart 绘制失败：" + ex.Message, ex);
            }
        }

        private void RenderCore(DrawingContext dc)
        {
            double w = ActualWidth;
            double h = ActualHeight;
            if (double.IsNaN(w) || double.IsNaN(h) || w < 40d || h < 40d)
            {
                return;
            }

            _pixelsPerDip = GetPixelsPerDip();

            double plotLeft = PadLeft;
            double plotRight = w - PadRight;
            double plotTop = PadTop;
            double plotBottom = h - PadBottom;

            List<ChartSeries> sources = SnapshotSourceList();
            List<SeriesSnapshot> snapshots = new(sources.Count);
            bool anyLegend = false;
            int maxCount = 0;

            foreach (ChartSeries source in sources)
            {
                SeriesSnapshot snapshot = SeriesSnapshot.Create(source);
                if (snapshot.Values.Length == 0)
                {
                    continue;
                }

                maxCount = Math.Max(maxCount, snapshot.Values.Length);
                if (!string.IsNullOrWhiteSpace(snapshot.Label))
                {
                    anyLegend = true;
                }

                snapshots.Add(snapshot);
            }

            bool showLegend = ShowLegend && anyLegend;
            if (showLegend)
            {
                plotTop += LegendHeight;
            }

            if (plotRight - plotLeft < 12d || plotBottom - plotTop < 12d)
            {
                return;
            }

            Brush grid = ResolveBrush("Chart.Grid", new SolidColorBrush(Color.FromRgb(0x1B, 0x24, 0x30)));
            Brush axis = ResolveBrush("Chart.Axis", new SolidColorBrush(Color.FromRgb(0x46, 0x53, 0x6A)));
            Brush tertiary = ResolveBrush("Text.Tertiary", Brushes.Gray);

            ResolveRange(snapshots, out double lo, out double hi);

            string unit = (Unit ?? string.Empty).Trim();
            var axisLabels = new string[5];
            double gutter = PadLeft;
            for (int step = 0; step <= 4; step++)
            {
                string text = FormatAxis(lo + (hi - lo) * (step / 4d), hi - lo);
                if (step == 4 && unit.Length > 0)
                {
                    text += " " + unit;
                }

                axisLabels[step] = text;
                // 左留白按最宽刻度文字自适应，单位（如 RPM、°C）不会被截断
                double width = CreateText(text, 10d, tertiary, 240d, bold: false).Width;
                gutter = Math.Max(gutter, width + 10d);
            }

            plotLeft = Math.Min(gutter, Math.Max(PadLeft, w * 0.32d));

            // 网格
            if (ShowGrid)
            {
                Pen gridPen = new(grid, 1d);
                gridPen.Freeze();
                for (int step = 0; step <= 4; step++)
                {
                    double y = Math.Round(plotBottom - (plotBottom - plotTop) * (step / 4d)) + 0.5d;
                    dc.DrawLine(gridPen, new Point(plotLeft, y), new Point(plotRight, y));
                }
            }

            Pen axisPen = new(axis, 1d);
            axisPen.Freeze();
            dc.DrawLine(axisPen, new Point(plotLeft, Math.Round(plotBottom) + 0.5d), new Point(plotRight, Math.Round(plotBottom) + 0.5d));

            for (int step = 0; step <= 4; step++)
            {
                FormattedText ft = CreateText(axisLabels[step], 10d, tertiary, Math.Max(12d, plotLeft - 8d), bold: false);
                double x = Math.Max(2d, plotLeft - 8d - ft.Width);
                double y = plotBottom - (plotBottom - plotTop) * (step / 4d) - ft.Height / 2d;
                dc.DrawText(ft, new Point(x, Math.Clamp(y, plotTop - ft.Height, plotBottom)));
            }

            bool hasPoint = snapshots.Any(s => s.ValidCount > 0);
            if (!hasPoint)
            {
                FormattedText empty = CreateText("暂无历史数据", 12.5d, tertiary, plotRight - plotLeft - 12d, bold: false);
                dc.DrawText(empty, new Point(
                    plotLeft + (plotRight - plotLeft - empty.Width) / 2d,
                    plotTop + (plotBottom - plotTop - empty.Height) / 2d));
                if (showLegend)
                {
                    DrawLegend(dc, snapshots, plotLeft, PadTop, plotRight);
                }

                return;
            }

            // 折线绘制区裁剪，避免越界数据画出图外
            dc.PushClip(new RectangleGeometry(new Rect(plotLeft, plotTop, plotRight - plotLeft, plotBottom - plotTop)));
            foreach (SeriesSnapshot snapshot in snapshots)
            {
                DrawSeries(dc, snapshot, maxCount, plotLeft, plotTop, plotRight, plotBottom, lo, hi);
            }

            dc.Pop();

            DrawTimeAxis(dc, snapshots, plotLeft, plotBottom, plotRight, tertiary);
            if (showLegend)
            {
                DrawLegend(dc, snapshots, plotLeft, PadTop, plotRight);
            }
        }

        private List<ChartSeries> SnapshotSourceList()
        {
            var list = new List<ChartSeries>();
            IEnumerable<ChartSeries>? series = Series;
            if (series is null)
            {
                return list;
            }

            try
            {
                foreach (ChartSeries item in series)
                {
                    if (item is not null)
                    {
                        list.Add(item);
                    }
                }
            }
            catch (Exception ex)
            {
                LogOnce("LineChart 读取序列集合失败：" + ex.Message, ex);
            }

            return list;
        }

        private void ResolveRange(List<SeriesSnapshot> snapshots, out double lo, out double hi)
        {
            double? dataMin = null;
            double? dataMax = null;
            foreach (SeriesSnapshot snapshot in snapshots)
            {
                if (snapshot.ValidCount == 0)
                {
                    continue;
                }

                if (snapshot.Min is double sMin)
                {
                    dataMin = dataMin is null ? sMin : Math.Min(dataMin.Value, sMin);
                }

                if (snapshot.Max is double sMax)
                {
                    dataMax = dataMax is null ? sMax : Math.Max(dataMax.Value, sMax);
                }
            }

            double? fixedMin = FixedMin;
            double? fixedMax = FixedMax;
            bool auto = AutoScale;

            lo = fixedMin ?? (auto ? dataMin ?? 0d : 0d);
            hi = fixedMax ?? (auto ? dataMax ?? 100d : 100d);

            if (double.IsNaN(lo) || double.IsInfinity(lo))
            {
                lo = 0d;
            }

            if (double.IsNaN(hi) || double.IsInfinity(hi))
            {
                hi = lo + 100d;
            }

            if (hi <= lo)
            {
                hi = lo + (Math.Abs(lo) > 1d ? Math.Abs(lo) * 0.2d : 1d);
            }

            // 纯自动量程且确有数据时留 8% 余量，曲线不贴边；手工指定的一端保持不动
            if (auto && fixedMin is null && fixedMax is null && dataMin is not null)
            {
                double pad = (hi - lo) * 0.08d;
                if (dataMin is null || dataMin.Value >= 0d)
                {
                    lo = Math.Max(0d, lo - pad);
                }
                else
                {
                    lo -= pad;
                }

                hi += pad;
            }
        }

        private void DrawSeries(DrawingContext dc, SeriesSnapshot snapshot, int maxCount, double left, double top, double right, double bottom, double lo, double hi)
        {
            double plotW = right - left;
            double plotH = bottom - top;
            double range = hi - lo;
            if (plotW <= 0d || plotH <= 0d || range <= 0d)
            {
                return;
            }

            // 左右各内缩 3px，末端圆点与末点描边不会被绘图区裁掉
            const double inset = 3d;
            double innerW = Math.Max(0d, plotW - inset * 2d);
            double X(int index)
            {
                return maxCount <= 1 ? left + plotW / 2d : left + inset + innerW * index / (maxCount - 1d);
            }

            double Y(double value)
            {
                double y = bottom - (value - lo) / range * plotH;
                return double.IsNaN(y) || double.IsInfinity(y) ? bottom : y;
            }

            double?[] values = snapshot.Values;
            int lastValid = -1;
            for (int i = values.Length - 1; i >= 0; i--)
            {
                if (values[i].HasValue)
                {
                    lastValid = i;
                    break;
                }
            }

            Brush areaBrush = MakeAreaBrush(snapshot.Stroke);
            Pen linePen = new(snapshot.Stroke, 1.7d)
            {
                LineJoin = PenLineJoin.Round,
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            };

            int runStart = -1;
            for (int i = 0; i <= values.Length; i++)
            {
                bool valid = i < values.Length && values[i].HasValue;
                if (valid)
                {
                    if (runStart < 0)
                    {
                        runStart = i;
                    }

                    continue;
                }

                int runEnd = i;
                if (runStart >= 0)
                {
                    int count = runEnd - runStart;
                    if (count >= 2)
                    {
                        DrawArea(dc, values, runStart, runEnd, X, Y, bottom, areaBrush);
                        DrawPolyline(dc, values, runStart, runEnd, X, Y, linePen);
                    }
                    else if (count == 1)
                    {
                        dc.DrawEllipse(snapshot.Stroke, null, new Point(X(runStart), Y(values[runStart]!.Value)), 2.6d, 2.6d);
                    }

                    runStart = -1;
                }
            }

            if (lastValid >= 0)
            {
                Point last = new(X(lastValid), Y(values[lastValid]!.Value));
                Brush ring = ResolveBrush("Bg.Surface", Brushes.Black);
                Pen ringPen = new(ring, 1.6d);
                dc.DrawEllipse(snapshot.Stroke, ringPen, last, 3.1d, 3.1d);
            }
        }

        private static void DrawArea(DrawingContext dc, double?[] values, int start, int endExclusive, Func<int, double> x, Func<double, double> y, double baseline, Brush brush)
        {
            var geometry = new StreamGeometry();
            using (StreamGeometryContext ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(x(start), baseline), isFilled: true, isClosed: true);
                for (int i = start; i < endExclusive; i++)
                {
                    ctx.LineTo(new Point(x(i), y(values[i]!.Value)), isStroked: true, isSmoothJoin: false);
                }

                ctx.LineTo(new Point(x(endExclusive - 1), baseline), isStroked: true, isSmoothJoin: false);
            }

            geometry.Freeze();
            dc.DrawGeometry(brush, null, geometry);
        }

        private static void DrawPolyline(DrawingContext dc, double?[] values, int start, int endExclusive, Func<int, double> x, Func<double, double> y, Pen pen)
        {
            var geometry = new StreamGeometry();
            using (StreamGeometryContext ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(x(start), y(values[start]!.Value)), isFilled: false, isClosed: false);
                for (int i = start + 1; i < endExclusive; i++)
                {
                    ctx.LineTo(new Point(x(i), y(values[i]!.Value)), isStroked: true, isSmoothJoin: true);
                }
            }

            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);
        }

        private void DrawLegend(DrawingContext dc, List<SeriesSnapshot> snapshots, double left, double top, double right)
        {
            double x = left;
            double y = top;
            Brush secondary = ResolveBrush("Text.Secondary", Brushes.Gray);

            foreach (SeriesSnapshot snapshot in snapshots)
            {
                if (string.IsNullOrWhiteSpace(snapshot.Label))
                {
                    continue;
                }

                FormattedText ft = CreateText(snapshot.Label!, 10.5d, secondary, 160d, bold: false);
                double itemWidth = 18d + ft.Width + 14d;
                if (x + itemWidth > right)
                {
                    break;
                }

                Pen pen = new(snapshot.Stroke, 2.2d)
                {
                    StartLineCap = PenLineCap.Round,
                    EndLineCap = PenLineCap.Round,
                };
                dc.DrawLine(pen, new Point(x, y + 9d), new Point(x + 14d, y + 9d));
                dc.DrawEllipse(snapshot.Stroke, null, new Point(x + 14d, y + 9d), 2.2d, 2.2d);
                dc.DrawText(ft, new Point(x + 18d, y + 9d - ft.Height / 2d));
                x += itemWidth;
            }
        }

        private void DrawTimeAxis(DrawingContext dc, List<SeriesSnapshot> snapshots, double left, double bottom, double right, Brush brush)
        {
            SeriesSnapshot? withTime = snapshots.FirstOrDefault(s => s.Times is not null && s.Times.Length == s.Values.Length && s.Values.Length > 0);
            if (withTime?.Times is null || withTime.Times.Length < 2)
            {
                return;
            }

            DateTimeOffset first = withTime.Times[0];
            DateTimeOffset last = withTime.Times[withTime.Times.Length - 1];
            if (first == last)
            {
                // 单点采样时首尾相同，画两个一样的刻度没有意义
                return;
            }

            FormattedText firstFt = CreateText(first.ToString("HH:mm", CultureInfo.CurrentCulture), 10d, brush, 80d, bold: false);
            FormattedText lastFt = CreateText(last.ToString("HH:mm", CultureInfo.CurrentCulture), 10d, brush, 80d, bold: false);

            double y = bottom + 6d;
            dc.DrawText(firstFt, new Point(left, y));
            dc.DrawText(lastFt, new Point(Math.Max(left, right - lastFt.Width), y));
        }

        private FormattedText CreateText(string text, double size, Brush brush, double maxWidth, bool bold)
        {
            return new FormattedText(
                text ?? string.Empty,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                bold ? LegendTypeface : LabelTypeface,
                size,
                brush,
                _pixelsPerDip)
            {
                MaxTextWidth = Math.Max(1d, maxWidth),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };
        }

        private static string FormatAxis(double value, double span)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return "—";
            }

            double absSpan = Math.Abs(span);
            if (Math.Abs(value) >= 1000d || absSpan >= 100d)
            {
                return value.ToString("0", CultureInfo.CurrentCulture);
            }

            if (absSpan >= 10d)
            {
                return value.ToString("0.#", CultureInfo.CurrentCulture);
            }

            return value.ToString("0.##", CultureInfo.CurrentCulture);
        }

        private static Brush MakeAreaBrush(Brush stroke)
        {
            Color color = stroke is SolidColorBrush solid ? solid.Color : Color.FromRgb(0x4F, 0x8B, 0xFF);
            var brush = new LinearGradientBrush(
                Color.FromArgb(64, color.R, color.G, color.B),
                Color.FromArgb(6, color.R, color.G, color.B),
                new Point(0.5d, 0d),
                new Point(0.5d, 1d));
            brush.Freeze();
            return brush;
        }

        private Brush ResolveBrush(string key, Brush fallback)
        {
            object? found = TryFindResource(key);
            return found as Brush ?? fallback;
        }

        private double GetPixelsPerDip()
        {
            try
            {
                double scale = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                return scale > 0d ? scale : 1d;
            }
            catch (Exception)
            {
                return 1d;
            }
        }

        private void LogOnce(string message, Exception ex)
        {
            if (_renderErrorLogged)
            {
                return;
            }

            _renderErrorLogged = true;
            try
            {
                AppLog.Instance.Error(message, ex);
            }
            catch (Exception)
            {
                // 日志本身失败时保持沉默，不能反过来影响 UI。
            }
        }

        /// <summary>渲染用的数据快照：把可空数组复制成普通数组，避免重绘期间被后台线程改写。</summary>
        private sealed class SeriesSnapshot
        {
            public Brush Stroke { get; private init; } = Brushes.DodgerBlue;

            public string? Label { get; private init; }

            public double?[] Values { get; private init; } = Array.Empty<double?>();

            public DateTimeOffset[]? Times { get; private init; }

            public double? Min { get; private init; }

            public double? Max { get; private init; }

            public int ValidCount { get; private init; }

            public static SeriesSnapshot Create(ChartSeries source)
            {
                IReadOnlyList<double?>? rawValues = source.Values;
                int count = rawValues?.Count ?? 0;
                var values = new double?[count];
                double? min = null;
                double? max = null;
                int valid = 0;

                for (int i = 0; i < count; i++)
                {
                    double? v = rawValues![i];
                    if (v is null || double.IsNaN(v.Value) || double.IsInfinity(v.Value))
                    {
                        continue;
                    }

                    values[i] = v;
                    valid++;
                    min = min is null ? v : Math.Min(min.Value, v.Value);
                    max = max is null ? v : Math.Max(max.Value, v.Value);
                }

                // 序列自带的参考范围也参与量程（例如固定 0..100 的百分比）
                double? rangeMin = source.Min ?? min;
                double? rangeMax = source.Max ?? max;

                DateTimeOffset[]? times = null;
                IReadOnlyList<DateTimeOffset>? rawTimes = source.Timestamps;
                if (rawTimes is not null && rawTimes.Count == count && count > 0)
                {
                    times = new DateTimeOffset[count];
                    for (int i = 0; i < count; i++)
                    {
                        times[i] = rawTimes[i];
                    }
                }

                return new SeriesSnapshot
                {
                    Stroke = source.Stroke ?? Brushes.DodgerBlue,
                    Label = source.Label,
                    Values = values,
                    Times = times,
                    Min = rangeMin,
                    Max = rangeMax,
                    ValidCount = valid,
                };
            }
        }
    }
}
