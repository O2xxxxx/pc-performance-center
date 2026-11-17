using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using Ppc.Core.Services;

namespace Ppc.Hardware.Lenovo;

/// <summary>
/// System.Management 薄封装：负责打开 root\WMI、定位类实例、读取方法表并按名称调用。
/// 原则：不猜测成功。调用失败时把 WMI 的真实异常文本原样带出去，供 SupportDetail 展示。
/// </summary>
internal sealed class LenovoWmiConnection : IDisposable
{
    public const string ScopePath = @"root\wmi";
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(6);

    private readonly object _sync = new();
    private readonly string _className;
    private readonly string? _instancePrefix;
    private readonly HashSet<string> _methodNames = new(StringComparer.OrdinalIgnoreCase);
    private ManagementScope? _scope;
    private ManagementClass? _class;
    private ManagementObject? _instance;
    private bool _disposed;

    public LenovoWmiConnection(string className, string? instancePrefix = null)
    {
        _className = className;
        _instancePrefix = instancePrefix;
    }

    public string ClassName => _className;
    public bool IsPresent { get; private set; }
    public bool HasInstance => _instance is not null;
    public string InstanceName { get; private set; } = string.Empty;
    public string OpenError { get; private set; } = string.Empty;
    public IReadOnlyCollection<string> MethodNames => _methodNames;

    /// <summary>打开 root\WMI、加载类定义并抓取实例；成功返回 true，失败原因写入 OpenError。</summary>
    public bool Connect()
    {
        lock (_sync)
        {
            if (IsPresent) return true;
            if (_disposed)
            {
                OpenError = "WMI 连接已释放";
                return false;
            }

            ManagementScope? scope = null;
            ManagementClass? cls = null;
            try
            {
                scope = new ManagementScope($@"\\.\{ScopePath}");
                scope.Options.Timeout = CallTimeout;
                scope.Connect();

                cls = new ManagementClass(scope, new ManagementPath(_className), null);
                cls.Get(); // 读取类定义（方法表在 Get 之后才可用）
                foreach (MethodData method in cls.Methods)
                {
                    _methodNames.Add(method.Name);
                }

                // 类定义已读到：即使取不到实例，方法表也是可信的事实，保留连接用于报告真实原因
                _scope = scope;
                _class = cls;
                IsPresent = true;

                ManagementObject? target = null;
                try
                {
                    using var searcher = new ManagementObjectSearcher(
                        scope,
                        new ObjectQuery($"SELECT * FROM {_className}"),
                        new System.Management.EnumerationOptions { Timeout = CallTimeout });

                    foreach (ManagementObject candidate in searcher.Get())
                    {
                        var name = candidate["InstanceName"] as string ?? string.Empty;
                        if (_instancePrefix is not null &&
                            !name.StartsWith(_instancePrefix, StringComparison.OrdinalIgnoreCase))
                        {
                            candidate.Dispose();
                            continue;
                        }

                        target = candidate;
                        break;
                    }

                    OpenError = target is null
                        ? (_instancePrefix is null
                            ? $"未找到 WMI 类 {_className} 的实例"
                            : $"未找到 WMI 类 {_className} 的实例（实例名前缀 {_instancePrefix}）")
                        : string.Empty;
                }
                catch (ManagementException ex)
                {
                    // 本机实测：LENOVO_FAN_METHOD 类能读方法表，但枚举实例直接报「不支持」
                    OpenError = $"枚举 WMI 类 {_className} 的实例失败：{Describe(ex)}";
                }

                _instance = target;
                InstanceName = target?["InstanceName"] as string ?? string.Empty;
                if (!string.IsNullOrEmpty(OpenError)) AppLog.Instance.Warn(OpenError);
                return true;
            }
            catch (ManagementException ex)
            {
                OpenError = $"打开 WMI 类 {_className} 失败：{Describe(ex)}";
            }
            catch (COMException ex)
            {
                OpenError = $"打开 WMI 类 {_className} 失败：COM 错误 0x{unchecked((uint)ex.ErrorCode):X8} {ex.Message}";
            }
            catch (UnauthorizedAccessException ex)
            {
                OpenError = $"打开 WMI 类 {_className} 失败：权限不足 {ex.Message}";
            }
            catch (Exception ex)
            {
                OpenError = $"打开 WMI 类 {_className} 失败：{ex.GetType().Name}: {ex.Message}";
            }
            finally
            {
                if (!IsPresent)
                {
                    // 打开失败时释放半成品，避免句柄泄漏
                    try { cls?.Dispose(); } catch (Exception) { /* 释放失败不影响主流程 */ }
                }
            }

            AppLog.Instance.Warn(OpenError);
            return false;
        }
    }

