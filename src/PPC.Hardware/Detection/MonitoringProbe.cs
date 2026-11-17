using Ppc.Core.Models;

namespace Ppc.Hardware.Detection;

/// <summary>
/// 检测阶段的一次监控采样结果：要么拿到真实快照，要么带着「为什么没拿到」的说明。
/// 检测只调用一次 IMonitorService.RefreshOnceAsync()，绝不启动/停止后台监控线程。
/// </summary>
internal sealed class MonitoringProbe
{
    internal MonitoringProbe(HardwareSnapshot snapshot, string note)
    {
        Snapshot = snapshot;
        Note = note;
    }

    internal HardwareSnapshot Snapshot { get; }

    /// <summary>采样说明：成功时是后端名与读数个数，失败时是失败原因（中文）。</summary>
    internal string Note { get; }

    internal bool HasData => Snapshot.Values.Count > 0;
}
