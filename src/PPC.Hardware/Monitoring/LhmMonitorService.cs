using System.Linq;
using LibreHardwareMonitor.Hardware;
using Ppc.Core.Abstractions;
using Ppc.Core.Models;
using Ppc.Core.Services;

namespace Ppc.Hardware.Monitoring;

/// <summary>
/// 基于 LibreHardwareMonitor 0.9.4 的真实硬件监控实现。
/// 三条铁律：
///   1) 每个数字都来自真实传感器，读不到就 SetUnavailable(原因, 来源)，绝不补 0；
///   2) 任何一步异常都被吞掉并记日志，单次采样失败不会停掉整个监控；
///   3) 没有管理员权限导致内核驱动打不开时，降级为“全部不可用 + 明确原因”，绝不崩。
/// </summary>
public sealed class LhmMonitorService : IMonitorService
{
    /// <summary>1 GiB 的字节数。LibreHardwareMonitor 的 SensorType.Data 约定单位是 GB。</summary>
    private const double Gigabyte = 1024d * 1024d * 1024d;

    /// <summary>数据来源统一前缀，写进 MetricReading.Source。</summary>
    private const string LhmSource = "LibreHardwareMonitor";

    private static readonly Metric[] AllMetrics = Enum.GetValues<Metric>();

    private static readonly Metric[] CpuMetrics =
        { Metric.CpuTemperature, Metric.CpuUsage, Metric.CpuPower, Metric.CpuClock };

    private static readonly Metric[] GpuMetrics =
    {
        Metric.GpuTemperature, Metric.GpuUsage, Metric.GpuPower,
        Metric.GpuClock, Metric.GpuMemoryUsedPercent
    };

    private static readonly Metric[] MemoryMetrics =
    {
        Metric.MemoryUsedPercent, Metric.MemoryUsedBytes,
        Metric.MemoryTotalBytes, Metric.MemoryAvailableBytes
    };

    private static readonly Metric[] DiskMetrics = { Metric.DiskTemperature, Metric.DiskUsagePercent };

    private static readonly Metric[] BatteryMetrics =
        { Metric.BatteryPercent, Metric.BatteryRateWatts, Metric.AcOnline };

    /// <summary>串行化 LHM 的 Update()/Close()：Computer 不是线程安全的。</summary>
    private readonly SemaphoreSlim _sampleGate = new(1, 1);

    /// <summary>保护引擎状态（Computer 实例、后端描述、探测结果）。</summary>
    private readonly object _stateGate = new();

    /// <summary>已经记录过的硬件更新失败，避免每个采样周期刷日志。</summary>
    private readonly HashSet<string> _warned = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 本进程内见过哪些硬件类型。独显节点在引擎刚打开时可能还没枚举出来，
    /// 如果只看当前采样就会误判成「只有核显」，把 GPU 指标写成核显不可用。
    /// </summary>
    private readonly HashSet<HardwareType> _seenHardware = new();

    private Computer? _computer;
    private List<string> _backends = new();
    private string? _openFailure;
    private bool _openAttempted;
    private string? _engineIssue;
    private MetricReading _packagePowerLimit = MetricReading.Unavailable("尚未探测 CPU 功耗上限", "powercfg");
    private bool _packageLimitProbed;

    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private volatile bool _isRunning;
    private volatile bool _disposed;

    /// <summary>监控是否在运行（驱动打不开时也算“运行中”，只是每帧都返回不可用原因）。</summary>
    public bool IsRunning => _isRunning;

    /// <summary>实际生效的后端说明；不可用时这里会写明原因。</summary>
    public IReadOnlyList<string> ActiveBackends
    {
        get
        {
            lock (_stateGate) return _backends.ToArray();
        }
    }

    /// <summary>每次采样完成后抛出完整快照，UI 线程订阅即可。</summary>
    public event EventHandler<HardwareSnapshot>? SnapshotReady;

    /// <summary>最近一次判定出的引擎问题（诊断导出用）。</summary>
    internal string? EngineIssue => _engineIssue;

    /// <summary>以指定间隔启动采样；幂等，重复调用不会创建第二个定时器。</summary>
    public async Task StartAsync(int intervalMilliseconds)
    {
        if (_disposed) return;

        var interval = Math.Clamp(intervalMilliseconds, 500, 60000);

        lock (_stateGate)
        {
            if (_isRunning) return;
            _isRunning = true;
        }

        // 打开硬件引擎：失败不抛异常，原因写进 ActiveBackends 与日志。
        // LHM 的 Open() 要加载内核驱动（本机实测约 1.5 秒），放到线程池执行，别卡 UI 线程
        await Task.Run(EnsureComputer).ConfigureAwait(false);

        // 预热：LibreHardwareMonitor 首次 Update 常常只填充一部分传感器，
        // 先静默多采两拍（结果丢弃）再对外发布，避免首屏把「还没读到」误显示成「不支持」。
        await WarmUpAsync().ConfigureAwait(false);

        // 静态信息（CPU 功耗上限）后台探测，不阻塞启动流程
        _ = Task.Run(ProbePackagePowerLimit);

        StartSamplingLoop(interval);

        AppLog.Instance.Info("监控已启动，采样间隔 " + interval + " ms；后端：" + string.Join("，", ActiveBackends));
    }

