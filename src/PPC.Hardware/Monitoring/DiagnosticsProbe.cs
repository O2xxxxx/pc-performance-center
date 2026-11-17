using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using LibreHardwareMonitor.Hardware;
using Ppc.Core.Abstractions;
using Ppc.Core.Services;

namespace Ppc.Hardware.Monitoring;

/// <summary>
/// 诊断探针：把 LibreHardwareMonitor 的硬件树、传感器清单、系统电源/内存事实、
/// CPU 功耗上限探测结果打印成纯文本，供设置页“导出诊断”使用。
/// 只读，不修改任何硬件状态；任何失败都写进报告文本，不抛异常。
/// </summary>
public static class DiagnosticsProbe
{
    /// <summary>
    /// 收集一份诊断报告。
    /// 传入正在运行的监控服务时复用它的硬件引擎（不会重开或关闭驱动、不影响采样）；
    /// 不传时临时开一个引擎实例，用完立刻关闭。
    /// </summary>
    public static async Task<string> CollectAsync(IMonitorService? monitor = null)
    {
        var report = new StringBuilder();
        try
        {
            report.AppendLine("==== PC Performance Center 硬件诊断报告 ====");
            report.AppendLine("生成时间：" + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture));
            report.AppendLine("操作系统：" + RuntimeInformation.OSDescription + "（" + RuntimeInformation.OSArchitecture + "）");
            report.AppendLine("运行时：" + RuntimeInformation.FrameworkDescription);
            report.AppendLine("进程权限：" + (SystemPowerProbe.IsElevated
                ? "管理员（可加载 LibreHardwareMonitor 内核驱动）"
                : "普通用户（无法加载内核驱动，传感器大概率读不到）"));

            var machine = SystemPowerProbe.ReadMachineText();
            if (machine.Length > 0) report.AppendLine("整机：" + machine);

            if (monitor is null)
            {
                report.AppendLine("监控服务：未提供（本报告使用临时引擎实例采集）");
            }
            else
            {
                report.AppendLine("监控服务：" + (monitor.IsRunning ? "运行中" : "已停止"));
                foreach (var backend in monitor.ActiveBackends) report.AppendLine("  后端：" + backend);
                if (monitor is LhmMonitorService live)
                {
                    report.AppendLine("  引擎状态：" + (live.EngineIssue ?? "正常"));
                }
            }

            report.AppendLine();
            report.AppendLine("---- 硬件树与传感器 ----");

            string tree;
            try
            {
                tree = monitor is LhmMonitorService service
                    ? await service.DumpHardwareTreeAsync().ConfigureAwait(false)
                    : await Task.Run(DescribeWithTemporaryEngine).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                tree = "读取硬件树失败：" + ex.GetType().Name + "：" + ex.Message;
                AppLog.Instance.Error("诊断探针读取硬件树失败", ex);
            }

            report.AppendLine(tree);

            report.AppendLine();
            report.AppendLine("---- 系统电源 ----");
            var power = SystemPowerProbe.ReadPowerStatus();
            report.AppendLine(power is null ? "GetSystemPowerStatus 读取失败" : power.Value.Describe());

            report.AppendLine();
            report.AppendLine("---- 系统内存（GlobalMemoryStatusEx）----");
            var memory = SystemPowerProbe.ReadMemoryStatus();
            if (memory is null)
            {
                report.AppendLine("读取失败");
            }
            else
            {
                report.AppendLine("总量：" + FormatGib(memory.Value.TotalBytes) + " GiB");
                report.AppendLine("可用：" + FormatGib(memory.Value.AvailableBytes) + " GiB");
                report.AppendLine("占用：" + memory.Value.LoadPercent + "%");
            }

            report.AppendLine();
            report.AppendLine("---- CPU 功耗上限探测（powercfg / WMI Win32_Processor）----");
            var limit = CpuLimitProbe.Probe();
            if (limit.Value is { } watts)
            {
                report.AppendLine("数值：" + watts.ToString("0.#", CultureInfo.InvariantCulture) + " W（来源：" + limit.Source + "）");
            }
            else
            {
                report.AppendLine("不可用：" + (limit.UnavailableReason ?? "原因未知"));
                report.AppendLine("来源：" + limit.Source);
            }

            report.AppendLine();
            report.AppendLine("==== 报告结束 ====");
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("生成诊断报告失败", ex);
            report.AppendLine();
            report.AppendLine("生成诊断报告时出现异常：" + ex.GetType().Name + "：" + ex.Message);
        }

