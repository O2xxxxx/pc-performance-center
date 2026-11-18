using System.Windows.Threading;
using Ppc.App.Controls;
using Ppc.App.Infrastructure;
using Ppc.Core.Abstractions;
using Ppc.Core.Models;
using Ppc.Core.Services;

namespace Ppc.App.ViewModels;

/// <summary>
/// 首页仪表盘：KPI 指标卡 + CPU/GPU 温度趋势 + 当前状态汇总。
/// 数据全部来自 AppServices（真实快照 / 历史缓冲 / 性能与风扇服务），读不到就显示「不支持」和原因。
/// </summary>
public sealed class DashboardViewModel : ObservableObject, IPageViewModel
{
    private readonly AppServices _app;
    private readonly Dispatcher? _dispatcher;
    private readonly ChartSeries _cpuTempSeries;
    private readonly ChartSeries _gpuTempSeries;

    private bool _subscribed;
    private string _fanSnapshotReason = "";

    private string _deviceTitle = "正在识别设备…";
    private string _deviceSubtitle = "等待硬件检测完成";
    private string _platformText = "—";
    private string _modeTitle = "读取中…";
    private string _modeDescription = "正在读取当前性能模式与电源方案";
    private string _powerSourceText = "—";
    private string _batteryText = "—";
    private string _monitoringIssue = "";
    private bool _hasMonitoringIssue;
    private string _fanStatusText = "正在检测风扇传感器…";
    private bool _fanSupported;
    private string _lastUpdatedText = "—";
    private string _cpuPowerLimitText = "—";
    private string _memoryDetailText = "—";
    private string _diskDetailText = "—";
    private string _refreshMessage = "";

    public DashboardViewModel(AppServices app)
    {
        _app = app;
        _dispatcher = Application.Current?.Dispatcher;

        CpuTemp = new MetricTile(Metric.CpuTemperature, "CPU 温度");
        CpuUsage = new MetricTile(Metric.CpuUsage, "CPU 使用率");
        CpuPower = new MetricTile(Metric.CpuPower, "CPU 功耗");
        CpuClock = new MetricTile(Metric.CpuClock, "CPU 频率");
        GpuTemp = new MetricTile(Metric.GpuTemperature, "GPU 温度");
        GpuUsage = new MetricTile(Metric.GpuUsage, "GPU 使用率");
        GpuPower = new MetricTile(Metric.GpuPower, "GPU 功耗");
        GpuClock = new MetricTile(Metric.GpuClock, "GPU 频率");
        MemoryTile = new MetricTile(Metric.MemoryUsedPercent, "内存占用");
        DiskTile = new MetricTile(Metric.DiskTemperature, "硬盘温度");

        FanTiles = new ObservableCollection<MetricTile>
        {
            new(Metric.Fan1Rpm, "风扇 1"),
            new(Metric.Fan2Rpm, "风扇 2"),
            new(Metric.Fan3Rpm, "风扇 3"),
            new(Metric.Fan4Rpm, "风扇 4"),
        };

        TemperatureSeries = new ObservableCollection<ChartSeries>();

        _cpuTempSeries = new ChartSeries
        {
            Label = "CPU 温度",
            Stroke = FindBrush("State.Info", Color.FromRgb(0x4C, 0xC9, 0xF0))
        };
        _gpuTempSeries = new ChartSeries
        {
            Label = "GPU 温度",
            Stroke = FindBrush("State.Warn", Color.FromRgb(0xFF, 0xB5, 0x47))
        };
        TemperatureSeries.Add(_cpuTempSeries);
        TemperatureSeries.Add(_gpuTempSeries);

        RefreshCommand = new RelayCommand(() => _ = RefreshByUserAsync());

        ApplyDevice();
        Apply(_app.Latest);
    }

    public string Key => "dashboard";
    public string Title => "首页仪表盘";
    public string Icon => "M12,3 L21,11 L18,11 L18,20 L14,20 L14,14 L10,14 L10,20 L6,20 L6,11 L3,11 Z";
    public string Subtitle => "实时掌握 CPU / GPU / 内存与风扇状态";

    public MetricTile CpuTemp { get; }
    public MetricTile CpuUsage { get; }
    public MetricTile CpuPower { get; }
    public MetricTile CpuClock { get; }
    public MetricTile GpuTemp { get; }
    public MetricTile GpuUsage { get; }
    public MetricTile GpuPower { get; }
    public MetricTile GpuClock { get; }
    public MetricTile MemoryTile { get; }
    public MetricTile DiskTile { get; }