    /// <summary>停止采样并关闭 LibreHardwareMonitor（释放内核驱动句柄）。</summary>
    public async Task StopAsync()
    {
        Task? loop;
        CancellationTokenSource? cts;

        lock (_stateGate)
        {
            _isRunning = false;
            loop = _loopTask;
            cts = _loopCts;
            _loopCts = null;
            _loopTask = null;
        }

        try
        {
            cts?.Cancel();
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("取消采样循环时异常：" + ex.Message);
        }

        if (loop is not null)
        {
            try
            {
                await Task.WhenAny(loop, Task.Delay(3000)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Instance.Warn("等待采样循环退出时异常：" + ex.Message);
            }
        }

        cts?.Dispose();

        var gated = false;
        try
        {
            gated = await _sampleGate.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("停止监控时等待采样锁异常：" + ex.Message);
        }

        try
        {
            CloseComputer();
        }
        finally
        {
            if (gated) _sampleGate.Release();
        }

        AppLog.Instance.Info("监控已停止，硬件访问引擎已关闭");
    }

    /// <summary>立即采样一次。任何情况下都返回快照对象，不抛异常（不可用时逐项写明原因）。</summary>
    public async Task<HardwareSnapshot> RefreshOnceAsync()
    {
        if (_disposed) return UnavailableSnapshot("监控服务已释放", "LhmMonitorService");

        bool gated;
        try
        {
            gated = await _sampleGate.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("获取采样锁失败", ex);
            return UnavailableSnapshot("获取采样锁失败：" + ex.Message, "LhmMonitorService");
        }

        if (!gated) return UnavailableSnapshot("上一次采样尚未完成，本次跳过", "LhmMonitorService");

        try
        {
            return await Task.Run(BuildSnapshotSafely).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("采样出现未捕获异常（本次快照标记为不可用）", ex);
            return UnavailableSnapshot("采样异常：" + ex.Message, "LhmMonitorService");
        }
        finally
        {
            _sampleGate.Release();
        }
    }

    /// <summary>释放资源：停止采样 + 关闭驱动。重复调用安全。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            StopAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("释放监控服务时异常", ex);
        }

