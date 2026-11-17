using System.Windows.Threading;
using Ppc.App.Controls;
using Ppc.App.Infrastructure;
using Ppc.Core.Models;
using Ppc.Core.Services;

namespace Ppc.App.ViewModels;

/// <summary>
/// 硬件监控页：全部指标逐行列出（含来源与不可用原因），并提供温度 / 使用率 / 功耗三张历史曲线。
/// 采样间隔可选 1 / 2 / 5 秒，切换后真正重启监控服务；「清空历史」清空 HistoryService 中的缓冲。
/// </summary>
public sealed class MonitoringViewModel : ObservableObject, IPageViewModel
{
    private static readonly int[] IntervalChoices = { 1, 2, 5 };

    private readonly AppServices _app;
    private readonly Dispatcher? _dispatcher;
    private readonly ChartSeries _cpuTemp;
    private readonly ChartSeries _cpuUsage;
    private readonly ChartSeries _cpuPower;
    private readonly ChartSeries _gpuTemp;
    private readonly ChartSeries _gpuUsage;
    private readonly ChartSeries _gpuPower;

    private bool _subscribed;
    private bool _loading;
    private int _refreshIntervalSeconds;
    private string _samplingText = "正在启动采样…";
    private string _backendsText = "—";
    private string _historyMessage = "";
    private string _lastUpdatedText = "—";
    private bool _showUnavailable = true;
    private string _metricCountText = "";

    public MonitoringViewModel(AppServices app)
    {
        _app = app;
        _dispatcher = Application.Current?.Dispatcher;

        AllMetrics = new ObservableCollection<MetricTile>();
        CpuSeries = new ObservableCollection<ChartSeries>();
        GpuSeries = new ObservableCollection<ChartSeries>();

        _cpuTemp = new ChartSeries { Label = "CPU 温度", Stroke = FindBrush("State.Info", Color.FromRgb(0x4C, 0xC9, 0xF0)) };
        _cpuUsage = new ChartSeries { Label = "CPU 使用率", Stroke = FindBrush("Accent.Primary", Color.FromRgb(0x4F, 0x8B, 0xFF)) };
        _cpuPower = new ChartSeries { Label = "CPU 功耗", Stroke = FindBrush("State.Good", Color.FromRgb(0x3D, 0xDC, 0x97)) };
        _gpuTemp = new ChartSeries { Label = "GPU 温度", Stroke = FindBrush("State.Warn", Color.FromRgb(0xFF, 0xB5, 0x47)) };
        _gpuUsage = new ChartSeries { Label = "GPU 使用率", Stroke = FindBrush("Accent.GradientEnd", Color.FromRgb(0x9B, 0x6B, 0xFF)) };
        _gpuPower = new ChartSeries { Label = "GPU 功耗", Stroke = FindBrush("State.Bad", Color.FromRgb(0xFF, 0x5C, 0x6C)) };

        CpuSeries.Add(_cpuTemp);
        CpuSeries.Add(_cpuUsage);
        CpuSeries.Add(_cpuPower);
        GpuSeries.Add(_gpuTemp);
        GpuSeries.Add(_gpuUsage);
        GpuSeries.Add(_gpuPower);

        // 三张图各自两条线（CPU / GPU），单位一致才不会误读
        TemperatureChartSeries = new ObservableCollection<ChartSeries> { _cpuTemp, _gpuTemp };
        UsageChartSeries = new ObservableCollection<ChartSeries> { _cpuUsage, _gpuUsage };
        PowerChartSeries = new ObservableCollection<ChartSeries> { _cpuPower, _gpuPower };

        _loading = true;
        _refreshIntervalSeconds = NormalizeInterval(app.Settings.RefreshIntervalSeconds);
        _showUnavailable = app.Settings.ShowUnavailableMetrics;
        _loading = false;

        RebuildMetrics();
        RefreshCommand = new RelayCommand(() => _ = RefreshByUserAsync());
        ClearHistoryCommand = new RelayCommand(ClearHistory);

        Apply(app.Latest);
        UpdateSamplingText();
    }