    public ObservableCollection<MetricTile> FanTiles { get; }

    /// <summary>CPU 温度 + GPU 温度两条曲线，绑定到 LineChart。</summary>
    public ObservableCollection<ChartSeries> TemperatureSeries { get; }

    public string DeviceTitle { get => _deviceTitle; private set => Set(ref _deviceTitle, value); }
    public string DeviceSubtitle { get => _deviceSubtitle; private set => Set(ref _deviceSubtitle, value); }
    public string PlatformText { get => _platformText; private set => Set(ref _platformText, value); }

    public string ModeTitle { get => _modeTitle; private set => Set(ref _modeTitle, value); }
    public string ModeDescription { get => _modeDescription; private set => Set(ref _modeDescription, value); }

    public string PowerSourceText { get => _powerSourceText; private set => Set(ref _powerSourceText, value); }
    public string BatteryText { get => _batteryText; private set => Set(ref _batteryText, value); }

    public string MonitoringIssue { get => _monitoringIssue; private set => Set(ref _monitoringIssue, value); }
    public bool HasMonitoringIssue { get => _hasMonitoringIssue; private set => Set(ref _hasMonitoringIssue, value); }

    /// <summary>风扇状态一句话说明：读不到时写明「不支持」与原因。</summary>
    public string FanStatusText { get => _fanStatusText; private set => Set(ref _fanStatusText, value); }
    public bool FanSupported { get => _fanSupported; private set => Set(ref _fanSupported, value); }

    public string LastUpdatedText { get => _lastUpdatedText; private set => Set(ref _lastUpdatedText, value); }
    public string CpuPowerLimitText { get => _cpuPowerLimitText; private set => Set(ref _cpuPowerLimitText, value); }
    public string MemoryDetailText { get => _memoryDetailText; private set => Set(ref _memoryDetailText, value); }
    public string DiskDetailText { get => _diskDetailText; private set => Set(ref _diskDetailText, value); }
    public string RefreshMessage { get => _refreshMessage; private set => Set(ref _refreshMessage, value); }

    public RelayCommand RefreshCommand { get; }

    public async Task ActivateAsync()
    {
        Subscribe();
        ApplyDevice();
        Apply(_app.Latest);
        await RefreshDerivedStateAsync();
        await RefreshByUserAsync();
    }

    public Task DeactivateAsync()
    {
        Unsubscribe();
        return Task.CompletedTask;
    }

    private void Subscribe()
    {
        if (_subscribed) return;
        _app.SnapshotUpdated += OnSnapshotUpdated;
        _app.ProfileUpdated += OnProfileUpdated;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        _app.SnapshotUpdated -= OnSnapshotUpdated;
        _app.ProfileUpdated -= OnProfileUpdated;
        _subscribed = false;
    }

    private void OnSnapshotUpdated(object? sender, HardwareSnapshot snapshot)
        => OnUi(() => Apply(snapshot));

    private void OnProfileUpdated(object? sender, DeviceProfile profile)
        => OnUi(ApplyDevice);

    /// <summary>把快照写进各个指标卡、曲线与状态文案。异常只记日志，不影响界面。</summary>
    private void Apply(HardwareSnapshot? snapshot)
    {
        try
        {
            if (snapshot is null) return;

            CpuTemp.Update(snapshot[Metric.CpuTemperature]);
            CpuUsage.Update(snapshot[Metric.CpuUsage]);
            CpuPower.Update(snapshot[Metric.CpuPower]);
            CpuClock.Update(snapshot[Metric.CpuClock]);
            GpuTemp.Update(snapshot[Metric.GpuTemperature]);
            GpuUsage.Update(snapshot[Metric.GpuUsage]);
            GpuPower.Update(snapshot[Metric.GpuPower]);
            GpuClock.Update(snapshot[Metric.GpuClock]);
            MemoryTile.Update(snapshot[Metric.MemoryUsedPercent]);
            DiskTile.Update(snapshot[Metric.DiskTemperature]);

            UpdateFanState(snapshot);
            UpdatePowerState(snapshot);
            UpdateCharts();
            UpdateDetailTexts(snapshot);

            LastUpdatedText = snapshot.Timestamp.LocalDateTime.ToString("HH:mm:ss");

            // 监控整体不可用时给一条明确的横幅，而不是让用户对着一堆「不支持」猜原因
            var cpuTemp = snapshot[Metric.CpuTemperature];
            var cpuUsage = snapshot[Metric.CpuUsage];
            if (snapshot.Timestamp == default)
            {
                MonitoringIssue = "尚未收到任何监控采样";
                HasMonitoringIssue = true;
            }
            else if (!cpuTemp.HasValue && !cpuUsage.HasValue)
            {
                var reason = cpuTemp.UnavailableReason ?? cpuUsage.UnavailableReason ?? "硬件访问被拒绝";
                MonitoringIssue = reason.Contains("管理员", StringComparison.Ordinal)
                    ? "当前没有管理员权限：CPU 温度 / 功耗 / 频率读不到。可在「软件设置」里点「以管理员身份重启」。"
                    : "监控不可用：" + reason;
                HasMonitoringIssue = true;
            }
            else
            {
                HasMonitoringIssue = false;
                MonitoringIssue = "";
            }
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("首页刷新数据失败", ex);
        }
    }