        try
        {
            _sampleGate.Dispose();
        }
        catch
        {
            // 关进程阶段，句柄释放失败无关紧要
        }
    }

    // ---------------------------------------------------------------- 采样循环

    /// <summary>PeriodicTimer + Task 驱动采样，运行在线程池上，不占用 UI 线程。</summary>
    /// <summary>预热采样：丢弃前两拍，让首个对外快照就带上真实读数。</summary>
    private async Task WarmUpAsync()
    {
        for (var i = 0; i < 2; i++)
        {
            try
            {
                await RefreshOnceAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                AppLog.Instance.Warn("预热采样失败（忽略，继续启动）：" + ex.Message);
            }

            try
            {
                await Task.Delay(200).ConfigureAwait(false);
            }
            catch
            {
                // 启动阶段不接受取消，忽略
            }
        }
    }

    private void StartSamplingLoop(int intervalMilliseconds)
    {
        var cts = new CancellationTokenSource();
        var token = cts.Token;

        lock (_stateGate)
        {
            _loopCts = cts;
            _loopTask = Task.Run(() => SampleLoopAsync(intervalMilliseconds, token), token);
        }
    }

    private async Task SampleLoopAsync(int intervalMilliseconds, CancellationToken token)
    {
        // 立即采一次，让界面首帧就有真实数据
        try
        {
            Emit(await RefreshOnceAsync().ConfigureAwait(false));
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("首次采样失败", ex);
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMilliseconds));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                if (_disposed) break;
                try
                {
                    Emit(await RefreshOnceAsync().ConfigureAwait(false));
                }
                catch (Exception ex)
                {
                    // 单次失败只跳过这一拍，监控继续跑
                    AppLog.Instance.Error("定时采样失败（已跳过本次，监控继续）", ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常停止路径
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("采样循环异常退出", ex);
        }
    }

    private void Emit(HardwareSnapshot snapshot)
    {
        if (_disposed) return;
        try
        {
            SnapshotReady?.Invoke(this, snapshot);
        }
        catch (Exception ex)
        {
            // 订阅方（UI）异常不能把监控带崩
            AppLog.Instance.Error("快照订阅者处理异常", ex);
        }
    }

    // ---------------------------------------------------------------- 引擎生命周期

    /// <summary>打开 LibreHardwareMonitor；只尝试一次，失败原因记录在 _openFailure。</summary>
    private void EnsureComputer()
    {
        lock (_stateGate)
        {
            if (_computer is not null || _openAttempted) return;
            _openAttempted = true;
        }

        Computer? computer = null;
        try
        {
            computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsStorageEnabled = true,
                IsMotherboardEnabled = true,
                IsBatteryEnabled = true,
                IsControllerEnabled = true
            };

            computer.Open();

            var backends = BuildBackends(computer);
            lock (_stateGate)
            {
                _computer = computer;
                _backends = backends;
                _openFailure = null;
                _engineIssue = null;
            }

            AppLog.Instance.Info("硬件访问引擎已打开：" + string.Join("，", backends));
        }
        catch (Exception ex)
        {
            try
            {
                computer?.Close();
            }
            catch
            {
                // 打开失败后再关闭也可能失败，忽略
            }

            var reason = DescribeOpenFailure(ex);
            lock (_stateGate)
            {
                _openFailure = reason;
                _backends = new List<string> { "监控不可用：" + reason };
            }

            AppLog.Instance.Error("硬件访问引擎打开失败：" + reason, ex);
        }
    }

    /// <summary>把打开失败翻译成用户能看懂的原因（优先指出权限问题）。</summary>
    private static string DescribeOpenFailure(Exception ex)
    {
        if (!SystemPowerProbe.IsElevated)
        {
            return "需要管理员权限：LibreHardwareMonitor 无法加载内核驱动（" + ex.GetType().Name + "）。请以管理员身份重新运行本程序";
        }

        return "无法加载硬件访问驱动（" + ex.GetType().Name + "：" + ex.Message + "），可能被其它监控软件（HWiNFO / AIDA64 等）独占";
    }

    private void CloseComputer()
    {
        Computer? computer;
        lock (_stateGate)
        {
            computer = _computer;
            _computer = null;
            _openAttempted = false;
        }

        if (computer is null) return;

        try
        {
            computer.Close();
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("关闭 LibreHardwareMonitor 时异常：" + ex.Message);
        }
    }

    /// <summary>根据真实硬件节点组成后端描述，绝不写实际上没生效的后端。</summary>
    private static List<string> BuildBackends(Computer computer)
    {
        var labels = new List<string>();
        try
        {
            foreach (var hardware in computer.Hardware)
            {
                var label = hardware.HardwareType switch
                {
                    HardwareType.Cpu => "CPU",
                    HardwareType.GpuNvidia => "GPU-NVIDIA",
                    HardwareType.GpuAmd => "GPU-AMD",
                    HardwareType.GpuIntel => "GPU-Intel",
                    HardwareType.Memory => "内存",
                    HardwareType.Storage => "存储",
                    HardwareType.Motherboard => "主板",
                    HardwareType.SuperIO => "SuperIO",
                    HardwareType.EmbeddedController => "EC",
                    HardwareType.Battery => "电池",
                    HardwareType.Network => "网卡",
                    HardwareType.Cooler => "散热器",
                    _ => string.Empty
                };

                if (label.Length > 0 && !labels.Contains(label)) labels.Add(label);
            }
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("枚举硬件节点失败：" + ex.Message);
        }

        var version = Guard.Safe(
            () => typeof(Computer).Assembly.GetName().Version?.ToString(3) ?? "未知版本",
            "未知版本");

        var backends = new List<string>
        {
            LhmSource + " " + version + "（" + (labels.Count > 0 ? string.Join(" / ", labels) : "未发现任何硬件节点") + "）"
        };

        if (SystemPowerProbe.ReadPowerStatus() is not null)
        {
            backends.Add("Kernel32 GetSystemPowerStatus（交流电 / 电池状态）");
        }

        if (SystemPowerProbe.ReadMemoryStatus() is not null)
        {
            backends.Add("Kernel32 GlobalMemoryStatusEx（内存用量核对）");
        }

        return backends;
    }

    private void ProbePackagePowerLimit()
    {
        try
        {
            lock (_stateGate)
            {
                if (_packageLimitProbed) return;
            }

            var probe = CpuLimitProbe.Probe();
            lock (_stateGate)
            {
                _packagePowerLimit = probe;
                _packageLimitProbed = true;
            }

            var text = probe.Value is { } watts
                ? watts.ToString("0.#") + " W"
                : probe.UnavailableReason ?? "不可用";
            AppLog.Instance.Info("CPU 功耗上限探测结果：" + text);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("探测 CPU 功耗上限失败", ex);
        }
    }

    private MetricReading CurrentPackagePowerLimit()
    {
        lock (_stateGate)
        {
            if (_packageLimitProbed) return _packagePowerLimit;
        }

        MetricReading probe;
        try
        {
            probe = CpuLimitProbe.Probe();
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("探测 CPU 功耗上限失败", ex);
            probe = MetricReading.Unavailable(
                "探测 CPU 功耗上限时异常：" + ex.Message, "powercfg / WMI Win32_Processor");
        }

        lock (_stateGate)
        {
            _packagePowerLimit = probe;
            _packageLimitProbed = true;
        }

        return probe;
    }

    /// <summary>导出完整硬件树 + 风扇清单（供设置页“导出诊断”）。复用现有引擎，不会重开或关闭驱动。</summary>
    internal async Task<string> DumpHardwareTreeAsync()
    {
        if (_disposed) return "监控服务已释放，无法导出硬件树。";

        EnsureComputer();

        Computer? computer;
        lock (_stateGate) computer = _computer;

        if (computer is null)
        {
            return _openFailure is not null
                ? "硬件访问引擎未启动：" + _openFailure
                : "监控服务未启动，无法导出硬件树（DiagnosticsProbe.CollectAsync 可不依赖该服务单独采集）";
        }

        bool gated;
        try
        {
            gated = await _sampleGate.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return "导出诊断时等待采样锁失败：" + ex.Message;
        }

        if (!gated) return "导出诊断超时：采样任务仍在占用硬件访问，请稍后重试。";

        try
        {
            UpdateHardwareTree(computer);
            return DiagnosticsProbe.Describe(computer);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("导出硬件树失败", ex);
            return "导出硬件树失败：" + ex.GetType().Name + "：" + ex.Message;
        }
        finally
        {
            _sampleGate.Release();
        }
    }

    // ---------------------------------------------------------------- 快照组装

    /// <summary>完整的单次采样：刷新硬件树 → 映射指标。内部逐段兜底，绝不向外抛。</summary>
    private HardwareSnapshot BuildSnapshotSafely()
    {
        if (_disposed) return UnavailableSnapshot("监控服务已释放", "LhmMonitorService");

        Computer? computer;
        lock (_stateGate) computer = _computer;

        if (computer is null)
        {
            // 引擎为空有两种可能：一是服务未启动或已 Stop（正常状态），二是 Open 失败（_openFailure 有值）
            var reason = _openFailure ?? (_isRunning
                ? "监控引擎尚未就绪（首次采样可能仍在初始化）"
                : "监控服务当前已停止：调用 StartAsync 后才会采集数据");
            return UnavailableSnapshot(reason, LhmSource);
        }

        try
        {
            UpdateHardwareTree(computer);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("刷新硬件数据失败", ex);
            return UnavailableSnapshot("刷新硬件数据失败：" + ex.Message, LhmSource);
        }

        var inventory = SensorInventory.Capture(computer.Hardware);

        lock (_seenHardware)
        {
            foreach (var hardware in computer.Hardware) _seenHardware.Add(hardware.HardwareType);
        }

        // 驱动没真正激活时（典型原因是没有管理员权限），按契约全部标记为不可用并给原因
        var issue = DetectEngineIssue(inventory);
        _engineIssue = issue;
        if (issue is not null && !SystemPowerProbe.IsElevated)
        {
            WarnOnce("engine", "监控降级：" + issue);
            return UnavailableSnapshot(issue, LhmSource);
        }

        if (issue is null)
        {
            lock (_warned) _warned.Remove("engine");
        }
        else
        {
            WarnOnce("engine", "监控部分降级：" + issue);
        }

        var fans = ReadFans(inventory);
        var snapshot = new HardwareSnapshot { Timestamp = DateTimeOffset.Now, Fans = fans };

        for (var i = 0; i < fans.Count && i < 4; i++)
        {
            snapshot.SetReading(FanMetric(i), fans[i]);
        }

        RunSection(snapshot, "CPU", CpuMetrics, () => MapCpu(snapshot, inventory));
        RunSection(snapshot, "GPU", GpuMetrics, () => MapGpu(snapshot, inventory));
        RunSection(snapshot, "内存", MemoryMetrics, () => MapMemory(snapshot, inventory));
        RunSection(snapshot, "存储", DiskMetrics, () => MapStorage(snapshot, inventory));
        RunSection(snapshot, "电池/电源", BatteryMetrics, () => MapBattery(snapshot, inventory));

        snapshot.SetReading(Metric.CpuPackagePowerLimit, CurrentPackagePowerLimit());
        return snapshot;
    }

    private void RunSection(HardwareSnapshot snapshot, string label, Metric[] metrics, Action map)
    {
        try
        {
            map();
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("映射 " + label + " 指标失败（本段指标标记为不可用）", ex);
            foreach (var metric in metrics)
            {
                snapshot.SetUnavailable(metric, label + " 数据映射异常：" + ex.Message, LhmSource);
            }
        }
    }

    /// <summary>递归刷新硬件树（主板下的 EC / SuperIO 都在 SubHardware 里）。</summary>
    private void UpdateHardwareTree(Computer computer)
    {
        foreach (var hardware in computer.Hardware) UpdateHardware(hardware);
    }

    private void UpdateHardware(IHardware hardware)
    {
        try
        {
            hardware.Update();
        }
        catch (Exception ex)
        {
            WarnOnce(hardware.Identifier?.ToString() ?? hardware.Name ?? "未知硬件",
                "更新硬件 " + hardware.Name + " 失败：" + ex.Message);
        }

        var children = hardware.SubHardware;
        if (children is { Length: > 0 })
        {
            foreach (var child in children) UpdateHardware(child);
        }
    }

    /// <summary>只记一次同类警告，避免每秒刷屏。</summary>
    private void WarnOnce(string key, string message)
    {
        lock (_warned)
        {
            if (!_warned.Add(key)) return;
        }

        AppLog.Instance.Warn(message);
    }

    /// <summary>判断引擎是否真的读到了数据（例如非管理员时驱动未加载）。</summary>
    private static string? DetectEngineIssue(SensorInventory inventory)
    {
        if (!inventory.HasHardware(HardwareType.Cpu))
        {
            return "未检测到 CPU 硬件节点：LibreHardwareMonitor 的硬件访问驱动没有加载";
        }

        var cpuTemperatures = inventory.AllWhere(
            HardwareType.Cpu, SensorType.Temperature, sensor => !IsTjMaxSensor(sensor));

        var anyReadable = cpuTemperatures.Any(hit => IsPlausibleTemperature(hit.Reading));
        if (anyReadable) return null;

        if (!SystemPowerProbe.IsElevated)
        {
            return "需要管理员权限：LibreHardwareMonitor 无法加载内核驱动，CPU 温度 / 功耗 / 频率均读不到。请以管理员身份重新运行本程序。";
        }

        return "CPU 温度传感器全部无读数：内核驱动没有加载成功。常见原因有：① Windows 安全中心「内核隔离 / 内存完整性(HVCI)」开启，拦截了 WinRing0 驱动；"
             + "② 驱动文件被杀毒软件隔离；③ 被 HWiNFO / AIDA64 等其它监控软件独占。可先关闭内存完整性后重启，再以管理员身份运行本程序。";
    }

    /// <summary>整份快照全部不可用（带同一个原因），用于引擎不可用或未启动的场景。</summary>
    private static HardwareSnapshot UnavailableSnapshot(string reason, string source)
    {
        var fans = new List<MetricReading>(4);
        for (var i = 0; i < 4; i++) fans.Add(MetricReading.Unavailable(reason, source));

        var snapshot = new HardwareSnapshot { Timestamp = DateTimeOffset.Now, Fans = fans };
        foreach (var metric in AllMetrics) snapshot.SetUnavailable(metric, reason, source);
        return snapshot;
    }

    // ---------------------------------------------------------------- 指标映射

    /// <summary>CPU：温度（Package → Core Max → 各核平均）、负载、封装功耗、各核频率平均。</summary>
    private static void MapCpu(HardwareSnapshot snapshot, SensorInventory inventory)
    {
        if (!inventory.HasHardware(HardwareType.Cpu))
        {
            foreach (var metric in CpuMetrics)
            {
                snapshot.SetUnavailable(metric, "未检测到 CPU 硬件节点（硬件访问驱动可能未加载）", LhmSource);
            }

            return;
        }

        // ---- 温度：CPU Package → Core Max → 各核心平均 ----
        // 注意：Distance to TjMax 表示“距离过热点还有多少度”，不是温度本身，绝不能直接当温度用。
        var package = inventory.First(HardwareType.Cpu, SensorType.Temperature, "CPU Package");
        var coreMax = inventory.First(HardwareType.Cpu, SensorType.Temperature, "Core Max");
        var cores = inventory.AllWhere(
                HardwareType.Cpu, SensorType.Temperature,
                sensor => !IsTjMaxSensor(sensor)
                          && sensor.Name.StartsWith("CPU Core", StringComparison.OrdinalIgnoreCase))
            .Where(hit => IsPlausibleTemperature(hit.Reading))
            .ToList();

        if (package is { } pkg && IsPlausibleTemperature(pkg.Reading))
        {
            snapshot.SetReading(Metric.CpuTemperature,
                MetricReading.Ok(Math.Round(pkg.Reading, 1), LhmSource + "·CPU Package"));
        }
        else if (coreMax is { } max && IsPlausibleTemperature(max.Reading))
        {
            snapshot.SetReading(Metric.CpuTemperature,
                MetricReading.Ok(Math.Round(max.Reading, 1), LhmSource + "·Core Max"));
        }
        else if (cores.Count > 0)
        {
            snapshot.SetReading(Metric.CpuTemperature,
                MetricReading.Ok(Math.Round(cores.Average(hit => hit.Reading), 1),
                    LhmSource + "·CPU " + cores.Count + " 个核心平均"));
        }
        else
        {
            snapshot.SetUnavailable(Metric.CpuTemperature,
                "未找到可用的 CPU 温度传感器（CPU Package / Core Max / 各核心均无读数）", LhmSource);
        }

        // ---- 使用率：CPU Total Load ----
        var totalLoad = inventory.First(HardwareType.Cpu, SensorType.Load, "CPU Total");
        if (totalLoad is { } load && IsPlausiblePercent(load.Reading))
        {
            snapshot.SetReading(Metric.CpuUsage,
                MetricReading.Ok(Math.Round(load.Reading, 1), LhmSource + "·CPU Total"));
        }
        else
        {
            snapshot.SetUnavailable(Metric.CpuUsage, "未找到 CPU Total Load 传感器读值", LhmSource);
        }

        // ---- 功耗：CPU Package Power ----
        var packagePower = inventory.First(HardwareType.Cpu, SensorType.Power, "CPU Package")
                           ?? inventory.FirstWhere(HardwareType.Cpu, SensorType.Power,
                               sensor => sensor.Name.IndexOf("Core", StringComparison.OrdinalIgnoreCase) < 0);
        if (packagePower is { } power && IsPlausiblePower(power.Reading))
        {
            snapshot.SetReading(Metric.CpuPower, MetricReading.Ok(
                Math.Round(power.Reading, 1),
                LhmSource + "·" + power.SensorName));
        }
        else
        {
            snapshot.SetUnavailable(Metric.CpuPower, "未找到 CPU Package Power 传感器读值", LhmSource);
        }

        // ---- 频率：各核心 Clock 平均值（排除 Bus Speed）----
        var clocks = inventory.AllWhere(
                HardwareType.Cpu, SensorType.Clock,
                sensor => !sensor.Name.Contains("Bus", StringComparison.OrdinalIgnoreCase))
            .Where(hit => hit.Reading > 0 && hit.Reading < 20000)
            .ToList();

        if (clocks.Count > 0)
        {
            snapshot.SetReading(Metric.CpuClock, MetricReading.Ok(
                Math.Round(clocks.Average(hit => hit.Reading)),
                LhmSource + "·CPU " + clocks.Count + " 个核心频率平均"));
        }
        else
        {
            snapshot.SetUnavailable(Metric.CpuClock, "未找到各核心频率传感器读值", LhmSource);
        }
    }

    /// <summary>GPU：优先独显（NVIDIA），没有独显节点时退回核显。</summary>
    private void MapGpu(HardwareSnapshot snapshot, SensorInventory inventory)
    {
        bool Seen(HardwareType type)
        {
            if (inventory.HasHardware(type)) return true;
            lock (_seenHardware) return _seenHardware.Contains(type);
        }

        HardwareType? target = null;
        if (Seen(HardwareType.GpuNvidia)) target = HardwareType.GpuNvidia;
        else if (Seen(HardwareType.GpuAmd)) target = HardwareType.GpuAmd;
        else if (Seen(HardwareType.GpuIntel)) target = HardwareType.GpuIntel;

        if (target is not { } gpu)
        {
            foreach (var metric in GpuMetrics)
            {
                snapshot.SetUnavailable(metric, "未检测到 GPU 硬件节点（硬件访问驱动可能未加载）", LhmSource);
            }

            return;
        }

        var gpuName = gpu switch
        {
            HardwareType.GpuNvidia => "NVIDIA GPU",
            HardwareType.GpuAmd => "AMD GPU",
            _ => "Intel 核显"
        };

        // ---- 温度：GPU Core（不要 Hot Spot / Memory Junction）----
        var coreTemp = inventory.First(gpu, SensorType.Temperature, "GPU Core");
        if (coreTemp is not { } temp || !IsPlausibleTemperature(temp.Reading))
        {
            coreTemp = inventory.FirstWhere(gpu, SensorType.Temperature,
                sensor => !IsSecondaryGpuTemperature(sensor.Name));
        }

        if (coreTemp is { } t && IsPlausibleTemperature(t.Reading))
        {
            snapshot.SetReading(Metric.GpuTemperature, MetricReading.Ok(
                Math.Round(t.Reading, 1), LhmSource + "·" + gpuName + "/" + t.SensorName));
        }
        else
        {
            snapshot.SetUnavailable(Metric.GpuTemperature, gpuName + " 未提供可用的核心温度传感器", LhmSource);
        }

        // ---- 使用率：GPU Core Load ----
        var usage = inventory.First(gpu, SensorType.Load, "GPU Core");
        if (usage is not { } u || !IsPlausiblePercent(u.Reading))
        {
            usage = inventory.FirstWhere(gpu, SensorType.Load,
                sensor => !ContainsAny(sensor.Name, "Memory", "Total", "Limit"));
        }

        if (usage is { } usageHit && IsPlausiblePercent(usageHit.Reading))
        {
            snapshot.SetReading(Metric.GpuUsage, MetricReading.Ok(
                Math.Round(usageHit.Reading, 1), LhmSource + "·" + gpuName + "/" + usageHit.SensorName));
        }
        else
        {
            snapshot.SetUnavailable(Metric.GpuUsage, gpuName + " 未提供 GPU 核心负载传感器", LhmSource);
        }

        // ---- 功耗：GPU Package Power ----
        var gpuPower = inventory.First(gpu, SensorType.Power, "GPU Package", "GPU Core")
                       ?? inventory.FirstWhere(gpu, SensorType.Power,
                           sensor => sensor.Name.IndexOf("Memory", StringComparison.OrdinalIgnoreCase) < 0);

        if (gpuPower is { } power && IsPlausiblePower(power.Reading))
        {
            snapshot.SetReading(Metric.GpuPower, MetricReading.Ok(
                Math.Round(power.Reading, 1), LhmSource + "·" + gpuName + "/" + power.SensorName));
        }
        else
        {
            snapshot.SetUnavailable(Metric.GpuPower, gpuName + " 未提供功耗传感器（核显通常没有）", LhmSource);
        }

        // ---- 频率：GPU Core Clock ----
        var gpuClock = inventory.First(gpu, SensorType.Clock, "GPU Core")
                       ?? inventory.FirstWhere(gpu, SensorType.Clock,
                           sensor => sensor.Name.IndexOf("Memory", StringComparison.OrdinalIgnoreCase) < 0);

        if (gpuClock is { } clock && clock.Reading > 0 && clock.Reading < 20000)
        {
            snapshot.SetReading(Metric.GpuClock, MetricReading.Ok(
                Math.Round(clock.Reading), LhmSource + "·" + gpuName + "/" + clock.SensorName));
        }
        else
        {
            snapshot.SetUnavailable(Metric.GpuClock, gpuName + " 未提供核心频率传感器", LhmSource);
        }

        // ---- 显存占用：GPU Memory Load ----
        var vram = inventory.First(gpu, SensorType.Load, "GPU Memory");
        if (vram is not { } vramHit || !IsPlausiblePercent(vramHit.Reading))
        {
            vram = inventory.FirstWhere(gpu, SensorType.Load,
                sensor => sensor.Name.IndexOf("Memory", StringComparison.OrdinalIgnoreCase) >= 0
                          && !ContainsAny(sensor.Name, "Total"));
        }

        if (vram is { } memoryHit && IsPlausiblePercent(memoryHit.Reading))
        {
            snapshot.SetReading(Metric.GpuMemoryUsedPercent, MetricReading.Ok(
                Math.Round(memoryHit.Reading, 1), LhmSource + "·" + gpuName + "/" + memoryHit.SensorName));
        }
        else
        {
            snapshot.SetUnavailable(Metric.GpuMemoryUsedPercent, gpuName + " 未提供显存占用传感器", LhmSource);
        }
    }

    /// <summary>内存：优先 LHM 内存节点的 Load/Data，缺项用 GlobalMemoryStatusEx 真实值补齐并标注来源。</summary>
    private static void MapMemory(HardwareSnapshot snapshot, SensorInventory inventory)
    {
        const string osSource = "Kernel32 GlobalMemoryStatusEx";
        var status = SystemPowerProbe.ReadMemoryStatus();
        var hasNode = inventory.HasHardware(HardwareType.Memory);

        double? percent = null;
        var percentSource = LhmSource;

        if (hasNode)
        {
            var load = inventory.FirstWhere(HardwareType.Memory, SensorType.Load,
                sensor => sensor.Name.IndexOf("Virtual", StringComparison.OrdinalIgnoreCase) < 0);
            if (load is { } loadHit && IsPlausiblePercent(loadHit.Reading))
            {
                percent = loadHit.Reading;
                percentSource = LhmSource + "·" + loadHit.SensorName;
            }
        }

        double? used = null;
        double? available = null;
        double? total = null;
        var usedFromLhm = false;
        var availableFromLhm = false;
        var totalFromLhm = false;

        if (hasNode)
        {
            var usedRaw = RawValue(inventory.First(HardwareType.Memory, SensorType.Data, "Memory Used"));
            var availableRaw = RawValue(inventory.First(HardwareType.Memory, SensorType.Data, "Memory Available"));
            var totalRaw = RawValue(inventory.First(HardwareType.Memory, SensorType.Data, "Memory Total", "Total Memory"))
                           ?? (usedRaw.HasValue && availableRaw.HasValue ? usedRaw + availableRaw : null);

            // LHM 的 Data 传感器约定单位是 GB；用系统真实物理内存核对，差两个数量级就按 MB 重新解释
            var scale = Gigabyte;
            if (totalRaw.HasValue && status is { } check && check.TotalBytes > 0)
            {
                var ratio = totalRaw.Value * Gigabyte / check.TotalBytes;
                if (ratio < 0.5 || ratio > 2.0) scale = 1024d * 1024d;
            }

            if (usedRaw.HasValue)
            {
                used = usedRaw.Value * scale;
                usedFromLhm = true;
            }

            if (availableRaw.HasValue)
            {
                available = availableRaw.Value * scale;
                availableFromLhm = true;
            }

            if (totalRaw.HasValue)
            {
                total = totalRaw.Value * scale;
                totalFromLhm = true;
            }
        }

        // 缺项一律用系统真实值补齐（来源写 GlobalMemoryStatusEx，绝不编造）
        if (status is { } os)
        {
            total ??= os.TotalBytes;
            available ??= os.AvailableBytes;
            used ??= os.TotalBytes > os.AvailableBytes ? os.TotalBytes - os.AvailableBytes : 0;
            if (!percent.HasValue)
            {
                percent = os.LoadPercent;
                percentSource = osSource + "·dwMemoryLoad";
            }
        }

        if (percent is null && used is { } usedForPercent && total is { } totalForPercent && totalForPercent > 0)
        {
            percent = usedForPercent / totalForPercent * 100d;
            percentSource = "由已用内存 / 内存总量计算";
        }

        if (used is { } usedBytes)
        {
            snapshot.SetReading(Metric.MemoryUsedBytes, MetricReading.Ok(
                Math.Round(usedBytes), usedFromLhm ? LhmSource + "·Memory Used" : osSource + "·TotalPhys-AvailPhys"));
        }
        else
        {
            snapshot.SetUnavailable(Metric.MemoryUsedBytes, "内存节点与系统接口都没有给出已用内存", LhmSource);
        }

        if (total is { } totalSize)
        {
            snapshot.SetReading(Metric.MemoryTotalBytes, MetricReading.Ok(
                Math.Round(totalSize), totalFromLhm ? LhmSource + "·Memory Total" : osSource + "·ullTotalPhys"));
        }
        else
        {
            snapshot.SetUnavailable(Metric.MemoryTotalBytes, "内存节点与系统接口都没有给出内存总量", LhmSource);
        }

        if (available is { } freeBytes)
        {
            snapshot.SetReading(Metric.MemoryAvailableBytes, MetricReading.Ok(
                Math.Round(freeBytes), availableFromLhm ? LhmSource + "·Memory Available" : osSource + "·ullAvailPhys"));
        }
        else
        {
            snapshot.SetUnavailable(Metric.MemoryAvailableBytes, "内存节点与系统接口都没有给出可用内存", LhmSource);
        }

        if (percent is { } percentValue)
        {
            snapshot.SetReading(Metric.MemoryUsedPercent, MetricReading.Ok(
                Math.Round(percentValue, 1), percentSource));
        }
        else
        {
            snapshot.SetUnavailable(Metric.MemoryUsedPercent, "内存占用比例无法取得", LhmSource);
        }
    }

    /// <summary>存储：LHM 存储节点的温度与 Used Space 占比；没有再退回系统盘 DriveInfo。</summary>
    private static void MapStorage(HardwareSnapshot snapshot, SensorInventory inventory)
    {
        var hasNode = inventory.HasHardware(HardwareType.Storage);

        // ---- 温度 ----
        var temperature = hasNode
            ? inventory.First(HardwareType.Storage, SensorType.Temperature, "Temperature")
              ?? inventory.FirstWhere(HardwareType.Storage, SensorType.Temperature,
                  sensor => !ContainsAny(sensor.Name, "Warning", "Critical", "Max"))
            : null;

        if (temperature is { } temp && IsPlausibleTemperature(temp.Reading))
        {
            snapshot.SetReading(Metric.DiskTemperature, MetricReading.Ok(
                Math.Round(temp.Reading, 1), LhmSource + "·" + temp.Describe()));
        }
        else
        {
            snapshot.SetUnavailable(Metric.DiskTemperature,
                hasNode ? "存储设备未提供温度传感器" : "未检测到存储硬件节点（硬件访问驱动可能未加载）", LhmSource);
        }

        // ---- 占用比例 ----
        var usage = hasNode
            ? inventory.First(HardwareType.Storage, SensorType.Load, "Used Space")
              ?? inventory.FirstWhere(HardwareType.Storage, SensorType.Load,
                  sensor => sensor.Name.IndexOf("Used", StringComparison.OrdinalIgnoreCase) >= 0)
            : null;

        if (usage is { } usageHit && IsPlausiblePercent(usageHit.Reading))
        {
            snapshot.SetReading(Metric.DiskUsagePercent, MetricReading.Ok(
                Math.Round(usageHit.Reading, 1), LhmSource + "·" + usageHit.Describe()));
            return;
        }

        // LHM 只有 Data（GB）时用已用/总量算，仍然真实
        if (hasNode)
        {
            var usedGb = RawValue(inventory.First(HardwareType.Storage, SensorType.Data, "Used Space"));
            var totalGb = RawValue(inventory.First(HardwareType.Storage, SensorType.Data, "Total Space"));
            if (usedGb is { } used && totalGb is { } total && total > 0 && used >= 0)
            {
                snapshot.SetReading(Metric.DiskUsagePercent, MetricReading.Ok(
                    Math.Round(used / total * 100d, 1), LhmSource + "·Used Space / Total Space"));
                return;
            }
        }

        // 最后退回系统盘（还是真实数据，来源标注清楚）
        var drive = ReadSystemDriveUsage();
        if (drive.HasValue)
        {
            snapshot.SetReading(Metric.DiskUsagePercent, MetricReading.Ok(
                Math.Round(drive.Value, 1), "System.IO.DriveInfo·系统盘"));
        }
        else
        {
            snapshot.SetUnavailable(Metric.DiskUsagePercent,
                hasNode ? "存储节点没有 Used Space 读数，系统盘信息也读取失败" : "未检测到存储硬件节点", LhmSource);
        }
    }

    /// <summary>电池 / 交流电：LHM 电池节点 + GetSystemPowerStatus 交叉补齐。</summary>
    private static void MapBattery(HardwareSnapshot snapshot, SensorInventory inventory)
    {
        var status = SystemPowerProbe.ReadPowerStatus();
        var hasNode = inventory.HasHardware(HardwareType.Battery);

        // ---- 电量百分比 ----
        // LibreHardwareMonitor 把电池电量放在 SensorType.Level（部分版本是 Load），两种都认
        var level = hasNode
            ? inventory.First(HardwareType.Battery, SensorType.Level, "Charge Level", "Battery Level")
              ?? inventory.First(HardwareType.Battery, SensorType.Load, "Charge Level", "Battery Level")
              ?? inventory.FirstWhere(HardwareType.Battery, SensorType.Level,
                  sensor => sensor.Name.IndexOf("Charge", StringComparison.OrdinalIgnoreCase) >= 0)
              ?? inventory.FirstWhere(HardwareType.Battery, SensorType.Load,
                  sensor => sensor.Name.IndexOf("Charge", StringComparison.OrdinalIgnoreCase) >= 0)
            : null;

        if (level is { } levelHit && IsPlausiblePercent(levelHit.Reading))
        {
            snapshot.SetReading(Metric.BatteryPercent, MetricReading.Ok(
                Math.Round(levelHit.Reading), LhmSource + "·" + levelHit.Describe()));
        }
        else if (status?.BatteryPercent is { } systemPercent)
        {
            snapshot.SetReading(Metric.BatteryPercent, MetricReading.Ok(
                systemPercent, "Kernel32 GetSystemPowerStatus·BatteryLifePercent"));
        }
        else
        {
            snapshot.SetUnavailable(Metric.BatteryPercent,
                status is { NoBattery: true } ? "本机没有电池" : "电池电量无法读取", LhmSource);
        }

        // ---- 充放电功率 ----
        // 符号约定：正数 = 电池放电（电池对外供电）；负数 = 电池充电（适配器给电池充电）。
        // 界面显示名为“电池放电功率”，与 Windows 电源计一致。
        MetricReading rate = MetricReading.Unavailable("电池充放电功率无法读取", LhmSource);

        if (hasNode)
        {
            var discharge = inventory.First(HardwareType.Battery, SensorType.Power,
                "Discharge Rate", "Discharge Power", "Discharge");
            var charge = inventory.First(HardwareType.Battery, SensorType.Power,
                "Charge Rate", "Charge Power", "Charge");

            if (discharge is { } dischargeHit && IsPlausiblePower(dischargeHit.Reading))
            {
                rate = MetricReading.Ok(
                    NormalizeZero(Math.Round(dischargeHit.Reading, 1)), LhmSource + "·" + dischargeHit.SensorName);
            }
            else if (charge is { } chargeHit && IsPlausiblePower(chargeHit.Reading))
            {
                rate = MetricReading.Ok(
                    NormalizeZero(Math.Round(-chargeHit.Reading, 1)),
                    LhmSource + "·" + chargeHit.SensorName + "（充电中，记为负）");
            }
            else
            {
                var anyPower = inventory.FirstWhere(HardwareType.Battery, SensorType.Power, _ => true);
                if (anyPower is { } powerHit && IsPlausiblePower(powerHit.Reading))
                {
                    // LHM 的 Charge/Discharge Rate 传感器存的是绝对值（源码里就是 Math.Abs(rate)），
                    // 方向只能靠交流电状态推断：插电视为充电（负），不插电视为放电（正）。
                    var signed = status?.AcOnline == true ? -powerHit.Reading : powerHit.Reading;
                    rate = MetricReading.Ok(NormalizeZero(Math.Round(signed, 1)),
                        LhmSource + "·" + powerHit.SensorName + "（绝对值传感器，方向按交流电状态推断）");
                }
                else
                {
                    rate = ReadBatteryRateFromVoltageAndCurrent(inventory, status);
                }
            }
        }
        else
        {
            rate = ReadBatteryRateFromVoltageAndCurrent(inventory, status);
        }

        snapshot.SetReading(Metric.BatteryRateWatts, rate);

        // ---- 交流电在线状态 ----
        if (status?.AcOnline is { } acOnline)
        {
            snapshot.SetReading(Metric.AcOnline, MetricReading.Ok(
                acOnline ? 1 : 0,
                "Kernel32 GetSystemPowerStatus·ACLineStatus" + (acOnline ? "（已接交流电）" : "（使用电池）")));
        }
        else
        {
            snapshot.SetUnavailable(Metric.AcOnline, "系统未报告交流电状态", "Kernel32 GetSystemPowerStatus");
        }
    }

    /// <summary>没有功率传感器时用 电压 × 电流 换算（真实读数，不是估算常数）。</summary>
    private static MetricReading ReadBatteryRateFromVoltageAndCurrent(
        SensorInventory inventory, SystemPowerProbe.PowerStatus? status)
    {
        var voltage = inventory.FirstWhere(HardwareType.Battery, SensorType.Voltage, _ => true);
        var current = inventory.FirstWhere(HardwareType.Battery, SensorType.Current, _ => true);

        if (voltage is { } volt && current is { } amp && volt.Reading > 0 && Math.Abs(amp.Reading) > 0)
        {
            var watts = volt.Reading * Math.Abs(amp.Reading);
            var signed = status?.AcOnline == true ? -watts : watts;
            return MetricReading.Ok(NormalizeZero(Math.Round(signed, 1)),
                LhmSource + "·电压 × 电流换算（" + volt.SensorName + " × " + amp.SensorName + "）");
        }

        return MetricReading.Unavailable(
            "电池节点未提供功率/电流传感器，无法得到充放电功率", LhmSource);
    }

    /// <summary>把风扇传感器按硬件树顺序映射到 Fan1Rpm..Fan4Rpm。</summary>
    private static List<MetricReading> ReadFans(SensorInventory inventory)
    {
        var hits = inventory.AllOfType(SensorType.Fan)
            .Where(hit => hit.Reading >= 0)
            .ToList();

        var result = new List<MetricReading>(4);

        if (hits.Count == 0)
        {
            for (var i = 0; i < 4; i++) result.Add(MetricReading.Unavailable("本机未提供风扇转速传感器", LhmSource));
            return result;
        }

        for (var i = 0; i < 4; i++)
        {
            if (i < hits.Count)
            {
                result.Add(MetricReading.Ok(
                    Math.Round(hits[i].Reading), LhmSource + "·" + hits[i].Describe()));
            }
            else
            {
                result.Add(MetricReading.Unavailable(
                    "本机仅有 " + hits.Count + " 个风扇转速传感器，该通道不存在", LhmSource));
            }
        }

        return result;
    }

    // ---------------------------------------------------------------- 小工具

    private static Metric FanMetric(int index) => index switch
    {
        0 => Metric.Fan1Rpm,
        1 => Metric.Fan2Rpm,
        2 => Metric.Fan3Rpm,
        _ => Metric.Fan4Rpm
    };

    private static double? RawValue(SensorInventory.SensorHit? hit) => hit is { } value ? value.Reading : null;

    /// <summary>把 -0 规整成 0，避免界面出现“-0 W”这种读起来像 bug 的显示。</summary>
    private static double NormalizeZero(double value) => value == 0 ? 0 : value;

    /// <summary>Distance to TjMax 是“距离过热点还剩多少度”，不是温度，必须排除。</summary>
    private static bool IsTjMaxSensor(ISensor sensor)
    {
        var name = sensor.Name ?? string.Empty;
        return name.IndexOf("Distance", StringComparison.OrdinalIgnoreCase) >= 0
               || name.IndexOf("TjMax", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>GPU 上的热点 / 显存结温不是“核心温度”。</summary>
    private static bool IsSecondaryGpuTemperature(string? name)
        => ContainsAny(name, "Hot", "Junction", "Memory", "VRM");

    private static bool IsPlausibleTemperature(double value) => value > 0 && value < 150;

    private static bool IsPlausiblePercent(double value) => value >= 0 && value <= 100;

    private static bool IsPlausiblePower(double value) => value >= 0 && value < 2000;

    private static bool ContainsAny(string? text, params string[] fragments)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (var fragment in fragments)
        {
            if (text.IndexOf(fragment, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        }

        return false;
    }

    /// <summary>系统盘占用率（LHM 没有存储节点时的真实退路）。</summary>
    private static double? ReadSystemDriveUsage()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory);
            if (string.IsNullOrEmpty(root)) return null;

            var drive = new DriveInfo(root);
            if (!drive.IsReady || drive.TotalSize <= 0) return null;

            return (1d - (double)drive.AvailableFreeSpace / drive.TotalSize) * 100d;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("读取系统盘占用率失败：" + ex.Message);
            return null;
        }
    }
}
