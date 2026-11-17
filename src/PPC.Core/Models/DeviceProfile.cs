namespace Ppc.Core.Models;

public sealed record CapabilityItem(string Key, string Title, bool Supported, string Detail);

public sealed class DeviceProfile
{
    public string Manufacturer { get; set; } = "未知";
    public string Model { get; set; } = "未知";
    public string SystemFamily { get; set; } = "";
    public string Sku { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public string BiosVersion { get; set; } = "";
    public string BiosReleaseDate { get; set; } = "";
    public string BoardVendor { get; set; } = "";
    public string BoardProduct { get; set; } = "";
    public string CpuName { get; set; } = "未知";
    public int CpuCores { get; set; }
    public int CpuThreads { get; set; }
    public string GpuDiscrete { get; set; } = "";
    public string GpuIntegrated { get; set; } = "";
    public string OsCaption { get; set; } = "";
    public string OsBuild { get; set; } = "";
    public long TotalMemoryBytes { get; set; }
    public string Platform { get; set; } = "通用平台";
    public bool LenovoGameZonePresent { get; set; }
    public bool LenovoFanTablePresent { get; set; }
    public bool IsElevated { get; set; }
    public List<CapabilityItem> Capabilities { get; set; } = new();
    public DateTimeOffset DetectedAt { get; set; } = DateTimeOffset.Now;

    public string DisplayName => string.IsNullOrWhiteSpace(SystemFamily) || SystemFamily == "未知"
        ? $"{Manufacturer} {Model}"
        : SystemFamily;

    public double TotalMemoryGb => Math.Round(TotalMemoryBytes / 1024d / 1024d / 1024d, 1);

    public bool Supports(string key) => Capabilities.Any(c => c.Key == key && c.Supported);
}
