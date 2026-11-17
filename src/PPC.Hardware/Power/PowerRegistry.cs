using Microsoft.Win32;
using Ppc.Core.Services;

namespace Ppc.Hardware.Power;

/// <summary>
/// 电源设置的注册表视图。powercfg /q 不输出隐藏设置（例如 PERFBOOSTMODE），
/// 这里按 Windows 官方存储结构读取每个方案的 AC/DC 覆盖值；只读为主，仅在还原时做一次清理。
/// </summary>
internal static class PowerRegistry
{
    private const string SchemesPath = @"SYSTEM\CurrentControlSet\Control\Power\User\PowerSchemes";
    private const string SettingsPath = @"SYSTEM\CurrentControlSet\Control\Power\PowerSettings";
    private const string OverlayAcValue = "ActiveOverlayAcPowerScheme";
    private const string OverlayDcValue = "ActiveOverlayDcPowerScheme";
    private const string AcSettingValue = "ACSettingIndex";
    private const string DcSettingValue = "DCSettingIndex";

    /// <summary>当前激活的电源方案 GUID（来自 ActivePowerScheme）。</summary>
    public static Guid? ReadActiveScheme()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SchemesPath, writable: false);
            if (key?.GetValue("ActivePowerScheme") is string text && Guid.TryParse(text, out var guid)) return guid;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("读取当前电源方案注册表值失败：" + ex.Message);
        }
        return null;
    }

    /// <summary>交流供电下的 Windows 11 电源模式覆盖；Guid.Empty 表示无覆盖。</summary>
    public static Guid? ReadOverlayAc() => ReadStringGuid(OverlayAcValue);

    /// <summary>电池供电下的 Windows 11 电源模式覆盖；Guid.Empty 表示无覆盖。</summary>
    public static Guid? ReadOverlayDc() => ReadStringGuid(OverlayDcValue);

    /// <summary>读取某个方案下处理器设置的 AC 或 DC 覆盖值；没写过则为 null（代表沿用系统默认）。</summary>
    public static int? ReadSetting(Guid scheme, Guid subgroup, Guid setting, bool useAc)
    {
        try
        {
            var path = $@"{SchemesPath}\{scheme:D}\{subgroup:D}\{setting:D}";
            using var key = Registry.LocalMachine.OpenSubKey(path, writable: false);
            if (key is null) return null;
            var value = key.GetValue(useAc ? AcSettingValue : DcSettingValue);
            return value switch
            {
                int intValue => intValue,
                long longValue => (int)longValue,
                _ => null
            };
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn($"读取电源设置注册表值失败（{setting:D}）：" + ex.Message);
            return null;
        }
    }

    /// <summary>
    /// 读取某个设置在指定电源方案下的「官方默认值」（PowerSettings\&lt;sub&gt;\&lt;setting&gt;\DefaultPowerSchemeValues\&lt;scheme&gt;）。
    /// 未提权时无法删除覆盖值，可用它把设置写回默认值，行为上与「系统默认」一致。
    /// </summary>
    public static int? ReadDefaultSetting(Guid scheme, Guid subgroup, Guid setting, bool useAc)
    {
        try
        {
            var path = $@"{SettingsPath}\{subgroup:D}\{setting:D}\DefaultPowerSchemeValues\{scheme:D}";
            using var key = Registry.LocalMachine.OpenSubKey(path, writable: false);
            var value = key?.GetValue(useAc ? AcSettingValue : DcSettingValue);
            return value switch
            {
                int intValue => intValue,
                long longValue => (int)longValue,
                _ => null
            };
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn($"读取电源设置默认值失败（{setting:D}）：" + ex.Message);
            return null;
        }
    }

    /// <summary>删除某个方案的处理器设置覆盖值，让该设置回到系统默认。需要管理员权限。</summary>
    public static bool TryClearSetting(Guid scheme, Guid subgroup, Guid setting, out string message)
    {
        var path = $@"{SchemesPath}\{scheme:D}\{subgroup:D}\{setting:D}";
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(path, writable: true);
            if (key is null)
            {
                message = "注册表项不存在，已是系统默认值";
                return true;
            }

            key.DeleteValue(AcSettingValue, throwOnMissingValue: false);
            key.DeleteValue(DcSettingValue, throwOnMissingValue: false);
            message = "已删除覆盖值";
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("删除电源设置注册表值失败：" + ex.Message);
            message = ex.Message;
            return false;
        }
    }

    private static Guid? ReadStringGuid(string valueName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(SchemesPath, writable: false);
            if (key?.GetValue(valueName) is string text && Guid.TryParse(text, out var guid)) return guid;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn($"读取注册表值 {valueName} 失败：" + ex.Message);
        }
        return null;
    }
}
