using System.Windows;
using System.Windows.Media;

namespace Ppc.App.Controls
{
    /// <summary>
    /// 圆角横向占比条。Value 取 0..1，超出范围自动夹紧；null 不适用（由页面绑定转换处理）。
    /// 纯 OnRender 绘制，尺寸为 0 或数值非法时什么也不画。
    /// </summary>
    public sealed class LevelBar : FrameworkElement
    {
        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
            nameof(Value), typeof(double), typeof(LevelBar),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
            nameof(Fill), typeof(Brush), typeof(LevelBar),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
            nameof(Track), typeof(Brush), typeof(LevelBar),
            new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty RadiusProperty = DependencyProperty.Register(
            nameof(Radius), typeof(double), typeof(LevelBar),
            new FrameworkPropertyMetadata(4d, FrameworkPropertyMetadataOptions.AffectsRender));

        /// <summary>占比，0..1。</summary>
        public double Value
        {
            get => (double)GetValue(ValueProperty);
            set => SetValue(ValueProperty, value);
        }

        /// <summary>填充画刷；为空时取主题 Accent.Brush。</summary>
        public Brush? Fill
        {
            get => (Brush?)GetValue(FillProperty);
            set => SetValue(FillProperty, value);
        }

        /// <summary>轨道画刷；为空时取主题 Slider.Track。</summary>
        public Brush? Track
        {
            get => (Brush?)GetValue(TrackProperty);
            set => SetValue(TrackProperty, value);
        }

        /// <summary>圆角半径；默认 4，超过一半高度时自动取半高（胶囊形）。</summary>
        public double Radius
        {
            get => (double)GetValue(RadiusProperty);
            set => SetValue(RadiusProperty, value);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            double w = double.IsInfinity(availableSize.Width) ? 120d : availableSize.Width;
            double h = double.IsInfinity(availableSize.Height) ? 6d : availableSize.Height;
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
                // 绘制异常不得冒泡到 UI 线程。
            }
        }

        private void RenderCore(DrawingContext dc)
        {
            double w = ActualWidth;
            double h = ActualHeight;
            if (double.IsNaN(w) || double.IsNaN(h) || w <= 0.5d || h <= 0.5d)
            {
                return;
            }

            Brush track = Track ?? ResolveBrush("Slider.Track", new SolidColorBrush(Color.FromRgb(0x1C, 0x25, 0x31)));
            Brush fill = Fill ?? ResolveBrush("Accent.Brush", Brushes.DodgerBlue);

            double radius = Radius;
            if (double.IsNaN(radius) || double.IsInfinity(radius) || radius < 0d)
            {
                radius = h / 2d;
            }

            radius = Math.Min(radius, h / 2d);

            dc.DrawRoundedRectangle(track, null, new Rect(0d, 0d, w, h), radius, radius);

            double ratio = Value;
            if (double.IsNaN(ratio) || double.IsInfinity(ratio))
            {
                return;
            }

            ratio = Math.Clamp(ratio, 0d, 1d);
            double fillWidth = w * ratio;
            if (fillWidth <= 0.35d)
            {
                return;
            }

            // 短条时收窄圆角，避免出现「两头圆中间空」的难看形状。
            double fillRadius = Math.Min(radius, Math.Min(fillWidth / 2d, h / 2d));
            dc.DrawRoundedRectangle(fill, null, new Rect(0d, 0d, fillWidth, h), fillRadius, fillRadius);
        }

        private Brush ResolveBrush(string key, Brush fallback)
        {
            object? found = TryFindResource(key);
            return found as Brush ?? fallback;
        }
    }
}
