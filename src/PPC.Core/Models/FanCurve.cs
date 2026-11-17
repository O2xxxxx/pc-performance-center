namespace Ppc.Core.Models;

public sealed class FanCurvePoint
{
    public int Temperature { get; set; }
    public int Percent { get; set; }
    public FanCurvePoint() { }
    public FanCurvePoint(int temperature, int percent) { Temperature = temperature; Percent = percent; }
    public FanCurvePoint Clone() => new(Temperature, Percent);
}

/// <summary>
/// Temperature -> fan speed curve. Percent is intentionally clamped to a safe band: the application
/// never drives a fan to 0% because that can defeat the vendor cooling design.
/// </summary>
public sealed class FanCurve
{
    public const int MinSafePercent = 20;
    public const int MaxPercent = 100;
    public const int MinTemperature = 30;
    public const int MaxTemperature = 100;

    // 默认节点作为静态数据存在：如果写成 = Default().Points，Default() 里的 new() 又会触发本初始化器，
    // 造成无限递归（StackOverflow，不可捕获）。这里必须用静态方法克隆，避免经过构造器。
    private static readonly FanCurvePoint[] DefaultPoints =
    {
        new(40, 25), new(50, 35), new(60, 50), new(70, 68), new(80, 85), new(90, 100)
    };

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "自定义曲线";
    public List<FanCurvePoint> Points { get; set; } = CloneDefaultPoints();

    private static List<FanCurvePoint> CloneDefaultPoints()
        => DefaultPoints.Select(p => p.Clone()).ToList();
    public int HysteresisCelsius { get; set; } = 3;
    public int ResponseDelaySeconds { get; set; } = 5;
    public bool Smoothing { get; set; } = true;
    public Metric SensorMetric { get; set; } = Metric.CpuTemperature;

    public static FanCurve Default() => new() { Name = "均衡曲线" };

    public FanCurve Clone() => new()
    {
        Id = Id, Name = Name, HysteresisCelsius = HysteresisCelsius,
        ResponseDelaySeconds = ResponseDelaySeconds, Smoothing = Smoothing,
        SensorMetric = SensorMetric,
        Points = Points.Select(p => p.Clone()).ToList()
    };

    public FanCurve Normalized()
    {
        var clone = Clone();
        clone.Points = clone.Points
            .Select(p => new FanCurvePoint(
                Math.Clamp(p.Temperature, MinTemperature, MaxTemperature),
                Math.Clamp(p.Percent, MinSafePercent, MaxPercent)))
            .OrderBy(p => p.Temperature)
            .GroupBy(p => p.Temperature)
            .Select(g => g.OrderByDescending(p => p.Percent).First())
            .ToList();
        if (clone.Points.Count == 0) clone.Points.Add(new FanCurvePoint(50, 50));
        if (clone.Points.Count == 1) clone.Points.Add(new FanCurvePoint(Math.Min(MaxTemperature, clone.Points[0].Temperature + 10), Math.Min(MaxPercent, clone.Points[0].Percent + 20)));
        clone.HysteresisCelsius = Math.Clamp(clone.HysteresisCelsius, 0, 15);
        clone.ResponseDelaySeconds = Math.Clamp(clone.ResponseDelaySeconds, 1, 60);
        return clone;
    }

    /// <summary>Linear interpolation, clamped outside the outermost points.</summary>
    public int Evaluate(double temperature)
    {
        var pts = Points.OrderBy(p => p.Temperature).ToList();
        if (pts.Count == 0) return 50;
        if (temperature <= pts[0].Temperature) return Math.Clamp(pts[0].Percent, MinSafePercent, MaxPercent);
        if (temperature >= pts[^1].Temperature) return Math.Clamp(pts[^1].Percent, MinSafePercent, MaxPercent);
        for (var i = 0; i < pts.Count - 1; i++)
        {
            var a = pts[i];
            var b = pts[i + 1];
            if (temperature < a.Temperature || temperature > b.Temperature) continue;
            var span = b.Temperature - a.Temperature;
            if (span <= 0) return b.Percent;
            var t = (temperature - a.Temperature) / span;
            return (int)Math.Round(a.Percent + (b.Percent - a.Percent) * t);
        }
        return Math.Clamp(pts[^1].Percent, MinSafePercent, MaxPercent);
    }
}