    public string Key => "monitoring";
    public string Title => "硬件监控";
    public string Icon => "M3,20 L3,4 L5,4 L5,18 L21,18 L21,20 Z M6,15 L10,9 L14,12 L19,5 L20.5,6.5 L14.5,15 L10,12 L7,17 Z";
    public string Subtitle => "全部传感器读数与历史趋势";

    /// <summary>全部 Metric 一行一项（受「显示不可用项」设置影响）。</summary>
    public ObservableCollection<MetricTile> AllMetrics { get; }

    public ObservableCollection<ChartSeries> CpuSeries { get; }
    public ObservableCollection<ChartSeries> GpuSeries { get; }

    public ObservableCollection<ChartSeries> TemperatureChartSeries { get; }
    public ObservableCollection<ChartSeries> UsageChartSeries { get; }
    public ObservableCollection<ChartSeries> PowerChartSeries { get; }

    public IReadOnlyList<int> IntervalOptions => IntervalChoices;

    public string SamplingText { get => _samplingText; private set => Set(ref _samplingText, value); }
    public string BackendsText { get => _backendsText; private set => Set(ref _backendsText, value); }
    public string HistoryMessage { get => _historyMessage; private set => Set(ref _historyMessage, value); }
    public string LastUpdatedText { get => _lastUpdatedText; private set => Set(ref _lastUpdatedText, value); }
    public string MetricCountText { get => _metricCountText; private set => Set(ref _metricCountText, value); }

    /// <summary>采样间隔（秒），下拉只给 1 / 2 / 5 三档；改动后立即重启采样。</summary>
    public int RefreshIntervalSeconds
    {
        get => _refreshIntervalSeconds;
        set
        {
            var normalized = NormalizeInterval(value);
            if (!Set(ref _refreshIntervalSeconds, normalized)) return;
            if (_loading) return;
            _ = ApplyIntervalAsync(normalized);
        }
    }

    public bool ShowUnavailable
    {
        get => _showUnavailable;
        set
        {
            if (!Set(ref _showUnavailable, value)) return;
            _app.Settings.ShowUnavailableMetrics = value;
            RebuildMetrics();
            _ = SaveQuietlyAsync();
        }
    }

    public RelayCommand RefreshCommand { get; }
    public RelayCommand ClearHistoryCommand { get; }

    public async Task ActivateAsync()
    {
        Subscribe();
        _loading = true;
        _refreshIntervalSeconds = NormalizeInterval(_app.Settings.RefreshIntervalSeconds);
        Raise(nameof(RefreshIntervalSeconds));
        _showUnavailable = _app.Settings.ShowUnavailableMetrics;
        Raise(nameof(ShowUnavailable));
        _loading = false;

        RebuildMetrics();
        Apply(_app.Latest);
        UpdateSamplingText();
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
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        _app.SnapshotUpdated -= OnSnapshotUpdated;
        _subscribed = false;
    }

    private void OnSnapshotUpdated(object? sender, HardwareSnapshot snapshot)
        => OnUi(() => Apply(snapshot));

