using System.Management;
using Ppc.Core.Models;
using Ppc.Core.Services;

namespace Ppc.Hardware.Lenovo;

/// <summary>厂商智能风扇档位（LENOVO_GAMEZONE_DATA 的 SmartFan / SetSmartFanMode 取值）。</summary>
public enum LenovoSmartFanMode
{
    Auto = 0,
    Silent = 1,
    Balanced = 2,
    Performance = 3
}

/// <summary>
/// LENOVO_GAMEZONE_DATA 适配器：只读探测 + 风扇/温度/档位读取 + 两个受控写入口。
///
/// 实测背景（联想 83AQ / XiaoXinPro 16 IRH8）：root\WMI 下该类与实例 ACPI\PNP0C14\GMZN_0 都存在，
/// 方法表也能读出来，但任何调用都会被固件拒绝（GetMethodParameters 返回 null，
/// InvokeMethod 报「无效的对象」/「常规故障」）。因此这里的写操作入口一律要求先通过只读探测，
/// 探测不过就永远不写，并把真实的 WMI 异常文本交给上层展示。
/// </summary>
public sealed class LenovoGameZoneAdapter : IDisposable
{
    private const string WmiClassName = "LENOVO_GAMEZONE_DATA";
    private const string InstancePrefix = "ACPI\\PNP0C14\\GMZN_";

    private static readonly string[] FanCountFields = { "FanCount", "FanCounts", "FanNumber" };
    private static readonly string[] Fan1Fields = { "Fan1Speed", "FanSpeed1", "Fan1" };
    private static readonly string[] Fan2Fields = { "Fan2Speed", "FanSpeed2", "Fan2" };
    private static readonly string[] Fan3Fields = { "Fan3Speed", "FanSpeed3", "Fan3" };
    private static readonly string[] Fan4Fields = { "Fan4Speed", "FanSpeed4", "Fan4" };
    private static readonly string[] FanMaxFields = { "FanMaxSpeed", "MaxFanSpeed", "FanFullSpeed" };
    private static readonly string[] CpuTempFields = { "CPUTemp", "CpuTemperature", "CPU_Temp" };
    private static readonly string[] GpuTempFields = { "GPUTemp", "GpuTemperature", "GPU_Temp" };
    private static readonly string[] ThermalModeFields = { "ThermalMode", "ThermalModeValue" };
    private static readonly string[] SmartFanFields = { "SmartFanMode", "FanMode" };
    private static readonly string[] FanCoolingSupportFields = { "SupportFanCooling", "IsSupportFanCooling", "FanCoolingSupport" };
    private static readonly string[] FanCoolingStatusFields = { "FanCoolingStatus", "FanCooling" };

    private readonly object _sync = new();
    private readonly LenovoWmiConnection _connection = new(WmiClassName, InstancePrefix);
    private string _probeDetail = "尚未探测";
    private string _lastError = string.Empty;
    private bool _probeAttempted;
    private bool _usable;
    private bool _disposed;

    public bool IsPresent => _connection.IsPresent;
    public bool ProbeAttempted { get { lock (_sync) return _probeAttempted; } }
    public bool IsUsable { get { lock (_sync) return _usable; } }
    public string ProbeDetail { get { lock (_sync) return _probeDetail; } }
    public string InstanceName => _connection.InstanceName;

    /// <summary>
    /// 只读探测：调用 GetFanCount，若存在 GetFan1Speed 再调一次。
    /// 任何一步失败即判定接口不可用，并把真实异常文本留在 ProbeDetail 里。
    /// </summary>
    public bool TryProbe(bool force = false)
    {
        lock (_sync)
        {
            if (_probeAttempted && !force) return _usable;

            _probeAttempted = true;
            _usable = false;
            _probeDetail = string.Empty;
            _lastError = string.Empty;

            if (_disposed)
            {
                _probeDetail = "适配器已释放，未执行探测";
                return false;
            }

            if (!Connect())
            {
                _probeDetail = _connection.OpenError;
                return false;
            }

            if (!_connection.HasMethod("GetFanCount"))
            {
                _probeDetail = "WMI 类存在，但缺少 GetFanCount 方法，无法确认风扇接口可用";
                return false;
            }

            if (!TryReadNumber("GetFanCount", FanCountFields, out var fanCount))
            {
                _probeDetail = string.IsNullOrEmpty(_lastError) ? "GetFanCount 调用失败" : _lastError;
                return false;
            }

            if (fanCount < 0)
            {
                _probeDetail = $"GetFanCount 返回非法风扇数量 {fanCount:0}";
                return false;
            }

            if (_connection.HasMethod("GetFan1Speed") &&
                !TryReadNumber("GetFan1Speed", Fan1Fields, out _))
            {
                _probeDetail = string.IsNullOrEmpty(_lastError) ? "GetFan1Speed 调用失败" : _lastError;
                return false;
            }

            _usable = true;
            _probeDetail = $"只读探测通过：GetFanCount={fanCount:0}";
            AppLog.Instance.Info("联想 GameZone 只读探测通过：" + _probeDetail);
            return true;
        }
    }

