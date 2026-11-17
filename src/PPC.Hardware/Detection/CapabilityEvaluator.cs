using System.Globalization;
using Ppc.Core.Models;

namespace Ppc.Hardware.Detection;

/// <summary>
/// 能力评估：把「WMI 探测 + 一次监控采样 + powercfg 查询」翻译成 CapabilityItem 列表。
/// 每个 Key 固定不变（UI/设置页按 Key 取值），Detail 一律用中文写清「支持 / 不支持的具体原因」。
/// 这里只做只读判断，不写任何硬件。
/// </summary>
internal static class CapabilityEvaluator
{
    internal static List<CapabilityItem> Evaluate(DeviceProfile profile, LenovoProbeFacts lenovo, MonitoringProbe monitoring)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(lenovo);

        var snapshot = monitoring.Snapshot;
        var items = new List<CapabilityItem>(12);

        var cpuTemp = snapshot[Metric.CpuTemperature];
        var cpuUsage = snapshot[Metric.CpuUsage];
        var cpuClock = snapshot[Metric.CpuClock];
        var cpuOk = cpuTemp.HasValue || cpuUsage.HasValue;
        items.Add(new CapabilityItem("monitor.cpu", "CPU 监控", cpuOk, cpuOk
            ? $"支持：CPU 温度 {Num(cpuTemp, "F1", "°C")}、负载 {Num(cpuUsage, "F0", "%")}" +
              (cpuClock.HasValue ? $"、频率 {Num(cpuClock, "F0", " MHz")}" : string.Empty) +
              $"（来源：{SourceOf(cpuTemp, cpuUsage, cpuClock)}）"
            : "不支持：读不到 CPU 温度与负载，监控后端没有可用的 CPU 传感器。原因：" + MissingReason(cpuTemp, monitoring)));

        var gpuTemp = snapshot[Metric.GpuTemperature];
        var gpuUsage = snapshot[Metric.GpuUsage];
        var gpuPower = snapshot[Metric.GpuPower];
        var gpuOk = gpuTemp.HasValue;
        items.Add(new CapabilityItem("monitor.gpu", "GPU 监控", gpuOk, gpuOk
            ? $"支持：GPU 温度 {Num(gpuTemp, "F1", "°C")}" +
              (gpuUsage.HasValue ? $"、负载 {Num(gpuUsage, "F0", "%")}" : string.Empty) +
              (gpuPower.HasValue ? $"、功耗 {Num(gpuPower, "F1", " W")}" : string.Empty) +
              $"（来源：{SourceOf(gpuTemp, gpuUsage, gpuPower)}）"
            : "不支持：读不到 GPU 温度。原因：" + MissingReason(gpuTemp, monitoring)));

        var fan1 = snapshot[Metric.Fan1Rpm];
        var fanOk = fan1.HasValue;
        items.Add(new CapabilityItem("monitor.fan", "风扇转速监控", fanOk, fanOk
            ? $"支持：风扇 1 转速 {Num(fan1, "F0", " RPM")}（来源：{SourceOf(fan1)}）"
            : "不支持：读不到风扇转速。原因：" + MissingReason(fan1, monitoring) +
              "；Windows 没有统一的读风扇转速接口，只有固件或厂商驱动主动曝光时才能读到，风扇转速通常由嵌入式控制器闭环控制、不经 ACPI/WMI 上报"));

        var fanControlOk = lenovo.GameZone.Exists && lenovo.FanCountCallOk;
        string fanControlDetail;
        if (fanControlOk)
        {
            fanControlDetail = "支持：联想 GameZone 接口的只读方法调用成功，" +
                               "可尝试用 SetSmartFanMode / SetFanCooling 写入风扇策略（写入前会校验返回值，失败立即还原为自动控制）";
        }
        else if (lenovo.GameZone.Exists)
        {
            fanControlDetail = $"不支持：检测到联想 GameZone WMI 接口（{lenovo.GameZone.Detail}），但只读探针调用失败 —— {lenovo.FanCountCallMessage}。" +
                               "Windows 没有通用风扇控制 API，未找到其他官方接口，因此本程序只显示风扇状态、不提供转速控制滑块";
        }
        else
        {
            fanControlDetail = "不支持：未检测到联想 GameZone / 风扇表等厂商 WMI 接口，Windows 也没有通用风扇控制 API。" +
                               "本程序保留风扇监控，但不提供无效的转速滑块" +
                               (lenovo.FanTable.Exists ? $"；{lenovo.FanTable.Detail}" : "；" + lenovo.FanTable.Detail);
        }
        items.Add(new CapabilityItem("control.fan", "风扇转速控制", fanControlOk, fanControlDetail));

        var power = PowerCfgProbe.Collect(profile.IsElevated);
        items.Add(new CapabilityItem("control.powerplan", "Windows 电源方案", power.Available, power.Available
            ? $"支持：powercfg 可执行，当前方案「{power.ActiveSchemeName}」{GuidText(power.ActiveSchemeGuid)}" +
              (power.SchemeCount > 0 ? $"，系统共 {power.SchemeCount} 个电源方案" : string.Empty) +
              "；可切换电源方案与 Windows 电源模式"
            : "不支持：无法执行 powercfg —— " + power.UnavailableReason));

