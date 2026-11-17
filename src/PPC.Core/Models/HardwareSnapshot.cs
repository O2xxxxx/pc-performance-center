namespace Ppc.Core.Models;

/// <summary>A single reading: either a real value with its source, or an explicit reason why it is unavailable.</summary>
public readonly record struct MetricReading(double? Value, string Source, string? UnavailableReason)
{
    public bool HasValue => Value.HasValue;

    public static MetricReading Ok(double value, string source) => new(value, source, null);
    public static MetricReading Unavailable(string reason, string source = "n/a") => new(null, source, reason);

    public string? Text(string format = "0")
    {
        if (!Value.HasValue) return null;
        var unit = string.Empty;
        return Value.Value.ToString(format) + unit;
    }
}

public sealed class HardwareSnapshot
{
    private readonly Dictionary<Metric, MetricReading> _values = new();

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;
    public IReadOnlyList<MetricReading> Fans { get; init; } = Array.Empty<MetricReading>();
    public IReadOnlyDictionary<Metric, MetricReading> Values => _values;

    public void Set(Metric metric, double? value, string source) => _values[metric] =
        value.HasValue ? MetricReading.Ok(value.Value, source) : MetricReading.Unavailable("传感器未提供数值", source);

    public void SetReading(Metric metric, MetricReading reading) => _values[metric] = reading;
    public void SetUnavailable(Metric metric, string reason, string source = "n/a") => _values[metric] = MetricReading.Unavailable(reason, source);

    public MetricReading this[Metric metric] =>
        _values.TryGetValue(metric, out var reading) ? reading : MetricReading.Unavailable("当前硬件未提供该项数据");

    public double? Get(Metric metric) => this[metric].Value;

    public static HardwareSnapshot Empty { get; } = new();
}

/// <summary>Rolling history used by the charts. Fixed capacity, cheap to append.</summary>
public sealed class HistoryBuffer
{
    private readonly double?[] _buffer;
    private readonly DateTimeOffset[] _stamps;
    private int _start;
    private int _count;

    public HistoryBuffer(int capacity)
    {
        Capacity = Math.Max(2, capacity);
        _buffer = new double?[Capacity];
        _stamps = new DateTimeOffset[Capacity];
    }

    public int Capacity { get; }
    public int Count => _count;

    public void Add(DateTimeOffset stamp, double? value)
    {
        var index = (_start + _count) % Capacity;
        _buffer[index] = value;
        _stamps[index] = stamp;
        if (_count < Capacity) _count++;
        else _start = (_start + 1) % Capacity;
    }

    public IReadOnlyList<(DateTimeOffset Stamp, double? Value)> ToList()
    {
        var list = new List<(DateTimeOffset, double?)>(_count);
        for (var i = 0; i < _count; i++)
        {
            var index = (_start + i) % Capacity;
            list.Add((_stamps[index], _buffer[index]));
        }
        return list;
    }

    public double? Last => _count == 0 ? null : _buffer[(_start + _count - 1) % Capacity];
    public void Clear() { _start = 0; _count = 0; }
}