    /// <summary>读取风扇转速（RPM）。不可用或读不到时返回空列表，并在 error 里说明原因。</summary>
    public IReadOnlyList<int> ReadFanSpeeds(out string error)
    {
        lock (_sync)
        {
            error = string.Empty;
            var speeds = new List<int>();
            if (!_usable)
            {
                error = "厂商风扇接口不可用（只读探测未通过）";
                return speeds;
            }

            var count = 4;
            if (TryReadNumber("GetFanCount", FanCountFields, out var fanCount) && fanCount > 0)
            {
                count = (int)Math.Round(fanCount);
            }

            var methods = new[]
            {
                ("GetFan1Speed", Fan1Fields),
                ("GetFan2Speed", Fan2Fields),
                ("GetFan3Speed", Fan3Fields),
                ("GetFan4Speed", Fan4Fields)
            };

            for (var i = 0; i < methods.Length && i < count; i++)
            {
                var (method, fields) = methods[i];
                if (!_connection.HasMethod(method)) break;
                if (!TryReadNumber(method, fields, out var value)) break;
                if (value < 0) break;
                speeds.Add((int)Math.Round(value));
            }

            if (speeds.Count == 0)
            {
                error = string.IsNullOrEmpty(_lastError) ? "厂商接口未返回任何风扇转速" : _lastError;
            }

            return speeds;
        }
    }

    public int? ReadFanCount()
    {
        lock (_sync)
        {
            return _usable && TryReadNumber("GetFanCount", FanCountFields, out var value) && value >= 0
                ? (int)Math.Round(value)
                : null;
        }
    }

    public int? ReadFanMaxSpeed()
    {
        lock (_sync)
        {
            return _usable && TryReadNumber("GetFanMaxSpeed", FanMaxFields, out var value) && value > 0
                ? (int)Math.Round(value)
                : null;
        }
    }

    public int? ReadCpuTemperature() => ReadTemperature("GetCPUTemp", CpuTempFields);
    public int? ReadGpuTemperature() => ReadTemperature("GetGPUTemp", GpuTempFields);

    public int? ReadThermalMode()
    {
        lock (_sync)
        {
            return _usable && TryReadNumber("GetThermalMode", ThermalModeFields, out var value)
                ? (int)Math.Round(value)
                : null;
        }
    }

    public int? ReadSmartFanMode()
    {
        lock (_sync)
        {
            return _usable && TryReadNumber("GetSmartFanMode", SmartFanFields, out var value)
                ? (int)Math.Round(value)
                : null;
        }
    }

    public bool? IsFanCoolingSupported()
    {
        lock (_sync)
        {
            if (!_usable || !_connection.TryInvoke("IsSupportFanCooling", null, out var output, out _))
            {
                return null;
            }

            try
            {
                if (output is not null && LenovoWmiValue.TryGetBoolean(output, FanCoolingSupportFields, out var supported))
                {
                    return supported;
                }

                return null;
            }
            finally
            {
                output?.Dispose();
            }
        }
    }

    public int? ReadFanCoolingStatus()
    {
        lock (_sync)
        {
            return _usable && TryReadNumber("GetFanCoolingStatus", FanCoolingStatusFields, out var value)
                ? (int)Math.Round(value)
                : null;
        }
    }

    /// <summary>写入口之一：设置厂商智能风扇档位。写前必须通过只读探测，失败返回 Fail 与真实错误。</summary>
    public OpResult SetSmartFanMode(LenovoSmartFanMode mode) => SetSmartFanModeRaw((int)mode);

    /// <summary>按原始数值写档位（Manual 模式下把百分比映射成厂商离散档位用）。</summary>
    public OpResult SetSmartFanModeRaw(int rawValue)
    {
        var action = $"智能风扇档位写入（原始值 {rawValue}）";
        return Write("SetSmartFanMode", rawValue, action);
    }