        items.Add(new CapabilityItem("control.processor", "处理器性能状态", power.HasProcessorMaxState,
            power.HasProcessorMaxState
                ? $"支持：当前电源方案暴露 PROCTHROTTLEMAX（最大处理器性能状态 {power.ProcessorStateNote}），" +
                  $"可写 5%–100%；{ElevationHint(profile.IsElevated)}"
                : $"不支持：当前电源方案没有 PROCTHROTTLEMAX 设置项（{power.ProcessorStateNote}），无法限制处理器最大性能状态"));

        items.Add(new CapabilityItem("control.boost", "睿频策略", power.HasBoostMode,
            power.HasBoostMode
                ? $"支持：当前电源方案暴露 PERFBOOSTMODE（处理器性能提升模式 {power.BoostModeNote}），" +
                  $"取值 0=禁用 1=启用 2=高效；{ElevationHint(profile.IsElevated)}"
                : $"不支持：当前电源方案没有 PERFBOOSTMODE 设置项（{power.BoostModeNote}），无法切换睿频策略"));

        var lenovoModeOk = profile.LenovoGameZonePresent && lenovo.GameZone.InstanceCount > 0;
        items.Add(new CapabilityItem("control.lenovo.mode", "联想性能模式", lenovoModeOk, lenovoModeOk
            ? $"支持检测：已取到联想 GameZone 接口实例（{lenovo.GameZone.InstancePath}）。" +
              $"仅在支持的联想机型可用；本机只读探针结果：{lenovo.FanCountCallMessage}。" +
              "写入前运行时会再校验一次，校验失败会自动禁用并显示原因"
            : "不支持：没有取到联想 GameZone 接口实例，无法读取或切换联想性能模式（智能散热/野兽模式等）"));

        items.Add(new CapabilityItem("control.gpu", "GPU 性能策略", false,
            "不支持：调整 GPU 功耗上限或核心频率需要 NVIDIA NVAPI / AMD ADLX 官方 SDK，本项目未集成；" +
            "GPU 性能目前完全由显卡驱动与系统电源策略决定"));

        var memory = snapshot[Metric.MemoryUsedPercent];
        items.Add(new CapabilityItem("monitor.memory", "内存监控", memory.HasValue, memory.HasValue
            ? $"支持：内存占用 {Num(memory, "F1", "%")}（来源：{SourceOf(memory)}）"
            : "不支持：读不到内存占用。原因：" + MissingReason(memory, monitoring)));

        var diskTemp = snapshot[Metric.DiskTemperature];
        var diskUsage = snapshot[Metric.DiskUsagePercent];
        var diskOk = diskUsage.HasValue || diskTemp.HasValue;
        items.Add(new CapabilityItem("monitor.disk", "硬盘监控", diskOk, diskOk
            ? "支持：" + (diskUsage.HasValue ? $"磁盘占用 {Num(diskUsage, "F1", "%")}" : string.Empty) +
              (diskUsage.HasValue && diskTemp.HasValue ? "、" : string.Empty) +
              (diskTemp.HasValue ? $"硬盘温度 {Num(diskTemp, "F1", "°C")}" : string.Empty) +
              $"（来源：{SourceOf(diskTemp, diskUsage)}）"
            : "不支持：读不到磁盘占用与硬盘温度。原因：" + MissingReason(diskUsage, monitoring)));

        return items;
    }

    private static string Num(MetricReading reading, string format, string unit)
        => reading.HasValue
            ? reading.Value!.Value.ToString(format, CultureInfo.InvariantCulture) + unit
            : "—";

    private static string SourceOf(params MetricReading[] readings)
    {
        foreach (var reading in readings)
        {
            if (!string.IsNullOrWhiteSpace(reading.Source) &&
                !string.Equals(reading.Source, "n/a", StringComparison.OrdinalIgnoreCase))
            {
                return reading.Source;
            }
        }
        return "未知来源";
    }

    /// <summary>指标缺失时给出真实原因：优先用监控后端自报的不支持原因，其次说明本次采样为空。</summary>
    private static string MissingReason(MetricReading reading, MonitoringProbe monitoring)
    {
        if (monitoring.Snapshot.Values.Count == 0)
        {
            return $"检测阶段没有采到任何监控数据（{monitoring.Note}）";
        }
        return string.IsNullOrWhiteSpace(reading.UnavailableReason) ? monitoring.Note : reading.UnavailableReason!;
    }

    private static string GuidText(string guid) => string.IsNullOrWhiteSpace(guid) ? string.Empty : $"（{guid}）";

    private static string ElevationHint(bool elevated)
        => elevated
            ? "当前已以管理员身份运行，可直接写入"
            : "当前未以管理员身份运行，写入会被系统拒绝，需要重启并提升权限";
}