    public bool HasMethod(string method)
    {
        lock (_sync)
        {
            if (!IsPresent && !Connect()) return false;
            return _methodNames.Contains(method);
        }
    }

    /// <summary>
    /// 按名称调用 WMI 方法。input 的键是「可能的方法参数名」，只有实际存在同名参数时才会被写入。
    /// 返回 false 时 error 里是 WMI 的真实异常（含错误码）；返回 true 但厂商 Status 非 0 也按失败处理。
    /// </summary>
    public bool TryInvoke(
        string method,
        IReadOnlyDictionary<string, object?>? input,
        out ManagementBaseObject? output,
        out string error)
    {
        output = null;
        error = string.Empty;

        lock (_sync)
        {
            if (!IsPresent && !Connect())
            {
                error = OpenError;
                return false;
            }

            if (!_methodNames.Contains(method))
            {
                error = $"WMI 类 {_className} 上没有方法 {method}";
                return false;
            }

            if (_instance is null)
            {
                error = $"WMI 类 {_className} 没有可用实例：{(string.IsNullOrEmpty(OpenError) ? "实例未找到" : OpenError)}";
                return false;
            }

            var notes = new List<string>();
            try
            {
                var inParams = _instance!.GetMethodParameters(method);
                if (inParams is null)
                {
                    // 本机实测现象：方法表存在，但 GetMethodParameters 直接返回 null
                    notes.Add("GetMethodParameters 返回 null");
                }
                else if (input is not null)
                {
                    foreach (var pair in input)
                    {
                        if (pair.Value is null) continue;
                        PropertyData? property;
                        try { property = inParams.Properties[pair.Key]; }
                        catch (ManagementException) { continue; }
                        if (property is null) continue;
                        property.Value = Coerce(pair.Value, property.Type);
                    }
                }

                try
                {
                    output = _instance.InvokeMethod(method, inParams, null);
                }
                catch (ManagementException) when (inParams is null)
                {
                    // GetMethodParameters 返回 null 时，部分机型仍接受无参对象；再试一次，把两次真实错误都留下
                    notes.Add("改为无参对象重试");
                    output = _instance.InvokeMethod(method, null, null);
                }

                if (output is not null && LenovoWmiValue.TryGetStatus(output, out var status) && status != 0)
                {
                    notes.Add($"厂商状态码 {status}");
                    error = $"{method} 返回失败状态：{string.Join("；", notes)}";
                    return false;
                }

                return true;
            }
            catch (ManagementException ex)
            {
                notes.Add(Describe(ex));
                error = $"{method}：{string.Join("；", notes)}";
                return false;
            }
            catch (COMException ex)
            {
                notes.Add($"COM 错误 0x{unchecked((uint)ex.ErrorCode):X8} {ex.Message}");
                error = $"{method}：{string.Join("；", notes)}";
                return false;
            }
            catch (InvalidOperationException ex)
            {
                notes.Add("InvalidOperationException: " + ex.Message);
                error = $"{method}：{string.Join("；", notes)}";
                return false;
            }
            catch (Exception ex)
            {
                notes.Add($"{ex.GetType().Name}: {ex.Message}");
                error = $"{method}：{string.Join("；", notes)}";
                return false;
            }
        }
    }

    public static string Describe(ManagementException ex)
    {
        var code = unchecked((uint)ex.ErrorCode);
        return $"{ex.Message}（0x{code:X8}）";
    }

    private static object? Coerce(object? value, CimType type)
    {
        if (value is null) return null;
        if (value is Array) return value;
        try
        {
            return type switch
            {
                CimType.Boolean => Convert.ToBoolean(value, CultureInfo.InvariantCulture),
                CimType.UInt8 => Convert.ToByte(value, CultureInfo.InvariantCulture),
                CimType.SInt8 => Convert.ToSByte(value, CultureInfo.InvariantCulture),
                CimType.UInt16 => Convert.ToUInt16(value, CultureInfo.InvariantCulture),
                CimType.SInt16 => Convert.ToInt16(value, CultureInfo.InvariantCulture),
                CimType.UInt32 => Convert.ToUInt32(value, CultureInfo.InvariantCulture),
                CimType.SInt32 => Convert.ToInt32(value, CultureInfo.InvariantCulture),
                CimType.UInt64 => Convert.ToUInt64(value, CultureInfo.InvariantCulture),
                CimType.SInt64 => Convert.ToInt64(value, CultureInfo.InvariantCulture),
                CimType.String => Convert.ToString(value, CultureInfo.InvariantCulture),
                _ => value
            };
        }
        catch (Exception)
        {
            // 转换失败就原样交给 WMI，让真实的类型错误从 InvokeMethod 里报出来
            return value;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            IsPresent = false;
            try { _instance?.Dispose(); } catch (Exception) { /* 释放失败不影响主流程 */ }
            try { _class?.Dispose(); } catch (Exception) { /* 释放失败不影响主流程 */ }
            _instance = null;
            _class = null;
            _scope = null;
            _methodNames.Clear();
        }
    }
}

