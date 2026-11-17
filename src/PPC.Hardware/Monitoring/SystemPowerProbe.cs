using System.Management;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace Ppc.Hardware.Monitoring;

/// <summary>
/// 不依赖 LibreHardwareMonitor 的 Windows 原生电源/内存事实读取。
/// 只用 kernel32 的官方 P/Invoke（GetSystemPowerStatus / GlobalMemoryStatusEx），不引入任何未知驱动。
/// 任何调用都吞掉异常并返回 null，绝不把 UI 拖崩。
/// </summary>
internal static class SystemPowerProbe
{
    /// <summary>Win32 SYSTEM_POWER_STATUS，GetSystemPowerStatus 的返回结构。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        /// <summary>0=用电池，1=接交流电，255=未知。</summary>
        public byte AcLineStatus;

        /// <summary>电池状态位：1=高电 2=低电 4=临界 8=充电中 128=无电池 255=未知。</summary>
        public byte BatteryFlag;

        /// <summary>电量百分比，255=未知。</summary>
        public byte BatteryLifePercent;

        /// <summary>保留字段。</summary>
        public byte SystemStatusFlag;

        /// <summary>剩余秒数，-1=未知。</summary>
        public int BatteryLifeTime;

        /// <summary>充满后总秒数，-1=未知。</summary>
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    /// <summary>Win32 MEMORYSTATUSEX，GlobalMemoryStatusEx 的返回结构。</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    /// <summary>系统电源状态（交流电/电池电量）。任一项读不到就是 null，不猜。</summary>
    public readonly record struct PowerStatus(bool? AcOnline, int? BatteryPercent, bool Charging, bool NoBattery)
    {
        /// <summary>给界面/日志用的一句话说明。</summary>
        public string Describe()
        {
            var ac = AcOnline switch { true => "已接交流电", false => "正在用电池", _ => "交流电状态未知" };
            var battery = BatteryPercent is { } p ? "电量 " + p + "%" : "电量未知";
            return ac + "｜" + battery;
        }
    }

    /// <summary>系统内存状态（字节）。读不到返回 null。</summary>
    public readonly record struct MemoryStatus(ulong TotalBytes, ulong AvailableBytes, uint LoadPercent);

    /// <summary>当前进程是否具备管理员权限（LibreHardwareMonitor 加载内核驱动的前提）。</summary>
    public static bool IsElevated { get; } = DetectElevation();

    /// <summary>权限的中文说明，写进日志与诊断报告。</summary>
    public static string ElevationText => IsElevated ? "管理员" : "普通用户";

    /// <summary>读取交流电/电池状态；失败返回 null 并写日志。</summary>
    public static PowerStatus? ReadPowerStatus()
    {
        try
        {
            if (!GetSystemPowerStatus(out var status))
            {
                Ppc.Core.Services.AppLog.Instance.Warn("GetSystemPowerStatus 调用失败，错误码 " + Marshal.GetLastWin32Error());
                return null;
            }

            bool? ac = status.AcLineStatus switch
            {
                0 => false,
                1 => true,
                _ => null
            };

            int? percent = status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : null;
            var noBattery = status.BatteryFlag == 128;
            var charging = (status.BatteryFlag & 8) == 8;

            return new PowerStatus(ac, percent, charging, noBattery);
        }
        catch (Exception ex)
        {
            Ppc.Core.Services.AppLog.Instance.Error("读取系统电源状态失败", ex);
            return null;
        }
    }

    /// <summary>读取物理内存总量/可用量；失败返回 null 并写日志。</summary>
    public static MemoryStatus? ReadMemoryStatus()
    {
        try
        {
            var buffer = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(ref buffer))
            {
                Ppc.Core.Services.AppLog.Instance.Warn("GlobalMemoryStatusEx 调用失败，错误码 " + Marshal.GetLastWin32Error());
                return null;
            }

            if (buffer.TotalPhys == 0) return null;
            return new MemoryStatus(buffer.TotalPhys, buffer.AvailPhys, buffer.MemoryLoad);
        }
        catch (Exception ex)
        {
            Ppc.Core.Services.AppLog.Instance.Error("读取系统内存状态失败", ex);
            return null;
        }
    }

    /// <summary>读取 WMI 里的整机信息（型号/序列号），供诊断报告使用；失败返回空字符串。</summary>
    public static string ReadMachineText()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Manufacturer, Model, TotalPhysicalMemory FROM Win32_ComputerSystem");
            foreach (ManagementBaseObject item in searcher.Get())
            {
                var manufacturer = item["Manufacturer"]?.ToString() ?? "";
                var model = item["Model"]?.ToString() ?? "";
                return (manufacturer + " " + model).Trim();
            }
        }
        catch (Exception ex)
        {
            Ppc.Core.Services.AppLog.Instance.Warn("读取 Win32_ComputerSystem 失败：" + ex.Message);
        }

        return string.Empty;
    }

    private static bool DetectElevation()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex)
        {
            Ppc.Core.Services.AppLog.Instance.Warn("检测管理员权限失败：" + ex.Message);
            return false;
        }
    }
}
