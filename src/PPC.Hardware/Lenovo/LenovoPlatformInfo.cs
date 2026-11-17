using Microsoft.Win32;
using Ppc.Core.Services;

namespace Ppc.Hardware.Lenovo;

/// <summary>
/// 从注册表 BIOS 节点读取本机机型信息（只读，不涉及任何写入）。
/// 仅用于生成人类可读的支持说明，例如「联想 83AQ」。
/// </summary>
internal static class LenovoPlatformInfo
{
    private static readonly Lazy<(string Manufacturer, string Model, string Bios)> Cached =
        new(Read, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string Manufacturer => Cached.Value.Manufacturer;
    public static string Model => Cached.Value.Model;
    public static string BiosVersion => Cached.Value.Bios;

    public static bool IsLenovo =>
        Manufacturer.Contains("LENOVO", StringComparison.OrdinalIgnoreCase) ||
        Model.Contains("LENOVO", StringComparison.OrdinalIgnoreCase);

    /// <summary>本机的简短称呼，联想机型形如「联想 83AQ」；读不到时退回「未知型号」。</summary>
    public static string DisplayName
    {
        get
        {
            var model = string.IsNullOrWhiteSpace(Model) ? "未知型号" : Model.Trim();
            if (model.StartsWith("联想", StringComparison.Ordinal)) return model;
            if (IsLenovo) return "联想 " + model;
            var manufacturer = string.IsNullOrWhiteSpace(Manufacturer) ? string.Empty : Manufacturer.Trim();
            return string.IsNullOrEmpty(manufacturer) ? model : manufacturer + " " + model;
        }
    }

    private static (string Manufacturer, string Model, string Bios) Read()
    {
        var manufacturer = string.Empty;
        var model = string.Empty;
        var bios = string.Empty;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            if (key is not null)
            {
                manufacturer = key.GetValue("SystemManufacturer") as string ?? string.Empty;
                model = key.GetValue("SystemProductName") as string ?? string.Empty;
                bios = key.GetValue("BIOSVersion") as string ?? string.Empty;
            }
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("读取 BIOS 机型信息失败（将使用通用描述）：" + ex.Message);
        }

        return (manufacturer, model, bios);
    }
}
