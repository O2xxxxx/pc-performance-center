using Ppc.Core.Services;

namespace Ppc.Hardware.Lenovo;

/// <summary>
/// LENOVO_FAN_METHOD 适配器：只封装 Fan_Get_Table（厂商原始风扇曲线表读取），
/// 用于只读探测与「厂商原始表」展示。按契约要求不做 Fan_Set_Table 写入——
/// 写厂商风扇表风险过高，本程序不提供该能力。
/// </summary>
public sealed class LenovoFanTableAdapter : IDisposable
{
    private const string WmiClassName = "LENOVO_FAN_METHOD";
    private const string GetTableMethod = "Fan_Get_Table";
    private const string SetTableMethod = "Fan_Set_Table";
    private const int DefaultTableSize = 10;

    private readonly object _sync = new();
    private readonly LenovoWmiConnection _connection = new(WmiClassName);
    private bool _probeAttempted;
    private bool _usable;
    private string _probeDetail = "尚未探测";
    private bool _disposed;

    public bool IsPresent => _connection.IsPresent;
    public bool ProbeAttempted { get { lock (_sync) return _probeAttempted; } }
    public bool IsUsable { get { lock (_sync) return _usable; } }
    public string ProbeDetail { get { lock (_sync) return _probeDetail; } }

    /// <summary>是否存在 Fan_Set_Table（仅用于说明「本程序不做危险写入」）。</summary>
    public bool HasSetTableMethod
    {
        get
        {
            lock (_sync)
            {
                return _connection.HasMethod(SetTableMethod);
            }
        }
    }

    /// <summary>只读探测：类/方法存在且 Fan_Get_Table 能正常返回数组才算可用。</summary>
    public bool TryProbe(bool force = false)
    {
        lock (_sync)
        {
            if (_probeAttempted && !force) return _usable;

            _probeAttempted = true;
            _usable = false;
            _probeDetail = string.Empty;

            if (_disposed)
            {
                _probeDetail = "适配器已释放，未执行探测";
                return false;
            }

            if (!_connection.Connect())
            {
                _probeDetail = _connection.OpenError;
                return false;
            }

            if (!_connection.HasMethod(GetTableMethod))
            {
                _probeDetail = $"WMI 类存在，但缺少 {GetTableMethod} 方法";
                return false;
            }

            if (!TryReadTable(0, 0, DefaultTableSize, out var table, out var error))
            {
                _probeDetail = error;
                return false;
            }

            _usable = true;
            _probeDetail = $"只读探测通过：{GetTableMethod} 返回 {table.Length} 项";
            AppLog.Instance.Info("联想风扇表只读探测通过：" + _probeDetail);
            return true;
        }
    }

    /// <summary>读取厂商原始风扇表（只读）。参数按契约：FanID / SensorID / FanTable / FanTableSize。</summary>
    public bool TryReadTable(int fanId, int sensorId, int tableSize, out int[] table, out string error)
    {
        lock (_sync)
        {
            table = Array.Empty<int>();
            error = string.Empty;

            if (!_connection.Connect())
            {
                error = _connection.OpenError;
                return false;
            }

            if (!_connection.HasMethod(GetTableMethod))
            {
                error = $"WMI 类 {WmiClassName} 上没有 {GetTableMethod} 方法";
                return false;
            }

            var size = Math.Clamp(tableSize, 1, 64);
            var input = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            {
                ["FanID"] = fanId,
                ["SensorID"] = sensorId,
                ["FanTableSize"] = size,
                ["TableSize"] = size
            };

            if (!_connection.TryInvoke(GetTableMethod, input, out var output, out var invokeError))
            {
                error = invokeError;
                return false;
            }

            try
            {
                if (output is not null && LenovoWmiValue.TryGetIntArray(output, new[] { "FanTable", "Table", "Data" }, out var values))
                {
                    table = values;
                    return true;
                }

                error = $"{GetTableMethod} 未返回 FanTable 数组";
                return false;
            }
            finally
            {
                output?.Dispose();
            }
        }
    }

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