    private void Apply(HardwareSnapshot? snapshot)
    {
        try
        {
            if (snapshot is null) return;

            foreach (var tile in AllMetrics)
                tile.Update(snapshot[tile.Metric]);

            UpdateSeries(_cpuTemp, Metric.CpuTemperature);
            UpdateSeries(_cpuUsage, Metric.CpuUsage);
            UpdateSeries(_cpuPower, Metric.CpuPower);
            UpdateSeries(_gpuTemp, Metric.GpuTemperature);
            UpdateSeries(_gpuUsage, Metric.GpuUsage);
            UpdateSeries(_gpuPower, Metric.GpuPower);

            LastUpdatedText = snapshot.Timestamp.LocalDateTime.ToString("HH:mm:ss");
            UpdateSamplingText();
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("监控页刷新数据失败", ex);
        }
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

    /// <summary>按枚举顺序重建指标列表；「显示不可用项」关闭时跳过当前读不到的项。</summary>
    private void RebuildMetrics()
    {
        try
        {
            var keep = AllMetrics.ToList();
            AllMetrics.Clear();
            var snapshot = _app.Latest;
            var added = 0;
            var skipped = 0;

            foreach (Metric metric in Enum.GetValues(typeof(Metric)))
            {
                var existing = keep.FirstOrDefault(t => t.Metric == metric);
                var tile = existing ?? new MetricTile(metric);
                var reading = snapshot[metric];
                tile.Update(reading);

                if (!reading.HasValue && !_showUnavailable)
                {
                    skipped++;
                    continue;
                }

                AllMetrics.Add(tile);
                added++;
            }

            MetricCountText = skipped == 0
                ? string.Format("共 {0} 项指标", added)
                : string.Format("显示 {0} 项，{1} 项不可用已隐藏（可在设置页开启「显示不可用项」）", added, skipped);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("重建指标列表失败", ex);
        }
    }

    private void UpdateSamplingText()
    {
        try
        {
            var running = _app.Monitor.IsRunning;
            var backends = _app.Monitor.ActiveBackends;
            BackendsText = backends is { Count: > 0 } ? string.Join("、", backends) : "无可用后端";

            SamplingText = string.Format("{0} · 采样间隔 {1} 秒 · 最后更新 {2}",
                running ? "监控运行中" : "监控已停止", _refreshIntervalSeconds, LastUpdatedText);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("刷新采样状态失败", ex);
            SamplingText = "采样状态不可用：" + ex.Message;
        }
    }

    private async Task ApplyIntervalAsync(int seconds)
    {
        try
        {
            _app.Settings.RefreshIntervalSeconds = seconds;
            AppLog.Instance.Info("监控采样间隔改为 " + seconds + " 秒，正在重启采样…");
            await _app.Monitor.StartAsync(Math.Max(1000, seconds * 1000));
            await _app.SaveSettingsAsync();
            HistoryMessage = string.Format("采样间隔已切换为 {0} 秒（{1}）", seconds, DateTime.Now.ToString("HH:mm:ss"));
            UpdateSamplingText();
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("切换采样间隔失败", ex);
            HistoryMessage = "切换采样间隔失败：" + ex.Message;
            UpdateSamplingText();
        }
    }

    private void ClearHistory()
    {
        try
        {
            _app.History.Clear();
            _cpuTemp.Values = Array.Empty<double?>();
            _cpuTemp.Timestamps = Array.Empty<DateTimeOffset>();
            _gpuTemp.Values = Array.Empty<double?>();
            _gpuTemp.Timestamps = Array.Empty<DateTimeOffset>();
            _cpuUsage.Values = Array.Empty<double?>();
            _cpuUsage.Timestamps = Array.Empty<DateTimeOffset>();
            _gpuUsage.Values = Array.Empty<double?>();
            _gpuUsage.Timestamps = Array.Empty<DateTimeOffset>();
            _cpuPower.Values = Array.Empty<double?>();
            _cpuPower.Timestamps = Array.Empty<DateTimeOffset>();
            _gpuPower.Values = Array.Empty<double?>();
            _gpuPower.Timestamps = Array.Empty<DateTimeOffset>();

            foreach (var series in CpuSeries.Concat(GpuSeries)) series.Invalidate();
            HistoryMessage = "已清空历史曲线（" + DateTime.Now.ToString("HH:mm:ss") + "）";
            AppLog.Instance.Info("用户清空了历史曲线数据");
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("清空历史失败", ex);
            HistoryMessage = "清空历史失败：" + ex.Message;
        }
    }

    private async Task RefreshByUserAsync()
    {
        try
        {
            await _app.RefreshOnceAsync();
            HistoryMessage = "已刷新 " + DateTime.Now.ToString("HH:mm:ss");
            UpdateSamplingText();
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("监控页手动刷新失败", ex);
            HistoryMessage = "刷新失败：" + ex.Message;
            UpdateSamplingText();
        }
    }

    private async Task SaveQuietlyAsync()
    {
        try
        {
            await _app.SaveSettingsAsync();
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("保存设置失败", ex);
        }
    }

    private static int NormalizeInterval(int value)
    {
        foreach (var candidate in IntervalChoices)
            if (candidate == value) return candidate;
        return 2;
    }

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
            // 资源字典未就绪时退回固定画刷
        }

        return new SolidColorBrush(fallback);
    }
}
