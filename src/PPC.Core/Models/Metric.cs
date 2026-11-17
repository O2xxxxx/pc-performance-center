namespace Ppc.Core.Models;

/// <summary>Every value the application can display. Values are always real or explicitly unavailable.</summary>
public enum Metric
{
    CpuTemperature,
    CpuUsage,
    CpuPower,
    CpuClock,
    CpuPackagePowerLimit,
    GpuTemperature,
    GpuUsage,
    GpuPower,
    GpuClock,
    GpuMemoryUsedPercent,
    MemoryUsedPercent,
    MemoryUsedBytes,
    MemoryTotalBytes,
    MemoryAvailableBytes,
    DiskTemperature,
    DiskUsagePercent,
    Fan1Rpm,
    Fan2Rpm,
    Fan3Rpm,
    Fan4Rpm,
    BatteryPercent,
    BatteryRateWatts,
    AcOnline,
}

public static class MetricInfo
{
    public static string Unit(Metric metric) => metric switch
    {
        Metric.CpuTemperature or Metric.GpuTemperature or Metric.DiskTemperature => "\u00B0C",
        Metric.CpuUsage or Metric.GpuUsage or Metric.MemoryUsedPercent
            or Metric.GpuMemoryUsedPercent or Metric.DiskUsagePercent => "%",
        Metric.CpuPower or Metric.GpuPower or Metric.BatteryRateWatts => "W",
        Metric.CpuClock or Metric.GpuClock => "MHz",
        Metric.Fan1Rpm or Metric.Fan2Rpm or Metric.Fan3Rpm or Metric.Fan4Rpm => "RPM",
        Metric.BatteryPercent => "%",
        Metric.AcOnline => "",
        _ => ""
    };

    public static bool IsFan(Metric metric) => metric is Metric.Fan1Rpm or Metric.Fan2Rpm or Metric.Fan3Rpm or Metric.Fan4Rpm;

    public static bool IsTemperature(Metric metric) => metric is Metric.CpuTemperature or Metric.GpuTemperature or Metric.DiskTemperature;

    public static string DisplayName(Metric metric) => metric switch
    {
        Metric.CpuTemperature => "CPU 温度",
        Metric.CpuUsage => "CPU 使用率",
        Metric.CpuPower => "CPU 功耗",
        Metric.CpuClock => "CPU 频率",
        Metric.CpuPackagePowerLimit => "CPU 功耗上限",
        Metric.GpuTemperature => "GPU 温度",
        Metric.GpuUsage => "GPU 使用率",
        Metric.GpuPower => "GPU 功耗",
        Metric.GpuClock => "GPU 频率",
        Metric.GpuMemoryUsedPercent => "显存占用",
        Metric.MemoryUsedPercent => "内存占用",
        Metric.MemoryUsedBytes => "已用内存",
        Metric.MemoryTotalBytes => "内存总量",
        Metric.MemoryAvailableBytes => "可用内存",
        Metric.DiskTemperature => "硬盘温度",
        Metric.DiskUsagePercent => "磁盘占用",
        Metric.Fan1Rpm => "风扇 1 转速",
        Metric.Fan2Rpm => "风扇 2 转速",
        Metric.Fan3Rpm => "风扇 3 转速",
        Metric.Fan4Rpm => "风扇 4 转速",
        Metric.BatteryPercent => "电池电量",
        Metric.BatteryRateWatts => "电池放电功率",
        Metric.AcOnline => "电源适配器",
        _ => metric.ToString()
    };
}