    /// <summary>风扇：快照里有 RPM 就用真实值；没有就按服务给出的原因明确显示「不支持」。</summary>
    private void UpdateFanState(HardwareSnapshot snapshot)
    {
        var anyValue = false;
        string? reason = null;

        for (var i = 0; i < FanTiles.Count; i++)
        {
            var metric = (Metric)((int)Metric.Fan1Rpm + i);
            var reading = snapshot[metric];
            FanTiles[i].Update(reading);
            if (reading.HasValue) anyValue = true;
            else reason ??= reading.UnavailableReason;
        }

        _fanSnapshotReason = reason ?? "";
        FanSupported = anyValue;

        if (anyValue) FanStatusText = "已读取到真实转速";
        else
        {
            var detail = SafeFanDetail();
            FanStatusText = "不支持：风扇转速不可读。原因：" + (string.IsNullOrWhiteSpace(_fanSnapshotReason)
                ? (string.IsNullOrWhiteSpace(detail) ? "系统未暴露风扇转速传感器，且厂商 WMI 接口调用失败。" : detail)
                : _fanSnapshotReason);
        }
    }

    private string SafeFanDetail()
    {
        try { return _app.Fan.SupportDetail ?? ""; }
        catch (Exception ex)
        {
            AppLog.Instance.Error("读取风扇支持信息失败", ex);
            return "";
        }
    }

    private void UpdatePowerState(HardwareSnapshot snapshot)
    {
        var ac = snapshot[Metric.AcOnline];
        if (ac.HasValue)
        {
            var online = ac.Value >= 0.5;
            PowerSourceText = online ? "电源适配器（已接通）" : "电池供电";
        }
        else
        {
            PowerSourceText = "不支持：" + (ac.UnavailableReason ?? "无法读取供电状态");
        }

        var percent = snapshot[Metric.BatteryPercent];
        var rate = snapshot[Metric.BatteryRateWatts];
        if (percent.HasValue)
        {
            BatteryText = string.Format("{0:0}%", percent.Value);
            if (rate.HasValue) BatteryText += string.Format("（{0:0.0} W）", rate.Value);
            else if (!string.IsNullOrWhiteSpace(rate.UnavailableReason)) BatteryText += "（功率不可读）";
        }
        else
        {
            BatteryText = "不支持：" + (percent.UnavailableReason ?? "无法读取电池状态");
        }
    }

    private void UpdateCharts()
    {
        UpdateSeries(_cpuTempSeries, Metric.CpuTemperature);
        UpdateSeries(_gpuTempSeries, Metric.GpuTemperature);
    }

    private void UpdateSeries(ChartSeries series, Metric metric)
    {
        var data = _app.History.GetSeries(metric);
        var values = new List<double?>(data.Count);
        var stamps = new List<DateTimeOffset>(data.Count);
        foreach (var (stamp, value) in data)
        {
            stamps.Add(stamp);
            values.Add(value);
        }

        series.Values = values;
        series.Timestamps = stamps;
        series.Invalidate();
    }

