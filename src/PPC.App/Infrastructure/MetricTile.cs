using Ppc.Core.Models;

namespace Ppc.App.Infrastructure;

/// <summary>一项硬件指标的展示单元。没有真实数据时显示「不支持」+ 原因，绝不显示占位数值。</summary>
public sealed class MetricTile : ObservableObject
{
    private string _value = "—";
    private string _detail = "";
    private bool _hasValue;
    private double _ratio;
    private string _state = "na";

    public MetricTile(Metric metric, string? title = null, string? unit = null)
    {
        Metric = metric;
        Title = title ?? MetricInfo.DisplayName(metric);
        _unit = unit ?? MetricInfo.Unit(metric);
    }

    public Metric Metric { get; }
    public string Title { get; }

    private string _unit;
    public string Unit { get => _unit; private set => Set(ref _unit, value); }

    public string Value { get => _value; private set => Set(ref _value, value); }
    public string Detail { get => _detail; private set => Set(ref _detail, value); }
    public bool HasValue { get => _hasValue; private set => Set(ref _hasValue, value); }
    public double Ratio { get => _ratio; private set => Set(ref _ratio, value); }
    /// <summary>ok / warn / bad / na</summary>
    public string State { get => _state; private set => Set(ref _state, value); }

    public void Update(MetricReading reading)
    {
        if (!reading.Value.HasValue)
        {
            HasValue = false;
            Value = "不支持";
            Unit = "";
            Detail = reading.UnavailableReason ?? "无法读取";
            Ratio = 0;
            State = "na";
            return;
        }

        var value = reading.Value.Value;
        HasValue = true;
        Value = Format(value);
        Detail = ShortSource(reading.Source);
        Ratio = RatioFor(value);
        State = StateFor(value);
    }

    private static string Format(double value) => Math.Abs(value) switch
    {
        0 => "0",
        > 1000 => value.ToString("0"),
        >= 100 => value.ToString("0"),
        >= 10 => value.ToString("0.0"),
        _ => value.ToString("0.00"),
    };

    /// <summary>把长数据源串缩短成卡片能放下的标注，避免两行折行截断。</summary>
    private static string ShortSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return "";
        if (source.Contains("LibreHardwareMonitor", StringComparison.OrdinalIgnoreCase)) return "LHM 实时采样";
        if (source.Contains("Windows", StringComparison.OrdinalIgnoreCase)) return "系统接口";
        return source.Length <= 12 ? source : source.Substring(0, 12);
    }

    private double RatioFor(double value)
    {
        var unit = Unit;
        if (unit == "%" ) return Math.Clamp(value / 100d, 0, 1);
        if (unit == "\u00B0C" && MetricInfo.IsTemperature(Metric)) return Math.Clamp(value / 100d, 0, 1);
        if (Metric == Metric.CpuTemperature || Metric == Metric.GpuTemperature) return Math.Clamp(value / 100d, 0, 1);
        if (Metric == Metric.DiskTemperature) return Math.Clamp(value / 70d, 0, 1);
        if (Metric == Metric.CpuPower) return Math.Clamp(value / 95d, 0, 1);
        if (Metric == Metric.GpuPower) return Math.Clamp(value / 120d, 0, 1);
        if (Metric == Metric.CpuClock) return Math.Clamp(value / 5200d, 0, 1);
        if (Metric == Metric.GpuClock) return Math.Clamp(value / 2600d, 0, 1);
        if (MetricInfo.IsFan(Metric)) return Math.Clamp(value / 6000d, 0, 1);
        if (Metric == Metric.BatteryPercent) return Math.Clamp(value / 100d, 0, 1);
        return 0;
    }

    private string StateFor(double value)
    {
        if (MetricInfo.IsTemperature(Metric))
            return value >= 92 ? "bad" : value >= 80 ? "warn" : "ok";
        if (Unit == "%") return value >= 92 ? "warn" : "ok";
        return "ok";
    }
}
