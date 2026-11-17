using LibreHardwareMonitor.Hardware;

namespace Ppc.Hardware.Monitoring;

/// <summary>
/// 把 LibreHardwareMonitor 的硬件树（含全部 SubHardware）拍平成一份传感器清单。
/// 每次采样只拍一次，所有指标映射都从这份清单里取，避免反复遍历硬件树。
/// </summary>
internal sealed class SensorInventory
{
    /// <summary>一条传感器记录：它属于哪个硬件、是哪个传感器对象。</summary>
    public readonly record struct SensorEntry(IHardware Hardware, ISensor Sensor);

    /// <summary>一次取到的真实读数，附带来源说明，直接写进 MetricReading.Source。</summary>
    public readonly record struct SensorHit(string HardwareName, string SensorName, double Reading)
    {
        /// <summary>形如 CPU/CPU Package，用于界面与日志里说明数据出处。</summary>
        public string Describe()
            => string.IsNullOrWhiteSpace(HardwareName) ? SensorName : HardwareName + "/" + SensorName;
    }

    private readonly List<SensorEntry> _entries;

    private SensorInventory(List<SensorEntry> entries) => _entries = entries;

    /// <summary>全部传感器（保持硬件树顺序：风扇这类按顺序映射的指标依赖这个顺序）。</summary>
    public IReadOnlyList<SensorEntry> Entries => _entries;

    /// <summary>遍历硬件树（含所有 SubHardware 与多级子节点）生成清单。</summary>
    public static SensorInventory Capture(IEnumerable<IHardware> hardware)
    {
        var entries = new List<SensorEntry>(256);
        foreach (var item in hardware) Collect(item, entries);
        return new SensorInventory(entries);
    }

    private static void Collect(IHardware hardware, List<SensorEntry> sink)
    {
        var sensors = hardware.Sensors;
        if (sensors is { Length: > 0 })
        {
            foreach (var sensor in sensors) sink.Add(new SensorEntry(hardware, sensor));
        }

        var children = hardware.SubHardware;
        if (children is { Length: > 0 })
        {
            foreach (var child in children) Collect(child, sink);
        }
    }

    /// <summary>是否存在某一类硬件节点（CPU / GPU / 内存 / 存储 / 电池 ...）。</summary>
    public bool HasHardware(HardwareType type)
    {
        foreach (var entry in _entries)
        {
            if (entry.Hardware.HardwareType == type) return true;
        }

        return false;
    }

    /// <summary>按“硬件类型 + 传感器类型 + 若干候选名称”取值，候选名称按传入顺序优先。</summary>
    public SensorHit? First(HardwareType type, SensorType sensorType, params string[] sensorNames)
    {
        foreach (var name in sensorNames)
        {
            foreach (var entry in _entries)
            {
                if (entry.Hardware.HardwareType != type) continue;
                if (entry.Sensor.SensorType != sensorType) continue;
                if (!NameEquals(entry.Sensor.Name, name)) continue;
                if (TryValue(entry.Sensor, out var value)) return Create(entry, value);
            }
        }

        return null;
    }

    /// <summary>按自定义条件取第一个有值的传感器（用于名称不完全固定的传感器）。</summary>
    public SensorHit? FirstWhere(HardwareType type, SensorType sensorType, Func<ISensor, bool> predicate)
    {
        foreach (var entry in _entries)
        {
            if (entry.Hardware.HardwareType != type) continue;
            if (entry.Sensor.SensorType != sensorType) continue;
            if (!predicate(entry.Sensor)) continue;
            if (TryValue(entry.Sensor, out var value)) return Create(entry, value);
        }

        return null;
    }

    /// <summary>取满足条件的全部传感器读数（例如各核心频率、各核心温度）。</summary>
    public IReadOnlyList<SensorHit> AllWhere(HardwareType type, SensorType sensorType, Func<ISensor, bool> predicate)
    {
        var hits = new List<SensorHit>();
        foreach (var entry in _entries)
        {
            if (entry.Hardware.HardwareType != type) continue;
            if (entry.Sensor.SensorType != sensorType) continue;
            if (!predicate(entry.Sensor)) continue;
            if (TryValue(entry.Sensor, out var value)) hits.Add(Create(entry, value));
        }

        return hits;
    }

    /// <summary>取任意硬件上的某类传感器（风扇转速不分硬件类型）。</summary>
    public IReadOnlyList<SensorHit> AllOfType(SensorType sensorType)
    {
        var hits = new List<SensorHit>();
        foreach (var entry in _entries)
        {
            if (entry.Sensor.SensorType != sensorType) continue;
            if (TryValue(entry.Sensor, out var value)) hits.Add(Create(entry, value));
        }

        return hits;
    }

    private static SensorHit Create(SensorEntry entry, double reading)
        => new(entry.Hardware.Name ?? string.Empty, entry.Sensor.Name ?? string.Empty, reading);

    private static bool NameEquals(string? actual, string expected)
        => string.Equals(actual?.Trim(), expected, StringComparison.OrdinalIgnoreCase);

    private static bool TryValue(ISensor sensor, out double value)
    {
        value = 0;
        if (sensor.Value is not float raw) return false;
        if (float.IsNaN(raw) || float.IsInfinity(raw)) return false;
        value = raw;
        return true;
    }
}
