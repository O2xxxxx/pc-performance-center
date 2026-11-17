using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Ppc.App.Controls
{
    /// <summary>
    /// 环形仪表盘：270° 圆弧 + 圆头笔触 + 中心数值。
    /// 纯 OnRender 绘制，不引入任何模板或第三方依赖；所有数据异常（null / NaN / 空尺寸）都安全降级。
    /// </summary>
    public sealed class GaugeRing : FrameworkElement
    {
        // 起始角 135°（左下），顺时针扫 270°，缺口留在正下方。
        private const double StartAngle = 135d;
        private const double SweepAngle = 270d;

        private static readonly Typeface ValueTypeface = new(
            new FontFamily("Segoe UI Variable Display, Segoe UI, Microsoft YaHei UI"),
            FontStyles.Normal,
            FontWeights.SemiBold,
            FontStretches.Normal);

        private static readonly Typeface LabelTypeface = new(
            new FontFamily("Microsoft YaHei UI, Segoe UI"),
            FontStyles.Normal,
            FontWeights.Normal,
            FontStretches.Normal);

        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
            nameof(Value), typeof(double?), typeof(GaugeRing),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
            nameof(Minimum), typeof(double), typeof(GaugeRing),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
            nameof(Maximum), typeof(double), typeof(GaugeRing),
            new FrameworkPropertyMetadata(100d, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(
            nameof(Unit), typeof(string), typeof(GaugeRing),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
            nameof(Label), typeof(string), typeof(GaugeRing),
            new FrameworkPropertyMetadata(string.Empty, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty AccentProperty = DependencyProperty.Register(
            nameof(Accent), typeof(Brush), typeof(GaugeRing),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
            nameof(Track), typeof(Brush), typeof(GaugeRing),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
            nameof(Thickness), typeof(double), typeof(GaugeRing),
            new FrameworkPropertyMetadata(9d, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ValueTextProperty = DependencyProperty.Register(
            nameof(ValueText), typeof(string), typeof(GaugeRing),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>当前值；null 表示暂无数据，中心显示「—」。</summary>
        public double? Value
        {
            get => (double?)GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        /// <summary>量程下限。</summary>
        public double Minimum
        {
            get => (double)GetValue(MinimumProperty);
            set => SetValue(MinimumProperty, value);
        }

        /// <summary>量程上限。</summary>
        public double Maximum
        {
            get => (double)GetValue(MaximumProperty);
            set => SetValue(MaximumProperty, value);
        }

        /// <summary>单位，例如 °C、%、RPM。</summary>
        public string Unit
        {
            get => (string)GetValue(UnitProperty);
            set => SetValue(UnitProperty, value);
        }

        /// <summary>中心下方的说明文字。</summary>
        public string Label
        {
            get => (string)GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }

        /// <summary>进度弧画刷；为空时取主题里的 Accent.Brush。</summary>
        public Brush? Accent
        {
            get => (Brush?)GetValue(AccentProperty);
            set => SetValue(AccentProperty, value);
        }

        /// <summary>轨道画刷；为空时取主题里的 Slider.Track。</summary>
        public Brush? Track
        {
            get => (Brush?)GetValue(TrackProperty);
            set => SetValue(TrackProperty, value);
        }

        /// <summary>弧线粗细（像素）。</summary>
        public double Thickness
        {
            get => (double)GetValue(ThicknessProperty);
            set => SetValue(ThicknessProperty, value);
        }

        /// <summary>自定义中心文本（例如「—」「5.2 GHz」）；为空时按 Value + Unit 自动格式化。</summary>
        public string? ValueText
        {
            get => (string?)GetValue(ValueTextProperty);
            set => SetValue(ValueTextProperty, value);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double w = double.IsInfinity(availableSize.Width) ? 160d : availableSize.Width;
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
            catch (Exception)
            {
                // 绘制阶段绝不允许把异常抛回 UI 线程：最坏情况是控件空白。
                // 数据本身的问题已在 RenderCore 内部逐项校验。
            }
        }

        private void RenderCore(DrawingContext dc)
        {
            double w = ActualWidth;
            double h = ActualHeight;
            if (double.IsNaN(w) || double.IsNaN(h) || w <= 4d || h <= 4d)
            {
                return;
            }

            double size = Math.Min(w, h);
            double thickness = NormalizeThickness(Thickness, size);
            double radius = (size - thickness) / 2d - 1d;
            if (radius <= 1d)
            {
                return;
            }

            var center = new Point(w / 2d, h / 2d);
            Brush accent = Accent ?? ResolveBrush("Accent.Brush", Brushes.DodgerBlue);
            Brush track = Track ?? ResolveBrush("Slider.Track", new SolidColorBrush(Color.FromRgb(0x1C, 0x25, 0x31)));

            // 1. 轨道
            DrawArc(dc, MakePen(track, thickness), center, radius, StartAngle, SweepAngle);

            // 2. 进度（含外层柔光）
            double ratio = ComputeRatio();
            double sweep = SweepAngle * ratio;
            if (sweep > 0.5d)
            {
                Brush glow = MakeGlow(accent);
                DrawArc(dc, MakePen(glow, thickness * 1.9d), center, radius, StartAngle, sweep);
                DrawArc(dc, MakePen(accent, thickness), center, radius, StartAngle, sweep);

                // 3. 末端圆点，强调当前读数落点
                Point tip = PointOnCircle(center, radius, StartAngle + sweep);
                dc.DrawEllipse(accent, null, tip, thickness * 0.42d, thickness * 0.42d);
            }

            // 4. 空心内圈：极淡的一圈描边，让中心文字与轨道之间有层次
            double innerRadius = radius - thickness * 0.86d;
            if (innerRadius > 1d)
            {
                Pen innerPen = MakePen(WithOpacity(track, 0.55d), 1d);
                dc.DrawEllipse(null, innerPen, center, innerRadius, innerRadius);
            }

            DrawCenterText(dc, center, size, track);
        }

        private void DrawCenterText(DrawingContext dc, Point center, double size, Brush track)
        {
            double pixelsPerDip = GetPixelsPerDip();
            Brush primary = ResolveBrush("Text.Primary", Brushes.White);
            Brush tertiary = ResolveBrush("Text.Tertiary", Brushes.Gray);

            string? custom = ValueText;
            bool useCustom = !string.IsNullOrWhiteSpace(custom);

            string mainText;
            string unitText;
            if (useCustom)
            {
                mainText = custom!.Trim();
                string unit = (Unit ?? string.Empty).Trim();
                // 调用方若已在自定义文本里写了单位就不重复追加。
                unitText = unit.Length > 0 && !mainText.Contains(unit, StringComparison.Ordinal) ? unit : string.Empty;
            }
            else
            {
                mainText = FormatValue(Value);
                unitText = (Unit ?? string.Empty).Trim();
            }

            string label = (Label ?? string.Empty).Trim();

            double maxTextWidth = Math.Max(8d, size * 0.58d);
            double labelMaxWidth = Math.Max(8d, size * 0.80d);
            double valueSize = Math.Clamp(size * 0.21d, 12d, 46d);

            var valueFt = CreateText(mainText, ValueTypeface, valueSize, primary, pixelsPerDip, maxTextWidth);
            // 文字超宽时按比例缩一号，避免溢出圆环内圈。
            if (valueFt.Width > maxTextWidth && valueFt.Width > 0d)
            {
                valueSize = Math.Max(9d, valueSize * (maxTextWidth / valueFt.Width));
                valueFt = CreateText(mainText, ValueTypeface, valueSize, primary, pixelsPerDip, maxTextWidth);
            }

            FormattedText? unitFt = null;
            if (unitText.Length > 0)
            {
                unitFt = CreateText(unitText, LabelTypeface, Math.Max(9d, valueSize * 0.42d), tertiary, pixelsPerDip, maxTextWidth * 0.5d);
            }

            FormattedText? labelFt = null;
            if (label.Length > 0)
            {
                labelFt = CreateText(label, LabelTypeface, Math.Clamp(size * 0.072d, 9d, 12d), tertiary, pixelsPerDip, labelMaxWidth);
            }

            double gap = unitFt is null ? 0d : Math.Max(2d, valueSize * 0.12d);
            double lineWidth = valueFt.Width + gap + (unitFt?.Width ?? 0d);
            double blockHeight = valueFt.Height + (labelFt is null ? 0d : 2d + labelFt.Height);
            double top = center.Y - blockHeight / 2d;
            double lineLeft = center.X - lineWidth / 2d;

            dc.DrawText(valueFt, new Point(lineLeft, top));
            if (unitFt is not null)
            {
                // 单位与数字底部对齐
                dc.DrawText(unitFt, new Point(lineLeft + valueFt.Width + gap, top + valueFt.Height - unitFt.Height));
            }

            if (labelFt is not null)
            {
                dc.DrawText(labelFt, new Point(center.X - labelFt.Width / 2d, top + valueFt.Height + 2d));
            }
        }

        private double ComputeRatio()
        {
            double? raw = Value;
            if (raw is null)
            {
                return 0d;
            }

            double value = raw.Value;
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return 0d;
            }

            double max = Maximum;
            double min = Minimum;
            if (double.IsNaN(max) || double.IsNaN(min) || double.IsInfinity(max) || double.IsInfinity(min) || max <= min)
            {
                return 0d;
            }

            double ratio = (value - min) / (max - min);
            if (double.IsNaN(ratio) || double.IsInfinity(ratio))
            {
                return 0d;
            }

            return Math.Clamp(ratio, 0d, 1d);
        }

        private static string FormatValue(double? value)
        {
            if (value is null)
            {
                return "—";
            }

            double v = value.Value;
            if (double.IsNaN(v) || double.IsInfinity(v))
            {
                return "—";
            }

            return v.ToString("0.#", CultureInfo.CurrentCulture);
        }

        private static FormattedText CreateText(string text, Typeface typeface, double size, Brush brush, double pixelsPerDip, double maxWidth)
        {
            var ft = new FormattedText(
                text,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                typeface,
                size,
                brush,
                pixelsPerDip)
            {
                MaxTextWidth = Math.Max(1d, maxWidth),
                MaxLineCount = 1,
                Trimming = TextTrimming.CharacterEllipsis,
            };

            return ft;
        }

        private static double NormalizeThickness(double thickness, double size)
        {
            if (double.IsNaN(thickness) || double.IsInfinity(thickness) || thickness <= 0d)
            {
                thickness = 9d;
            }

            // 留出中心文字空间：最多占半径的 40%
            double max = Math.Max(2d, size * 0.2d);
            return Math.Clamp(thickness, 2d, max);
        }

        private static Pen MakePen(Brush brush, double thickness)
        {
            return new Pen(brush, Math.Max(0.5d, thickness))
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            };
        }

        private static void DrawArc(DrawingContext dc, Pen pen, Point center, double radius, double startAngle, double sweep)
        {
            if (radius <= 0d || pen.Thickness <= 0d || double.IsNaN(sweep) || double.IsInfinity(sweep) || Math.Abs(sweep) < 0.05d)
            {
                return;
            }

            if (Math.Abs(sweep) >= 359.99d)
            {
                dc.DrawEllipse(null, pen, center, radius, radius);
                return;
            }

            Point start = PointOnCircle(center, radius, startAngle);
            Point end = PointOnCircle(center, radius, startAngle + sweep);

            var geometry = new StreamGeometry();
            using (StreamGeometryContext ctx = geometry.Open())
            {
                ctx.BeginFigure(start, isFilled: false, isClosed: false);
                ctx.ArcTo(
                    end,
                    new Size(radius, radius),
                    rotationAngle: 0d,
                    isLargeArc: Math.Abs(sweep) > 180d,
                    sweepDirection: sweep >= 0d ? SweepDirection.Clockwise : SweepDirection.Counterclockwise,
                    isStroked: true,
                    isSmoothJoin: true);
            }

            geometry.Freeze();
            dc.DrawGeometry(null, pen, geometry);
        }

        private static Point PointOnCircle(Point center, double radius, double angleDegrees)
        {
            double rad = angleDegrees * Math.PI / 180d;
            return new Point(center.X + radius * Math.Cos(rad), center.Y + radius * Math.Sin(rad));
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
                double dpiScale = VisualTreeHelper.GetDpi(this).PixelsPerDip;
                return dpiScale > 0d ? dpiScale : 1d;
            }
            catch (Exception)
            {
                return 1d;
            }
        }

        private static Brush MakeGlow(Brush accent)
        {
            switch (accent)
            {
                case SolidColorBrush solid:
                {
                    Color c = solid.Color;
                    var glow = new SolidColorBrush(Color.FromArgb(40, c.R, c.G, c.B));
                    glow.Freeze();
                    return glow;
                }
                default:
                {
                    Brush clone = accent.CloneCurrentValue();
                    clone.Opacity = 0.22d;
                    if (clone.CanFreeze)
                    {
                        clone.Freeze();
                    }

                    return clone;
                }
            }
        }

        private static Brush WithOpacity(Brush source, double opacity)
        {
            switch (source)
            {
                case SolidColorBrush solid:
                {
                    Color c = solid.Color;
                    var brush = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * Math.Clamp(opacity, 0d, 1d)), c.R, c.G, c.B));
                    brush.Freeze();
                    return brush;
                }
                default:
                {
                    Brush clone = source.CloneCurrentValue();
                    clone.Opacity = Math.Clamp(opacity, 0d, 1d);
                    if (clone.CanFreeze)
                    {
                        clone.Freeze();
                    }

                    return clone;
                }
            }
        }
    }
}
