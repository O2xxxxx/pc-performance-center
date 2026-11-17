using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Ppc.Core.Models;
using Ppc.Core.Services;

namespace Ppc.App.Controls;

/// <summary>
/// 风扇温度曲线编辑器（纯自绘，不依赖任何第三方图表库）：
/// 横轴温度 30–100℃，纵轴转速 20–100%，左键拖动节点、双击/右键空白添加、右键节点删除。
/// 所有改动都直接写回绑定的 <see cref="FanCurve"/>，并把节点夹紧到硬件安全范围
/// （FanCurve.MinSafePercent..100 / MinTemperature..MaxTemperature），绝不出现 0% 转速。
/// </summary>
public partial class FanCurveEditor : UserControl
{
    private const double PadLeft = 46;
    private const double PadRight = 22;
    private const double PadTop = 24;
    private const double PadBottom = 32;
    private const double NodeRadius = 5.5;
    private const double HitRadius = 13;

    private readonly List<Point> _nodePositions = new();
    private int _dragIndex = -1;
    private string _dragLabel = string.Empty;

    public static readonly DependencyProperty CurveProperty = DependencyProperty.Register(
        nameof(Curve),
        typeof(FanCurve),
        typeof(FanCurveEditor),
        new FrameworkPropertyMetadata(
            null,
            FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            OnCurveChanged));

    public static readonly DependencyProperty CurrentTemperatureProperty = DependencyProperty.Register(
        nameof(CurrentTemperature),
        typeof(double?),
        typeof(FanCurveEditor),
        new FrameworkPropertyMetadata(null, OnCurveChanged));

    /// <summary>节点被拖动/增删后触发，宿主可据此刷新「当前温度 → 建议转速」等文本。</summary>
    public event EventHandler? CurveEdited;