    /// <summary>写入口之二：开关强制散热（厂商风扇全速）。</summary>
    public OpResult SetFanCooling(bool on)
    {
        var action = on ? "开启强制散热" : "关闭强制散热并交回固件自动控制";
        return Write("SetFanCooling", on ? 1 : 0, action);
    }

    private OpResult Write(string method, int rawValue, string action)
    {
        lock (_sync)
        {
            if (!_usable && !TryProbe())
            {
                var reason = ProbeDetail;
                AppLog.Instance.Warn($"联想风扇：{action} 未执行，{reason}");
                return OpResult.Fail($"未执行「{action}」。原因：厂商风扇接口不可用——{reason}");
            }

            if (!_connection.HasMethod(method))
            {
                return OpResult.Unsupported($"厂商接口没有 {method} 方法，无法执行「{action}」。");
            }

            // 方法参数名在不同机型/BIOS 上不一致，这里把常见名字都带上，
            // LenovoWmiConnection 只会写入目标方法真实存在的参数。
            var input = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["Mode"] = rawValue,
                ["Data"] = rawValue,
                ["FanCooling"] = rawValue,
                ["On"] = rawValue,
                ["Status"] = rawValue
            };

            if (!_connection.TryInvoke(method, input, out var output, out var error))
            {
                output?.Dispose();
                var message = $"厂商接口拒绝执行「{action}」。真实错误：{error}";
                AppLog.Instance.Warn("联想风扇：" + message);
                return OpResult.Fail(message);
            }

            var echoed = string.Empty;
            try
            {
                if (output is not null && LenovoWmiValue.TryGetStatus(output, out var status))
                {
                    echoed = $"（厂商状态码 {status}）";
                }
            }
            finally
            {
                output?.Dispose();
            }

            AppLog.Instance.Info($"联想风扇：{action} 成功{echoed}");
            return OpResult.Ok($"「{action}」已提交给厂商 ACPI/WMI 接口{echoed}。");
        }
    }

    private int? ReadTemperature(string method, string[] fields)
    {
        lock (_sync)
        {
            return _usable && TryReadNumber(method, fields, out var value) && value > 0
                ? (int)Math.Round(value)
                : null;
        }
    }

    private bool TryReadNumber(string method, string[] fields, out double value)
    {
        value = 0;
        if (!Connect())
        {
            _lastError = _connection.OpenError;
            return false;
        }

        if (!_connection.TryInvoke(method, null, out var output, out var error))
        {
            _lastError = error;
            return false;
        }

        try
        {
            if (output is not null && LenovoWmiValue.TryGetNumber(output, fields, out value))
            {
                return true;
            }

            _lastError = $"{method} 未返回可识别的数值字段";
            return false;
        }
        finally
        {
            output?.Dispose();
        }
    }

    private bool Connect()
    {
        if (_connection.IsPresent) return true;
        if (!_connection.Connect()) return false;
        AppLog.Instance.Info($"联想 WMI 已连接：{_connection.ClassName}（{_connection.InstanceName}）");
        return true;
    }

    /// <summary>把转速百分比映射成厂商离散档位（厂商只有静音/均衡/性能三档）。</summary>
    public static LenovoSmartFanMode MapPercentToMode(int percent)
    {
        var clamped = Math.Clamp(percent, FanCurve.MinSafePercent, FanCurve.MaxPercent);
        if (clamped <= 35) return LenovoSmartFanMode.Silent;
        if (clamped <= 70) return LenovoSmartFanMode.Balanced;
        return LenovoSmartFanMode.Performance;
    }

    /// <summary>档位对应的近似风扇百分比（用于在 UI 上说明实际生效档位）。</summary>
    public static int ModeToApproximatePercent(LenovoSmartFanMode mode) => mode switch
    {
        LenovoSmartFanMode.Silent => 25,
        LenovoSmartFanMode.Balanced => 55,
        LenovoSmartFanMode.Performance => 90,
        _ => 50
    };

    public static string DescribeMode(LenovoSmartFanMode mode) => mode switch
    {
        LenovoSmartFanMode.Auto => "自动",
        LenovoSmartFanMode.Silent => "静音",
        LenovoSmartFanMode.Balanced => "均衡",
        LenovoSmartFanMode.Performance => "性能",
        _ => "未知档位"
    };

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _usable = false;
            _connection.Dispose();
        }
    }
}
