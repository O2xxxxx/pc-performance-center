using System.Diagnostics;
using System.Globalization;
using System.Management;
using Ppc.Core.Models;

namespace Ppc.Hardware.Monitoring;

/// <summary>
/// CPU 功耗上限（PL1/PL2）探测。
/// 只用 Windows 公开接口：powercfg（最大处理器状态百分比）与 WMI Win32_Processor。
/// 拿不到瓦数就返回 Unavailable 并写清楚为什么，绝不用 TDP 或百分比换算出来充数。
/// </summary>
internal static class CpuLimitProbe
{
    private static readonly string[] PowerLikePropertyHints =
    {
        "Power", "Limit", "Watt", "Tdp", "Plimit"
    };

    /// <summary>同步探测一次，返回可直接塞进快照的读数（结果由调用方缓存，不要高频调用）。</summary>
    public static MetricReading Probe()
    {
        var notes = new List<string>();

        double? watts = null;
        try
        {
            var query = Task.Run(() => ProbeWmiProcessor(notes));
            if (query.Wait(TimeSpan.FromSeconds(4)))
            {
                watts = query.Result;
            }
            else
            {
                notes.Add("WMI Win32_Processor 查询超时（4 秒）");
            }
        }
        catch (Exception ex)
        {
            notes.Add("WMI Win32_Processor 查询异常：" + ex.GetType().Name + "：" + ex.Message);
        }

        var maxState = TryReadMaxProcessorState(notes);
        if (maxState.HasValue)
        {
            notes.Add("powercfg 只能读到最大处理器状态 " + maxState.Value + "%（PROCTHROTTLEMAX），该值不是瓦数，不能换算成功耗上限");
        }

        if (watts.HasValue && watts.Value > 0)
        {
            return MetricReading.Ok(Math.Round(watts.Value, 1), "WMI Win32_Processor");
        }

        var reason = notes.Count > 0 ? string.Join("；", notes) : "未取得任何可用数据";
        return MetricReading.Unavailable(
            reason + "。Windows 公开接口不提供 CPU PL1/PL2 瓦数，因此该项不显示数值",
            "powercfg / WMI Win32_Processor");
    }

    /// <summary>在 Win32_Processor 的属性里找任何像功耗/上限的字段；没有就如实写进 notes。</summary>
    private static double? ProbeWmiProcessor(List<string> notes)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT * FROM Win32_Processor");

            var inspected = 0;
            double? found = null;

            foreach (ManagementBaseObject item in searcher.Get())
            {
                foreach (var property in item.Properties)
                {
                    inspected++;
                    if (property.Value is null) continue;

                    var name = property.Name ?? string.Empty;
                    if (!LooksLikePowerLimit(name)) continue;

                    // 只认真正的数值属性（PowerManagementCapabilities 这类数组属性直接跳过）
                    if (property.Value is not (byte or sbyte or short or ushort or int or uint
                        or long or ulong or float or double or decimal)) continue;

                    if (double.TryParse(Convert.ToString(property.Value, CultureInfo.InvariantCulture),
                            NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value > 0)
                    {
                        notes.Add("Win32_Processor." + name + " = " + value.ToString("0.###", CultureInfo.InvariantCulture) + "（按瓦数解释）");
                        found = value;
                    }
                }
            }

            if (found is null)
            {
                notes.Add("Win32_Processor 未提供任何功耗/上限字段（已检查 " + inspected + " 个属性）");
            }

            return found;
        }
        catch (Exception ex)
        {
            notes.Add("WMI 查询失败：" + ex.Message);
            return null;
        }
    }

    private static bool LooksLikePowerLimit(string name)
    {
        foreach (var hint in PowerLikePropertyHints)
        {
            if (name.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }

        return false;
    }

    /// <summary>用 powercfg 读“最大处理器状态”百分比，只用于在不可用原因里给出真实依据。</summary>
    private static int? TryReadMaxProcessorState(List<string> notes)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = "powercfg",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            info.ArgumentList.Add("/query");
            info.ArgumentList.Add("SCHEME_CURRENT");
            info.ArgumentList.Add("SUB_PROCESSOR");
            info.ArgumentList.Add("PROCTHROTTLEMAX");

            using var process = Process.Start(info);
            if (process is null)
            {
                notes.Add("powercfg 无法启动");
                return null;
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);

            // 输出顺序固定为：允许最小值、允许最大值、增量、当前交流值、当前直流值 —— 取最后一个才是当前生效的设置
            var hexValues = new List<uint>();
            foreach (var line in output.Split(new[] { (char)13, (char)10 }, StringSplitOptions.RemoveEmptyEntries))
            {
                var index = line.IndexOf("0x", StringComparison.OrdinalIgnoreCase);
                if (index < 0 || line.Length < index + 10) continue;

                if (uint.TryParse(line.Substring(index + 2, 8), NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out var parsed))
                {
                    hexValues.Add(parsed);
                }
            }

            if (hexValues.Count == 0)
            {
                notes.Add("powercfg 未返回 PROCTHROTTLEMAX 数值（退出码 " + process.ExitCode + "）");
                return null;
            }

            var percent = (int)hexValues[hexValues.Count - 1];
            return percent is >= 0 and <= 200 ? percent : null;
        }
        catch (Exception ex)
        {
            notes.Add("调用 powercfg 失败：" + ex.GetType().Name + "：" + ex.Message);
            return null;
        }
    }
}
