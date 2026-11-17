using System.Security.Principal;
using Ppc.Core.Abstractions;
using Ppc.Core.Models;
using Ppc.Core.Services;

namespace Ppc.Hardware.Detection;

/// <summary>
/// 设备检测入口：只用只读的系统接口（WMI、powercfg 查询、一次监控采样）把本机硬件与可用能力摸清楚。
/// 设计原则：
///   1. DetectAsync 不抛异常 —— 每一项探测都单独 try/catch，失败只留下原因；
///   2. 不写任何硬件 —— 不调 Set* / Write* 方法，不改电源方案，不碰 EC；
///   3. 拿不到数据时如实标记为「不支持」，并把原因写进 Capabilities，绝不编造数值。
/// </summary>
public sealed class DeviceDetectionService : IDeviceDetectionService
{
    /// <summary>单次监控采样最多等多久；超时就按「没采到数据」处理，避免启动界面被卡住。</summary>
    private static readonly TimeSpan MonitoringTimeout = TimeSpan.FromSeconds(12);

    private readonly IMonitorService _monitor;

    public DeviceDetectionService(IMonitorService monitor)
    {
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
    }

    public async Task<DeviceProfile> DetectAsync(CancellationToken cancellationToken = default)
    {
        var profile = new DeviceProfile();

        // 权限判断与监控采样互不依赖，但都很快，顺序执行便于把日志写清楚。
        profile.IsElevated = IsElevatedSafe();

        var lenovo = DeviceInfoCollector.Collect(profile);
        profile.LenovoGameZonePresent = lenovo.GameZone.Exists;
        profile.LenovoFanTablePresent = lenovo.FanTable.Exists;

        // 取消令牌只用来提前结束采样，不向外抛异常：检测要么返回完整结果，要么返回「带原因的不完整结果」。
        var monitoring = await ReadMonitoringAsync(cancellationToken).ConfigureAwait(false);

        List<CapabilityItem> capabilities;
        try
        {
            capabilities = CapabilityEvaluator.Evaluate(profile, lenovo, monitoring);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("能力评估失败，回退为全部不支持", ex);
            capabilities = new List<CapabilityItem>
            {
                new("detect.error", "设备能力评估", false, $"能力评估过程中出错：{ex.GetType().Name}：{ex.Message}")
            };
        }

        profile.Capabilities = capabilities;
        profile.DetectedAt = DateTimeOffset.Now;

        try
        {
            var supported = capabilities.Count(c => c.Supported);
            AppLog.Instance.Info(
                $"设备检测完成：{profile.DisplayName}（{profile.Platform}），CPU {profile.CpuCores}C/{profile.CpuThreads}T，" +
                $"独显「{profile.GpuDiscrete}」核显「{profile.GpuIntegrated}」，内存 {profile.TotalMemoryGb} GB，" +
                $"能力 {supported}/{capabilities.Count} 项可用，GameZone={profile.LenovoGameZonePresent}，风扇表={profile.LenovoFanTablePresent}，" +
                $"管理员={profile.IsElevated}；监控：{monitoring.Note}");
        }
        catch (Exception ex)
        {
            // 日志本身出错也不能影响检测结果。
            AppLog.Instance.Warn("写检测日志失败：" + ex.Message);
        }

        return profile;
    }

    /// <summary>
    /// 只调用一次 RefreshOnceAsync 取当前快照；任何异常、超时都转成「带原因的失败」，不让上层崩。
    /// </summary>
    private async Task<MonitoringProbe> ReadMonitoringAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (cancellationToken.IsCancellationRequested)
            {
                return new MonitoringProbe(HardwareSnapshot.Empty, "检测已取消，未采样监控数据");
            }

            var refresh = _monitor.RefreshOnceAsync();
            var timeout = Task.Delay(MonitoringTimeout, CancellationToken.None);
            var finished = await Task.WhenAny(refresh, timeout).ConfigureAwait(false);
            if (!ReferenceEquals(finished, refresh))
            {
                // 采样卡住时不再等：后台任务自行结束，本次检测按「无数据」处理。
                AppLog.Instance.Warn($"监控采样超过 {MonitoringTimeout.TotalSeconds:F0} 秒未返回，按无数据处理");
                return new MonitoringProbe(HardwareSnapshot.Empty,
                    $"监控采样超过 {MonitoringTimeout.TotalSeconds:F0} 秒未返回（硬件驱动可能无响应）");
            }

            var snapshot = await refresh.ConfigureAwait(false);
            if (snapshot is null)
            {
                return new MonitoringProbe(HardwareSnapshot.Empty, "监控后端返回了空快照");
            }

            var backends = DescribeBackends();
            var note = snapshot.Values.Count == 0
                ? $"监控后端（{backends}）没有返回任何读数"
                : $"监控后端：{backends}，采样 {snapshot.Values.Count} 项读数";
            return new MonitoringProbe(snapshot, note);
        }
        catch (OperationCanceledException)
        {
            return new MonitoringProbe(HardwareSnapshot.Empty, "监控采样被取消");
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("监控采样失败，本次检测按无监控数据处理", ex);
            return new MonitoringProbe(HardwareSnapshot.Empty,
                $"监控采样失败：{ex.GetType().Name}：{ex.Message}");
        }
    }

    private string DescribeBackends()
    {
        try
        {
            var backends = _monitor.ActiveBackends;
            if (backends is null || backends.Count == 0)
            {
                return _monitor.IsRunning ? "已启动但没有可用后端" : "监控未启动";
            }
            return string.Join(" + ", backends);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("读取监控后端列表失败：" + ex.Message);
            return "未知后端";
        }
    }

    /// <summary>
    /// 判断当前进程是否以管理员身份运行；任何异常（无令牌权限等）按「否」处理，绝不抛出。
    /// AppServices 里已有 ElevationHelper，这里保留一份静态实现供本程序集内部使用。
    /// </summary>
    public static bool IsElevatedSafe()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("管理员权限检测失败，按非管理员处理：" + ex.Message);
            return false;
        }
    }
}
