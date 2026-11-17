using System.Globalization;
using System.Text.RegularExpressions;
using Ppc.Core.Models;
using Ppc.Core.Services;

namespace Ppc.Hardware.Detection;

/// <summary>联想私有 WMI 接口的只读探测结果，供能力评估使用。</summary>
internal sealed class LenovoProbeFacts
{
    internal LenovoProbeFacts(WmiClassProbe gameZone, WmiClassProbe fanTable, bool fanCountCallOk, string fanCountCallMessage)
    {
        GameZone = gameZone;
        FanTable = fanTable;
        FanCountCallOk = fanCountCallOk;
        FanCountCallMessage = fanCountCallMessage;
    }

    /// <summary>root\\WMI 中的 LENOVO_GAMEZONE_DATA（联想 GameZone ACPI 接口）。</summary>
    internal WmiClassProbe GameZone { get; }

    /// <summary>root\\WMI 中的 LENOVO_FAN_METHOD（联想风扇表接口）。</summary>
    internal WmiClassProbe FanTable { get; }

    /// <summary>只读探针 GetFanCount 是否真的调用成功（用来区分「接口在」和「接口能用」）。</summary>
    internal bool FanCountCallOk { get; }

    internal string FanCountCallMessage { get; }
}

/// <summary>
/// 设备静态信息采集：全部走只读 WMI 查询（System.Management）。
/// 任何一项失败只留空并写日志，绝不抛异常；本类不包含任何写操作。
/// </summary>
internal static class DeviceInfoCollector
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>采集硬件信息填入 profile，并返回联想接口探测结果。</summary>
    internal static LenovoProbeFacts Collect(DeviceProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);

        CollectComputerSystem(profile);
        CollectComputerSystemProduct(profile);
        CollectBios(profile);
        CollectBaseBoard(profile);
        CollectProcessor(profile);
        CollectVideoControllers(profile);
        CollectOperatingSystem(profile);

        profile.Platform = ResolvePlatform(profile);
        return ProbeLenovoInterfaces(profile);
    }

    private static void CollectComputerSystem(DeviceProfile profile)
    {
        var row = WmiProbe.Query(WmiProbe.CimV2, "Win32_ComputerSystem",
            "Manufacturer", "Model", "SystemFamily", "TotalPhysicalMemory").FirstOrDefault();
        if (row is null)
        {
            AppLog.Instance.Warn("未能读取 Win32_ComputerSystem，厂商/型号/内存字段保持默认值");
            return;
        }

        profile.Manufacturer = OrUnknown(WmiProbe.GetString(row, "Manufacturer"));
        profile.Model = OrUnknown(WmiProbe.GetString(row, "Model"));
        profile.SystemFamily = WmiProbe.GetString(row, "SystemFamily");
        profile.TotalMemoryBytes = WmiProbe.GetLong(row, "TotalPhysicalMemory");
    }

    private static void CollectComputerSystemProduct(DeviceProfile profile)
    {
        var row = WmiProbe.Query(WmiProbe.CimV2, "Win32_ComputerSystemProduct",
            "Name", "Version", "SKUNumber", "IdentifyingNumber").FirstOrDefault();
        if (row is null)
        {
            AppLog.Instance.Warn("未能读取 Win32_ComputerSystemProduct，机型族/序列号字段留空");
            return;
        }

        profile.Sku = WmiProbe.GetString(row, "SKUNumber");
        if (profile.Sku.Length == 0)
        {
            // 实测部分联想机型（本机 83AQ）Win32_ComputerSystemProduct.SKU 为空，
            // 改从 Win32_ComputerSystem.SystemSKUNumber 兜底取机型号。
            var fallback = WmiProbe.Query(WmiProbe.CimV2, "Win32_ComputerSystem", "SystemSKUNumber").FirstOrDefault();
            if (fallback is not null) profile.Sku = WmiProbe.GetString(fallback, "SystemSKUNumber");
        }
        profile.SerialNumber = WmiProbe.GetString(row, "IdentifyingNumber");
        profile.SystemFamily = PickProductName(
            profile.SystemFamily,
            WmiProbe.GetString(row, "Version"),
            WmiProbe.GetString(row, "Name"));
    }

    private static void CollectBios(DeviceProfile profile)
    {
        var row = WmiProbe.Query(WmiProbe.CimV2, "Win32_BIOS",
            "SMBIOSBIOSVersion", "ReleaseDate", "Version", "Manufacturer").FirstOrDefault();
        if (row is null)
        {
            AppLog.Instance.Warn("未能读取 Win32_BIOS，BIOS 版本字段留空");
            return;
        }

        var version = WmiProbe.GetString(row, "SMBIOSBIOSVersion");
        if (version.Length == 0) version = WmiProbe.GetString(row, "Version");
        profile.BiosVersion = version;
        profile.BiosReleaseDate = WmiProbe.GetCimDate(row, "ReleaseDate");
    }

    private static void CollectBaseBoard(DeviceProfile profile)
    {
        var row = WmiProbe.Query(WmiProbe.CimV2, "Win32_BaseBoard", "Manufacturer", "Product").FirstOrDefault();
        if (row is null)
        {
            AppLog.Instance.Warn("未能读取 Win32_BaseBoard，主板信息留空");
            return;
        }

        profile.BoardVendor = WmiProbe.GetString(row, "Manufacturer");
        profile.BoardProduct = WmiProbe.GetString(row, "Product");
    }

    private static void CollectProcessor(DeviceProfile profile)
    {
        var row = WmiProbe.Query(WmiProbe.CimV2, "Win32_Processor",
            "Name", "NumberOfCores", "NumberOfLogicalProcessors").FirstOrDefault();
        if (row is null)
        {
            AppLog.Instance.Warn("未能读取 Win32_Processor，CPU 信息留空");
            return;
        }

        var name = Whitespace.Replace(WmiProbe.GetString(row, "Name"), " ").Trim();
        profile.CpuName = name.Length > 0 ? name : "未知";
        profile.CpuCores = (int)WmiProbe.GetLong(row, "NumberOfCores");
        profile.CpuThreads = (int)WmiProbe.GetLong(row, "NumberOfLogicalProcessors");
        if (profile.CpuThreads <= 0) profile.CpuThreads = profile.CpuCores;
    }

    /// <summary>Win32_VideoController：按厂商把显卡分成独显（NVIDIA/AMD）与核显（Intel）。</summary>
    private static void CollectVideoControllers(DeviceProfile profile)
    {
        var rows = WmiProbe.Query(WmiProbe.CimV2, "Win32_VideoController", "Name", "AdapterCompatibility", "DriverVersion");
        if (rows.Count == 0)
        {
            AppLog.Instance.Warn("未能读取 Win32_VideoController，显卡型号留空");
            return;
        }

        var discrete = new List<string>();
        var integrated = new List<string>();
        foreach (var row in rows)
        {
            var name = Whitespace.Replace(WmiProbe.GetString(row, "Name"), " ").Trim();
            if (name.Length == 0) continue;

            var vendor = WmiProbe.GetString(row, "AdapterCompatibility");
            var haystack = (name + " " + vendor).ToUpperInvariant();

            if (haystack.Contains("MICROSOFT BASIC DISPLAY", StringComparison.Ordinal) ||
                haystack.Contains("REMOTE DISPLAY", StringComparison.Ordinal) ||
                haystack.Contains("VIRTUAL", StringComparison.Ordinal))
            {
                AppLog.Instance.Info($"忽略虚拟/基础显示适配器：{name}");
                continue;
            }

            if (haystack.Contains("NVIDIA", StringComparison.Ordinal) ||
                haystack.Contains("AMD", StringComparison.Ordinal) ||
                haystack.Contains("RADEON", StringComparison.Ordinal))
            {
                discrete.Add(name);
            }
            else if (haystack.Contains("INTEL", StringComparison.Ordinal))
            {
                integrated.Add(name);
            }
            else
            {
                AppLog.Instance.Info($"未识别的显卡厂商，不计入独显/核显分类：{name}（{vendor}）");
            }
        }

        profile.GpuDiscrete = string.Join(" / ", discrete);
        profile.GpuIntegrated = string.Join(" / ", integrated);
        if (discrete.Count == 0 && integrated.Count == 0)
        {
            AppLog.Instance.Warn("Win32_VideoController 没有可识别的实体显卡（可能只有基础显示驱动）");
        }
    }

    private static void CollectOperatingSystem(DeviceProfile profile)
    {
        var row = WmiProbe.Query(WmiProbe.CimV2, "Win32_OperatingSystem",
            "Caption", "Version", "BuildNumber").FirstOrDefault();
        if (row is null)
        {
            AppLog.Instance.Warn("未能读取 Win32_OperatingSystem，系统信息留空");
            return;
        }

        profile.OsCaption = WmiProbe.GetString(row, "Caption");
        var build = WmiProbe.GetString(row, "BuildNumber");
        if (build.Length == 0) build = WmiProbe.GetString(row, "Version");
        profile.OsBuild = build;
    }

    /// <summary>只读探测联想 GameZone / 风扇表接口是否存在，并用只读方法确认是否真的能调用。</summary>
    private static LenovoProbeFacts ProbeLenovoInterfaces(DeviceProfile profile)
    {
        var gameZone = WmiProbe.ProbeClass(WmiProbe.WmiRoot, "LENOVO_GAMEZONE_DATA");
        var fanTable = WmiProbe.ProbeClass(WmiProbe.WmiRoot, "LENOVO_FAN_METHOD");
        profile.LenovoGameZonePresent = gameZone.Exists;
        profile.LenovoFanTablePresent = fanTable.Exists;

        var callOk = false;
        var callMessage = "未探测（LENOVO_GAMEZONE_DATA 不存在）";
        if (gameZone.Exists && gameZone.InstanceCount > 0)
        {
            var probe = WmiProbe.TryInvokeReadOnlyMethod(WmiProbe.WmiRoot, "LENOVO_GAMEZONE_DATA", "GetFanCount");
            callOk = probe.Ok;
            callMessage = probe.Message;
            if (callOk) AppLog.Instance.Info("联想 GameZone 只读方法 GetFanCount 调用成功：" + callMessage);
            else AppLog.Instance.Warn("联想 GameZone 只读方法 GetFanCount 调用失败：" + callMessage);
        }
        else if (gameZone.Exists)
        {
            callMessage = "LENOVO_GAMEZONE_DATA 类存在但没有任何实例";
            AppLog.Instance.Warn(callMessage);
        }

        AppLog.Instance.Info($"联想私有接口探测：GameZone={gameZone.Exists}（{gameZone.InstanceCount} 个实例），FanTable={fanTable.Exists}（{fanTable.InstanceCount} 个实例）");
        return new LenovoProbeFacts(gameZone, fanTable, callOk, callMessage);
    }

    /// <summary>根据厂商与型号推断平台分类，只用于决定走哪套调节适配器。</summary>
    internal static string ResolvePlatform(DeviceProfile profile)
    {
        var vendor = profile.Manufacturer ?? string.Empty;
        var isLenovo = vendor.Contains("LENOVO", StringComparison.OrdinalIgnoreCase) ||
                       vendor.Contains("联想", StringComparison.Ordinal);
        if (!isLenovo) return "通用平台";

        var haystack = string.Join(" ", new[]
        {
            profile.SystemFamily ?? string.Empty,
            profile.Model ?? string.Empty,
            profile.Sku ?? string.Empty,
            profile.BoardProduct ?? string.Empty
        }).ToUpperInvariant();

        if (haystack.Contains("LEGION", StringComparison.Ordinal) ||
            haystack.Contains("LOQ", StringComparison.Ordinal) ||
            haystack.Contains("拯救者", StringComparison.Ordinal))
        {
            return "Lenovo Legion";
        }

        if (haystack.Contains("XIAOXIN", StringComparison.Ordinal) ||
            haystack.Contains("小新", StringComparison.Ordinal) ||
            haystack.Contains("IDEAPAD", StringComparison.Ordinal) ||
            haystack.Contains("YOGA", StringComparison.Ordinal))
        {
            return "Lenovo 小新/IdeaPad";
        }

        AppLog.Instance.Info($"联想机型但未能识别产品线：SystemFamily={profile.SystemFamily}｜Model={profile.Model}｜SKU={profile.Sku}");
        return "通用平台";
    }

    private static string PickProductName(string systemFamily, string productVersion, string productName)
    {
        if (HasValue(systemFamily)) return systemFamily.Trim();
        if (HasValue(productVersion)) return productVersion.Trim();
        if (HasValue(productName)) return productName.Trim();
        return systemFamily?.Trim() ?? string.Empty;
    }

    private static bool HasValue(string? value) => !string.IsNullOrWhiteSpace(value) && !IsGenericValue(value.Trim());

    private static bool IsGenericValue(string value)
    {
        if (value.Length == 0) return true;
        if (value.All(char.IsDigit)) return true;
        return value.Equals("System Product Name", StringComparison.OrdinalIgnoreCase)
               || value.Equals("System Version", StringComparison.OrdinalIgnoreCase)
               || value.Equals("Default string", StringComparison.OrdinalIgnoreCase)
               || value.Equals("To be filled by O.E.M.", StringComparison.OrdinalIgnoreCase)
               || value.Equals("None", StringComparison.OrdinalIgnoreCase)
               || value.Equals("1.0", StringComparison.OrdinalIgnoreCase);
    }

    private static string OrUnknown(string value) => string.IsNullOrWhiteSpace(value) ? "未知" : value;
}
