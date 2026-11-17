using System.Globalization;
using System.Management;
using Ppc.Core.Services;

namespace Ppc.Hardware.Detection;

/// <summary>
/// 一个 WMI 类的只读探测结果。
/// Exists 表示类定义是否存在；InstanceCount 表示当前能否枚举到实例（空壳接口常常「类在、实例在、方法调不通」）。
/// </summary>
internal readonly record struct WmiClassProbe(bool Exists, int InstanceCount, string InstancePath, string Detail);

/// <summary>
/// System.Management 的只读封装。所有查询都自带 try/catch 与超时：
/// 单条查询失败只记一条日志并返回空结果，绝不把异常抛给上层（硬件探测失败必须降级，不能让 UI 崩）。
/// 本类不含任何 InvokeMethod 写操作，唯一的方法调用是 <see cref="TryInvokeReadOnlyMethod"/> 里的只读探针。
/// </summary>
internal static class WmiProbe
{
    internal const string CimV2 = "root\\CIMV2";
    internal const string WmiRoot = "root\\WMI";
    internal const string PowerNamespace = "root\\cimv2\\power";

    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(10);

    /// <summary>执行 SELECT，把每个实例复制成「属性名 → 值」字典。失败返回空列表。</summary>
    internal static List<Dictionary<string, object?>> Query(string wmiNamespace, string className, params string[] properties)
    {
        var rows = new List<Dictionary<string, object?>>();
        try
        {
            var projection = properties.Length > 0 ? string.Join(", ", properties) : "*";
            using var searcher = new ManagementObjectSearcher(wmiNamespace, $"SELECT {projection} FROM {className}")
            {
                Options = new System.Management.EnumerationOptions { Timeout = QueryTimeout, ReturnImmediately = false }
            };
            using var collection = searcher.Get();
            foreach (ManagementObject instance in collection)
            {
                using (instance)
                {
                    var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (var property in properties)
                    {
                        try
                        {
                            row[property] = instance[property];
                        }
                        catch (Exception ex)
                        {
                            row[property] = null;
                            AppLog.Instance.Warn($"WMI 属性读取失败 {className}.{property}：{Describe(ex)}");
                        }
                    }
                    rows.Add(row);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn($"WMI 查询失败 {wmiNamespace}:{className}：{Describe(ex)}");
        }
        return rows;
    }

    /// <summary>探测类是否存在以及有多少实例。只读，不调用任何方法。</summary>
    internal static WmiClassProbe ProbeClass(string wmiNamespace, string className)
    {
        var metaFound = false;
        string? metaError = null;
        try
        {
            using var searcher = new ManagementObjectSearcher(wmiNamespace, $"SELECT * FROM meta_class WHERE __CLASS = '{className}'");
            using var collection = searcher.Get();
            metaFound = collection.Count > 0;
        }
        catch (Exception ex)
        {
            metaError = Describe(ex);
            AppLog.Instance.Warn($"WMI 类元数据查询失败 {wmiNamespace}:{className}：{metaError}");
        }

        var instanceCount = 0;
        var instancePath = string.Empty;
        string? instanceError = null;
        try
        {
            using var searcher = new ManagementObjectSearcher(wmiNamespace, $"SELECT * FROM {className}")
            {
                Options = new System.Management.EnumerationOptions { Timeout = QueryTimeout, ReturnImmediately = false }
            };
            using var collection = searcher.Get();
            foreach (ManagementObject instance in collection)
            {
                using (instance)
                {
                    instanceCount++;
                    if (instancePath.Length == 0)
                    {
                        try
                        {
                            instancePath = instance.Path?.Path ?? string.Empty;
                        }
                        catch (Exception ex)
                        {
                            AppLog.Instance.Warn($"读取 WMI 实例路径失败 {className}：{Describe(ex)}");
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            instanceError = Describe(ex);
        }

        var exists = metaFound || instanceCount > 0;
        string detail;
        if (exists)
        {
            detail = $"类 {className} 存在，实例数 {instanceCount}";
            if (instancePath.Length > 0) detail += $"，实例路径 {instancePath}";
        }
        else
        {
            var error = instanceError ?? metaError;
            detail = $"类 {className} 不存在" + (error is null ? "" : $"（{error}）");
        }
        return new WmiClassProbe(exists, instanceCount, instancePath, detail);
    }

    /// <summary>
    /// 调用一个「只读 getter」方法验证接口是否真的可用（例如联想 GameZone 的 GetFanCount）。
    /// 只允许传只读方法名；任何写方法（Set*）都不得走到这里。
    /// </summary>
    internal static (bool Ok, string Message) TryInvokeReadOnlyMethod(string wmiNamespace, string className, string methodName)
    {
        if (!methodName.StartsWith("Get", StringComparison.OrdinalIgnoreCase) &&
            !methodName.StartsWith("Is", StringComparison.OrdinalIgnoreCase) &&
            !methodName.StartsWith("Read", StringComparison.OrdinalIgnoreCase))
        {
            return (false, $"拒绝调用非只读方法 {className}.{methodName}");
        }

        try
        {
            using var searcher = new ManagementObjectSearcher(wmiNamespace, $"SELECT * FROM {className}")
            {
                Options = new System.Management.EnumerationOptions { Timeout = QueryTimeout, ReturnImmediately = false }
            };
            using var collection = searcher.Get();
            var attempted = false;
            var message = "没有可调用实例";
            foreach (ManagementObject instance in collection)
            {
                using (instance)
                {
                    attempted = true;
                    try
                    {
                        using var inParameters = instance.GetMethodParameters(methodName);
                        if (inParameters is not null)
                        {
                            using var outParameters = instance.InvokeMethod(methodName, inParameters, null);
                            return (true, $"只读方法 {className}.{methodName} 调用成功");
                        }

                        message = $"{className}.{methodName} 的 GetMethodParameters 返回 null";
                        try
                        {
                            // 无参重载返回 object，成功即代表方法真的被固件接受。
                            var raw = instance.InvokeMethod(methodName, Array.Empty<object>());
                            (raw as ManagementBaseObject)?.Dispose();
                            return (true, $"只读方法 {className}.{methodName} 无参调用成功");
                        }
                        catch (Exception inner)
                        {
                            message += $"；无参调用也失败：{Describe(inner)}";
                        }
                    }
                    catch (Exception ex)
                    {
                        message = $"{className}.{methodName} 调用失败：{Describe(ex)}";
                    }
                }
            }
            return (false, attempted ? message : $"类 {className} 没有任何实例，无法调用 {methodName}");
        }
        catch (Exception ex)
        {
            return (false, $"探测 {className}.{methodName} 时出错：{Describe(ex)}");
        }
    }

    internal static string GetString(IReadOnlyDictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value is null) return string.Empty;
        try
        {
            return value switch
            {
                string text => text.Trim(),
                string[] array => string.Join(", ", array).Trim(),
                _ => (Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty).Trim()
            };
        }
        catch
        {
            return string.Empty;
        }
    }

    internal static long GetLong(IReadOnlyDictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value is null) return 0;
        try
        {
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0;
        }
    }

    internal static bool GetBool(IReadOnlyDictionary<string, object?> row, string key)
    {
        if (!row.TryGetValue(key, out var value) || value is null) return false;
        try
        {
            return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>把 WMI 的 CIM_DATETIME 字符串（如 20260429000000.000000+000）转成 yyyy-MM-dd。</summary>
    internal static string GetCimDate(IReadOnlyDictionary<string, object?> row, string key)
    {
        var raw = GetString(row, key);
        if (raw.Length == 0) return string.Empty;
        try
        {
            return ManagementDateTimeConverter.ToDateTime(raw).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn($"WMI 日期解析失败 {key}={raw}：{Describe(ex)}");
            return raw;
        }
    }

    internal static string Describe(Exception ex) => ex is ManagementException management
        ? $"ManagementException {management.ErrorCode}（{ex.Message}）"
        : $"{ex.GetType().Name}（{ex.Message}）";
}