/// <summary>从 WMI 输出对象里安全地取值。所有方法失败只返回 false，不抛异常。</summary>
internal static class LenovoWmiValue
{
    private static bool IsStatusName(string name) =>
        name.Equals("Status", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("ReturnValue", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("ReturnStatus", StringComparison.OrdinalIgnoreCase);

    public static bool TryGetStatus(ManagementBaseObject? output, out int status)
    {
        status = 0;
        if (output is null) return false;
        foreach (var name in new[] { "Status", "ReturnValue", "ReturnStatus" })
        {
            try
            {
                var property = output.Properties[name];
                if (property?.Value is null) continue;
                status = Convert.ToInt32(property.Value, CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception)
            {
                // 该字段不是数值，检查下一个
            }
        }

        return false;
    }

    public static bool TryGetNumber(ManagementBaseObject? output, string[] preferredNames, out double value)
    {
        value = 0;
        if (output is null) return false;

        foreach (var preferred in preferredNames)
        {
            try
            {
                var property = output.Properties[preferred];
                if (property?.Value is null) continue;
                if (TryConvertNumber(property.Value, out value)) return true;
            }
            catch (Exception)
            {
                // 忽略单个字段的异常
            }
        }

        foreach (PropertyData property in output.Properties)
        {
            if (IsStatusName(property.Name)) continue;
            if (TryConvertNumber(property.Value, out value)) return true;
        }

        return false;
    }

    public static bool TryGetBoolean(ManagementBaseObject? output, string[] preferredNames, out bool value)
    {
        value = false;
        if (output is null) return false;

        foreach (var preferred in preferredNames)
        {
            try
            {
                var property = output.Properties[preferred];
                if (property?.Value is null) continue;
                if (TryConvertBoolean(property.Value, out value)) return true;
            }
            catch (Exception)
            {
                // 忽略单个字段的异常
            }
        }

        foreach (PropertyData property in output.Properties)
        {
            if (IsStatusName(property.Name)) continue;
            if (TryConvertBoolean(property.Value, out value)) return true;
        }

        return false;
    }

    public static bool TryGetIntArray(ManagementBaseObject? output, string[] preferredNames, out int[] values)
    {
        values = Array.Empty<int>();
        if (output is null) return false;

        foreach (var preferred in preferredNames)
        {
            try
            {
                var property = output.Properties[preferred];
                if (property?.Value is not Array array) continue;

                var list = new List<int>();
                foreach (var item in array)
                {
                    if (!TryConvertNumber(item, out var number))
                    {
                        list.Clear();
                        break;
                    }

                    list.Add((int)Math.Round(number));
                }

                if (list.Count > 0)
                {
                    values = list.ToArray();
                    return true;
                }
            }
            catch (Exception)
            {
                // 忽略单个字段的异常
            }
        }

        return false;
    }

    public static bool TryConvertNumber(object? raw, out double value)
    {
        value = 0;
        try
        {
            switch (raw)
            {
                case null:
                    return false;
                case bool boolean:
                    value = boolean ? 1 : 0;
                    return true;
                case string text:
                    return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
                case Array:
                    return false;
                case IConvertible convertible:
                    value = convertible.ToDouble(CultureInfo.InvariantCulture);
                    return true;
                default:
                    return false;
            }
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static bool TryConvertBoolean(object? raw, out bool value)
    {
        value = false;
        switch (raw)
        {
            case null:
                return false;
            case bool boolean:
                value = boolean;
                return true;
            case string text when bool.TryParse(text, out var parsed):
                value = parsed;
                return true;
            case Array:
                return false;
        }

        if (!TryConvertNumber(raw, out var number)) return false;
        value = Math.Abs(number) > double.Epsilon;
        return true;
    }
}