    public FanCurveEditor()
    {
        InitializeComponent();

        Plot.MouseLeftButtonDown += OnPlotLeftButtonDown;
        Plot.MouseMove += OnPlotMouseMove;
        Plot.MouseLeftButtonUp += OnPlotLeftButtonUp;
        Plot.MouseRightButtonDown += OnPlotRightButtonDown;
        Plot.LostMouseCapture += (_, _) =>
        {
            if (_dragIndex < 0) return;
            _dragIndex = -1;
            _dragLabel = string.Empty;
            Draw();
        };

        Loaded += (_, _) => Draw();
        SizeChanged += (_, _) => Draw();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) Draw();
        };
    }

    /// <summary>绑定的曲线对象（Points 就是 List&lt;FanCurvePoint&gt;，可直接修改）。</summary>
    public FanCurve? Curve
    {
        get => (FanCurve?)GetValue(CurveProperty);
        set => SetValue(CurveProperty, value);
    }

    /// <summary>当前 CPU 温度（可选，用来画一条参考竖线）。</summary>
    public double? CurrentTemperature
    {
        get => (double?)GetValue(CurrentTemperatureProperty);
        set => SetValue(CurrentTemperatureProperty, value);
    }

    /// <summary>外部替换了点位后，手动要求重绘。</summary>
    public void Refresh() => Draw();

    private static void OnCurveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FanCurveEditor editor) return;
        if (e.Property == CurveProperty) editor.SortPointsInPlace();
        editor.Draw();
    }

    private void SortPointsInPlace()
    {
        try
        {
            var curve = Curve;
            if (curve?.Points is null) return;
            curve.Points.Sort((a, b) => a.Temperature.CompareTo(b.Temperature));
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("整理曲线节点顺序失败", ex);
        }
    }

    private void Draw()
    {
        try
        {
            Plot.Children.Clear();
            _nodePositions.Clear();

            var width = Plot.ActualWidth;
            var height = Plot.ActualHeight;
            var curve = Curve;
            EmptyHint.Visibility = curve is null ? Visibility.Visible : Visibility.Collapsed;
            if (width < 80 || height < 80) return;

            var gridBrush = ResourceBrush("Border.Subtle", Color.FromRgb(0x23, 0x2B, 0x38));
            var strongBrush = ResourceBrush("Border.Strong", Color.FromRgb(0x31, 0x40, 0x52));
            var axisBrush = ResourceBrush("Text.Tertiary", Color.FromRgb(0x6E, 0x7B, 0x8D));
            var accentBrush = ResourceBrush("Accent.Primary", Color.FromRgb(0x4F, 0x8B, 0xFF));
            var fillBrush = ResourceBrush("Accent.Soft", Color.FromRgb(0x1E, 0x2C, 0x47));
            var surfaceBrush = ResourceBrush("Bg.Surface", Color.FromRgb(0x14, 0x19, 0x22));
            var warnBrush = ResourceBrush("State.Warn", Color.FromRgb(0xFF, 0xB5, 0x47));

            DrawGrid(width, height, gridBrush, strongBrush, axisBrush);
            DrawCurve(curve, width, height, accentBrush, fillBrush, surfaceBrush, axisBrush);
            DrawCurrentTemperature(width, height, warnBrush, surfaceBrush, axisBrush);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("绘制风扇曲线失败", ex);
        }
    }

    private void DrawGrid(double width, double height, Brush gridBrush, Brush strongBrush, Brush axisBrush)
    {
        // 横向参考线：转速 20% → 100%，每 10% 一条
        for (var percent = FanCurve.MinSafePercent; percent <= FanCurve.MaxPercent; percent += 10)
        {
            var y = YFor(percent, height);
            var isMajor = percent % 20 == 0;
            Plot.Children.Add(new Line
            {
                X1 = PadLeft, X2 = width - PadRight, Y1 = y, Y2 = y,
                Stroke = isMajor ? strongBrush : gridBrush,
                StrokeThickness = isMajor ? 1 : 0.7,
                Opacity = isMajor ? 0.75 : 0.45,
                IsHitTestVisible = false,
            });

            if (isMajor)
            {
                var label = MakeLabel(percent + "%", 9.5, axisBrush);
                Canvas.SetLeft(label, 6);
                Canvas.SetTop(label, y - 8);
                Plot.Children.Add(label);
            }
        }

        // 纵向参考线：温度 30 → 100℃，每 10℃ 一条
        for (var temperature = FanCurve.MinTemperature; temperature <= FanCurve.MaxTemperature; temperature += 10)
        {
            var x = XFor(temperature, width);
            var isMajor = temperature % 20 == 10 || temperature == FanCurve.MinTemperature || temperature == FanCurve.MaxTemperature;
            Plot.Children.Add(new Line
            {
                X1 = x, X2 = x, Y1 = PadTop, Y2 = height - PadBottom,
                Stroke = isMajor ? strongBrush : gridBrush,
                StrokeThickness = isMajor ? 1 : 0.7,
                Opacity = isMajor ? 0.75 : 0.45,
                IsHitTestVisible = false,
            });

            var label = MakeLabel(temperature.ToString(CultureInfo.InvariantCulture), 9.5, axisBrush);
            Canvas.SetLeft(label, x - 8);
            Canvas.SetTop(label, height - PadBottom + 6);
            Plot.Children.Add(label);
        }

        // 轴标题
        var yTitle = MakeLabel("转速 %", 10, axisBrush);
        Canvas.SetLeft(yTitle, 6);
        Canvas.SetTop(yTitle, 4);
        Plot.Children.Add(yTitle);

        var xTitle = MakeLabel("温度 ℃", 10, axisBrush);
        Canvas.SetLeft(xTitle, width - PadRight - 40);
        Canvas.SetTop(xTitle, height - 18);
        Plot.Children.Add(xTitle);

        // 安全下限提示
        var safeLabel = MakeLabel("安全下限 " + FanCurve.MinSafePercent + "%", 9, axisBrush);
        Canvas.SetLeft(safeLabel, PadLeft + 2);
        Canvas.SetTop(safeLabel, height - PadBottom - 14);
        Plot.Children.Add(safeLabel);
    }

    private void DrawCurve(FanCurve? curve, double width, double height, Brush accentBrush, Brush fillBrush, Brush surfaceBrush, Brush axisBrush)
    {
        if (curve?.Points is null || curve.Points.Count == 0) return;

        var points = curve.Points.OrderBy(p => p.Temperature).ToList();
        var polyline = new Polyline
        {
            Stroke = accentBrush,
            StrokeThickness = 2.2,
            StrokeLineJoin = PenLineJoin.Round,
            IsHitTestVisible = false,
        };

        var area = new Polygon { Fill = fillBrush, Opacity = 0.55, IsHitTestVisible = false };

        foreach (var point in points)
        {
            var position = new Point(XFor(point.Temperature, width), YFor(point.Percent, height));
            polyline.Points.Add(position);
            area.Points.Add(position);
            _nodePositions.Add(position);
        }

        if (polyline.Points.Count >= 2)
        {
            area.Points.Add(new Point(polyline.Points[^1].X, YFor(FanCurve.MinSafePercent, height)));
            area.Points.Add(new Point(polyline.Points[0].X, YFor(FanCurve.MinSafePercent, height)));
            Plot.Children.Add(area);
        }

        Plot.Children.Add(polyline);

        // 节点 + 数值
        for (var index = 0; index < points.Count; index++)
        {
            var point = points[index];
            var position = _nodePositions[index];
            var isDragging = index == _dragIndex;

            var dot = new Ellipse
            {
                Width = NodeRadius * 2,
                Height = NodeRadius * 2,
                Fill = isDragging ? accentBrush : surfaceBrush,
                Stroke = accentBrush,
                StrokeThickness = 2,
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(dot, position.X - NodeRadius);
            Canvas.SetTop(dot, position.Y - NodeRadius);
            Plot.Children.Add(dot);

            var valueLabel = MakeLabel(point.Percent + "%", 9.5, isDragging ? accentBrush : axisBrush);
            Canvas.SetLeft(valueLabel, position.X - 9);
            Canvas.SetTop(valueLabel, position.Y - 20);
            Plot.Children.Add(valueLabel);

            var tempLabel = MakeLabel(point.Temperature + "℃", 9, axisBrush);
            Canvas.SetLeft(tempLabel, position.X - 11);
            Canvas.SetTop(tempLabel, position.Y + 8);
            Plot.Children.Add(tempLabel);
        }

        // 拖动中的实时数值气泡
        if (_dragIndex >= 0 && !string.IsNullOrEmpty(_dragLabel) && _dragIndex < _nodePositions.Count)
        {
            var pos = _nodePositions[_dragIndex];
            var bubble = new Border
            {
                Background = ResourceBrush("Bg.Selected", Color.FromRgb(0x24, 0x30, 0x4A)),
                BorderBrush = accentBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(6, 2, 6, 2),
                IsHitTestVisible = false,
                Child = MakeLabel(_dragLabel, 10, accentBrush),
            };
            bubble.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var left = Math.Clamp(pos.X + 12, PadLeft, Math.Max(PadLeft, width - bubble.DesiredSize.Width - 4));
            var top = Math.Clamp(pos.Y - bubble.DesiredSize.Height - 12, 2, height - bubble.DesiredSize.Height - 2);
            Canvas.SetLeft(bubble, left);
            Canvas.SetTop(bubble, top);
            Plot.Children.Add(bubble);
        }
    }

    private void DrawCurrentTemperature(double width, double height, Brush warnBrush, Brush surfaceBrush, Brush axisBrush)
    {
        if (CurrentTemperature is not { } temperature) return;

        var x = XFor(temperature, width);
        Plot.Children.Add(new Line
        {
            X1 = x, X2 = x, Y1 = PadTop, Y2 = height - PadBottom,
            Stroke = warnBrush,
            StrokeThickness = 1.2,
            StrokeDashArray = new DoubleCollection { 4, 3 },
            IsHitTestVisible = false,
        });

        var label = new Border
        {
            Background = surfaceBrush,
            Padding = new Thickness(4, 1, 4, 1),
            CornerRadius = new CornerRadius(4),
            IsHitTestVisible = false,
            Child = MakeLabel("现在 " + temperature.ToString("0.0", CultureInfo.InvariantCulture) + "℃", 9.5, warnBrush),
        };
        label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var left = Math.Clamp(x + 4, PadLeft, Math.Max(PadLeft, width - label.DesiredSize.Width - 4));
        Canvas.SetLeft(label, left);
        Canvas.SetTop(label, PadTop - 2);
        Plot.Children.Add(label);
    }

    private static TextBlock MakeLabel(string text, double size, Brush brush) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = brush,
        IsHitTestVisible = false,
        SnapsToDevicePixels = true,
    };

    private void OnPlotLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Curve is null) return;
        var position = e.GetPosition(Plot);
        var index = HitTestNode(position);

        if (index < 0)
        {
            // 双击空白处添加节点
            if (e.ClickCount == 2)
            {
                AddPoint(position);
                e.Handled = true;
            }
            return;
        }

        _dragIndex = index;
        Plot.CaptureMouse();
        UpdateDrag(position);
        e.Handled = true;
    }

    private void OnPlotMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragIndex < 0 || !Plot.IsMouseCaptured) return;
        UpdateDrag(e.GetPosition(Plot));
        e.Handled = true;
    }

    private void OnPlotLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragIndex < 0) return;
        _dragIndex = -1;
        _dragLabel = string.Empty;
        if (Plot.IsMouseCaptured) Plot.ReleaseMouseCapture();
        Draw();
        CurveEdited?.Invoke(this, EventArgs.Empty);
        e.Handled = true;
    }

    private void OnPlotRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var curve = Curve;
        if (curve?.Points is null) return;

        var position = e.GetPosition(Plot);
        var index = HitTestNode(position);
        if (index < 0)
        {
            AddPoint(position);
            e.Handled = true;
            return;
        }

        if (curve.Points.Count <= 2)
        {
            // 至少保留两个节点，曲线才有意义
            AppLog.Instance.Warn("风扇曲线至少需要两个节点，已忽略删除操作");
            e.Handled = true;
            return;
        }

        if (index < curve.Points.Count)
        {
            var removed = curve.Points[index];
            curve.Points.RemoveAt(index);
            _dragIndex = -1;
            Draw();
            CurveEdited?.Invoke(this, EventArgs.Empty);
            AppLog.Instance.Info($"删除曲线节点：{removed.Temperature}℃ / {removed.Percent}%");
        }
        e.Handled = true;
    }

    private void AddPoint(Point position)
    {
        var curve = Curve;
        if (curve?.Points is null) return;

        var width = Math.Max(1, Plot.ActualWidth);
        var height = Math.Max(1, Plot.ActualHeight);
        var temperature = (int)Math.Round(TempFromX(position.X, width));
        temperature = Math.Clamp(temperature, FanCurve.MinTemperature, FanCurve.MaxTemperature);
        var percent = (int)Math.Round(PercentFromY(position.Y, height));
        percent = Math.Clamp(percent, FanCurve.MinSafePercent, FanCurve.MaxPercent);

        var existing = curve.Points.FirstOrDefault(p => p.Temperature == temperature);
        if (existing is not null)
        {
            existing.Percent = percent;
        }
        else
        {
            curve.Points.Add(new FanCurvePoint(temperature, percent));
            curve.Points.Sort((a, b) => a.Temperature.CompareTo(b.Temperature));
        }

        Draw();
        CurveEdited?.Invoke(this, EventArgs.Empty);
        AppLog.Instance.Info($"曲线节点已设为：{temperature}℃ / {percent}%");
    }

    private void UpdateDrag(Point position)
    {
        var curve = Curve;
        if (curve?.Points is null) return;
        if (_dragIndex < 0 || _dragIndex >= curve.Points.Count) return;

        var width = Math.Max(1, Plot.ActualWidth);
        var height = Math.Max(1, Plot.ActualHeight);
        var points = curve.Points;
        var current = points[_dragIndex];

        // 左右不能越过相邻节点，上下夹紧到安全范围
        var lower = _dragIndex > 0 ? points[_dragIndex - 1].Temperature : FanCurve.MinTemperature;
        var upper = _dragIndex < points.Count - 1 ? points[_dragIndex + 1].Temperature : FanCurve.MaxTemperature;

        var temperature = (int)Math.Round(TempFromX(position.X, width));
        temperature = Math.Clamp(temperature, lower, Math.Max(lower, upper));
        var percent = (int)Math.Round(PercentFromY(position.Y, height));
        percent = Math.Clamp(percent, FanCurve.MinSafePercent, FanCurve.MaxPercent);

        if (current.Temperature == temperature && current.Percent == percent) return;

        current.Temperature = temperature;
        current.Percent = percent;
        _dragLabel = $"{temperature}℃ · {percent}%";
        Draw();
    }

    private int HitTestNode(Point position)
    {
        for (var i = 0; i < _nodePositions.Count; i++)
        {
            var dx = _nodePositions[i].X - position.X;
            var dy = _nodePositions[i].Y - position.Y;
            if (dx * dx + dy * dy <= HitRadius * HitRadius) return i;
        }
        return -1;
    }

    private double XFor(double temperature, double width)
    {
        var span = FanCurve.MaxTemperature - FanCurve.MinTemperature;
        var clamped = Math.Clamp(temperature, FanCurve.MinTemperature, FanCurve.MaxTemperature);
        var usable = Math.Max(1, width - PadLeft - PadRight);
        return PadLeft + (clamped - FanCurve.MinTemperature) / span * usable;
    }

    private double YFor(double percent, double height)
    {
        var span = FanCurve.MaxPercent - FanCurve.MinSafePercent;
        var clamped = Math.Clamp(percent, FanCurve.MinSafePercent, FanCurve.MaxPercent);
        var usable = Math.Max(1, height - PadTop - PadBottom);
        return PadTop + (1 - (clamped - FanCurve.MinSafePercent) / span) * usable;
    }

    private double TempFromX(double x, double width)
    {
        var usable = Math.Max(1, width - PadLeft - PadRight);
        var ratio = (x - PadLeft) / usable;
        return FanCurve.MinTemperature + ratio * (FanCurve.MaxTemperature - FanCurve.MinTemperature);
    }

    private double PercentFromY(double y, double height)
    {
        var usable = Math.Max(1, height - PadTop - PadBottom);
        var ratio = (y - PadTop) / usable;
        return FanCurve.MinSafePercent + (1 - ratio) * (FanCurve.MaxPercent - FanCurve.MinSafePercent);
    }

    /// <summary>颜色一律来自主题资源；取不到时才用兜底色，绝不写死在 XAML 里。</summary>
    private Brush ResourceBrush(string key, Color fallback)
    {
        try
        {
            if (TryFindResource(key) is Brush local) return local;
            if (Application.Current?.TryFindResource(key) is Brush app) return app;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("读取主题画刷失败：" + key + " · " + ex.Message);
        }

        var brush = new SolidColorBrush(fallback);
        brush.Freeze();
        return brush;
    }
}