    private void UpdateDetailTexts(HardwareSnapshot snapshot)
    {
        var limit = snapshot[Metric.CpuPackagePowerLimit];
        CpuPowerLimitText = limit.HasValue
            ? string.Format("功耗上限 {0:0} W", limit.Value)
            : "功耗上限不可读";

        var usedBytes = snapshot[Metric.MemoryUsedBytes];
        var totalBytes = snapshot[Metric.MemoryTotalBytes];
        var available = snapshot[Metric.MemoryAvailableBytes];

        if (usedBytes.HasValue && totalBytes.HasValue && totalBytes.Value > 0)
        {
            MemoryDetailText = string.Format("已用 {0:0.0} GB / {1:0.0} GB", usedBytes.Value / 1024d / 1024d / 1024d,
                totalBytes.Value / 1024d / 1024d / 1024d);
            if (available.HasValue) MemoryDetailText += string.Format("，可用 {0:0.0} GB", available.Value / 1024d / 1024d / 1024d);
        }
        else
        {
            var reason = usedBytes.UnavailableReason ?? totalBytes.UnavailableReason ?? "内存容量不可读";
            MemoryDetailText = "不支持：" + reason;
        }

        var diskUsage = snapshot[Metric.DiskUsagePercent];
        DiskDetailText = diskUsage.HasValue
            ? string.Format("磁盘占用 {0:0}%", diskUsage.Value)
            : "磁盘占用不可读" + (string.IsNullOrWhiteSpace(diskUsage.UnavailableReason) ? "" : "：" + diskUsage.UnavailableReason);
    }

    private void ApplyDevice()
    {
        try
        {
            var device = _app.Device;
            if (device is null) return;

            DeviceTitle = string.IsNullOrWhiteSpace(device.DisplayName) ? "未知设备" : device.DisplayName;
            PlatformText = string.IsNullOrWhiteSpace(device.Platform) ? "通用平台" : device.Platform;

            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(device.Model)) parts.Add("型号 " + device.Model);
            if (device.CpuCores > 0) parts.Add(string.Format("{0} 核 {1} 线程", device.CpuCores, device.CpuThreads));
            if (!string.IsNullOrWhiteSpace(device.GpuDiscrete)) parts.Add(device.GpuDiscrete);
            DeviceSubtitle = parts.Count == 0 ? "尚未完成硬件检测" : string.Join(" · ", parts);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("刷新设备信息失败", ex);
            DeviceTitle = "设备信息不可用";
            DeviceSubtitle = ex.Message;
        }
    }

    /// <summary>读取性能模式与风扇能力（异步硬件调用，失败降级为文字说明）。</summary>
    private async Task RefreshDerivedStateAsync()
    {
        try
        {
            var state = await _app.Performance.GetStateAsync();
            ModeTitle = ModeName(state.Mode);
            var details = new List<string>();
            if (!string.IsNullOrWhiteSpace(state.PowerSchemeName)) details.Add("电源方案：" + state.PowerSchemeName);
            if (state.MaxProcessorState.HasValue) details.Add("最大处理器状态 " + state.MaxProcessorState + "%");
            if (!string.IsNullOrWhiteSpace(state.Summary)) details.Add(state.Summary);
            ModeDescription = details.Count == 0 ? "性能模式信息不可用" : string.Join(" · ", details);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("读取性能模式失败", ex);
            ModeTitle = "读取失败";
            ModeDescription = "读取性能模式时出错：" + ex.Message + "（已记录日志）";
        }
    }

    private static string ModeName(PerformanceMode mode) => mode switch
    {
        PerformanceMode.Eco => "节能模式",
        PerformanceMode.Balanced => "均衡模式",
        PerformanceMode.Performance => "高性能模式",
        PerformanceMode.Custom => "自定义模式",
        _ => mode.ToString()
    };

    private async Task RefreshByUserAsync()
    {
        try
        {
            RefreshMessage = "正在刷新…";
            await _app.RefreshOnceAsync();
            await RefreshDerivedStateAsync();
            RefreshMessage = "已刷新 " + DateTime.Now.ToString("HH:mm:ss");
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("手动刷新监控数据失败", ex);
            RefreshMessage = "刷新失败：" + ex.Message;
            MonitoringIssue = "监控刷新失败：" + ex.Message;
            HasMonitoringIssue = true;
        }
    }

    /// <summary>后台线程来的事件必须切回 UI 线程，否则绑定会抛异常。</summary>
    private void OnUi(Action action)
    {
        var dispatcher = _dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        try { dispatcher.BeginInvoke(action); }
        catch (Exception ex) { AppLog.Instance.Error("切回 UI 线程失败", ex); }
    }

    private static Brush FindBrush(string key, Color fallback)
    {
        try
        {
            if (Application.Current?.TryFindResource(key) is Brush brush) return brush;
        }
        catch
        {
            // 资源字典还没加载时退回固定画刷，保证曲线颜色不为空
        }

        return new SolidColorBrush(fallback);
    }
}