        return report.ToString();
    }

    /// <summary>把硬件树（含全部 SubHardware）与风扇清单渲染成文本。供 LhmMonitorService 复用。</summary>
    internal static string Describe(Computer computer)
    {
        var builder = new StringBuilder();
        var hardwareCount = 0;
        var sensorCount = 0;
        var fanLines = new List<string>();

        try
        {
            foreach (var hardware in computer.Hardware)
            {
                WriteHardware(hardware, builder, 0, ref hardwareCount, ref sensorCount, fanLines);
            }

            if (hardwareCount == 0)
            {
                builder.AppendLine("（未发现任何硬件节点：内核驱动可能未加载，或没有被支持的硬件）");
            }

            builder.AppendLine();
            builder.AppendLine("---- 风扇传感器 ----");
            if (fanLines.Count == 0)
            {
                builder.AppendLine("（无：LibreHardwareMonitor 没有暴露任何 SensorType.Fan 传感器，本机风扇转速不可读）");
            }
            else
            {
                foreach (var line in fanLines) builder.AppendLine(line);
            }

            builder.AppendLine();
            builder.AppendLine("统计：硬件节点 " + hardwareCount + " 个，传感器 " + sensorCount + " 个，风扇通道 " + fanLines.Count + " 个");
        }
        catch (Exception ex)
        {
            builder.AppendLine("渲染硬件树时异常：" + ex.GetType().Name + "：" + ex.Message);
            AppLog.Instance.Error("渲染硬件树失败", ex);
        }

        return builder.ToString();
    }

    private static void WriteHardware(
        IHardware hardware,
        StringBuilder builder,
        int depth,
        ref int hardwareCount,
        ref int sensorCount,
        List<string> fanLines)
    {
        hardwareCount++;
        var indent = new string(' ', depth * 2);
        var sensors = hardware.Sensors;
        var sensorTotal = sensors is null ? 0 : sensors.Length;

        builder.AppendLine(indent + "[" + hardware.HardwareType + "] " + Clean(hardware.Name) + "（传感器 " + sensorTotal + " 个）");

        if (sensors is { Length: > 0 })
        {
            foreach (var sensor in sensors)
            {
                sensorCount++;
                var value = sensor.Value is float raw
                    ? raw.ToString("0.###", CultureInfo.InvariantCulture)
                    : "null";

                builder.AppendLine(indent + "  " + Pad(sensor.SensorType.ToString(), 12) + " "
                                   + Pad(Clean(sensor.Name), 34) + value + SensorUnit(sensor.SensorType));

                if (sensor.SensorType == SensorType.Fan)
                {
                    fanLines.Add(hardware.Name + "/" + sensor.Name + " = " + value + " RPM");
                }
            }
        }

        var children = hardware.SubHardware;
        if (children is { Length: > 0 })
        {
            foreach (var child in children)
            {
                WriteHardware(child, builder, depth + 1, ref hardwareCount, ref sensorCount, fanLines);
            }
        }
    }

    /// <summary>临时开一个引擎实例做诊断，用完立刻关闭，避免和正在运行的监控抢驱动句柄。</summary>
    private static string DescribeWithTemporaryEngine()
    {
        Computer? computer = null;
        try
        {
            computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsStorageEnabled = true,
                IsMotherboardEnabled = true,
                IsBatteryEnabled = true,
                IsControllerEnabled = true
            };

            computer.Open();
            return Describe(computer);
        }
        catch (Exception ex)
        {
            var hint = SystemPowerProbe.IsElevated
                ? "（已具备管理员权限，可能是驱动被其它监控软件占用）"
                : "（当前进程不是管理员，无法加载内核驱动）";
            AppLog.Instance.Error("临时引擎打开失败", ex);
            return "打开 LibreHardwareMonitor 失败：" + ex.GetType().Name + "：" + ex.Message + hint;
        }
        finally
        {
            try
            {
                computer?.Close();
            }
            catch
            {
                // 临时实例关闭失败不影响报告
            }
        }
    }

    private static string SensorUnit(SensorType type) => type switch
    {
        SensorType.Temperature => " °C",
        SensorType.Load => " %",
        SensorType.Power => " W",
        SensorType.Clock => " MHz",
        SensorType.Fan => " RPM",
        SensorType.Voltage => " V",
        SensorType.Current => " A",
        SensorType.Frequency => " Hz",
        SensorType.Data => " GB",
        SensorType.Level => " %",
        _ => string.Empty
    };

    /// <summary>去掉硬件名里夹带的控制字符（本机电池名尾巴上带一个 NUL）。</summary>
    private static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsControl(ch)) continue;
            builder.Append(ch);
        }

        return builder.ToString().Trim();
    }

    private static string FormatGib(ulong bytes)
        => (bytes / (1024d * 1024d * 1024d)).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>按近似宽度补空格（中文按 2 个字符宽计算），让诊断文本能对齐。</summary>
    private static string Pad(string text, int width)
    {
        var displayWidth = 0;
        foreach (var ch in text)
        {
            displayWidth += ch > 0x7F ? 2 : 1;
        }

        if (displayWidth >= width) return text;
        return text + new string(' ', width - displayWidth);
    }
}
