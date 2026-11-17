using System.Collections.ObjectModel;
using System.Reflection;
using Ppc.App.Infrastructure;
using Ppc.App.ViewModels;
using Ppc.Core.Abstractions;
using Ppc.Core.Models;
using Ppc.Core.Services;
using Ppc.Hardware.Automation;
using Ppc.Hardware.Detection;
using Ppc.Hardware.Lenovo;
using Ppc.Hardware.Monitoring;
using Ppc.Hardware.Power;

namespace Ppc.App;

/// <summary>
/// 应用级服务容器。所有页面 ViewModel 通过构造函数拿到它，避免全局静态散射。
/// </summary>
public sealed class AppServices : ObservableObject
{
    private static AppServices? _current;
    private HardwareSnapshot _latest = HardwareSnapshot.Empty;
    private DeviceProfile _device = new();
    private IPageViewModel? _currentPage;
    private string _statusText = "正在初始化…";
    private bool _initialized;
    private bool _restoreDone;
    private volatile bool _firstSnapshotReceived;

    public AppServices()
    {
        _current = this;
        Store = new SettingsStore();
        Settings = Store.Load();
        Monitor = new LhmMonitorService();
        Detection = new DeviceDetectionService(Monitor);
        Performance = new PerformanceService(Store);
        Fan = new LenovoFanControlService(Monitor);
        History = new HistoryService(Settings.HistoryWindowSeconds / Math.Max(1, Settings.RefreshIntervalSeconds));
        Automation = new AutomationService(Settings, Store, Performance, Fan, Monitor);
        AppVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        LogDirectory = AppLog.Instance.LogFilePath;
    }

    public static AppServices Current => _current ?? throw new InvalidOperationException("AppServices 尚未初始化");

    public SettingsStore Store { get; }
    public AppSettings Settings { get; private set; }
    public IMonitorService Monitor { get; }
    public IDeviceDetectionService Detection { get; }
    public IPerformanceService Performance { get; }
    public IFanControlService Fan { get; }
    public IHistoryService History { get; }
    public AutomationService Automation { get; }
    public string LogDirectory { get; }
    public string AppVersion { get; }

    public ObservableCollection<IPageViewModel> Pages { get; } = new();

    public DeviceProfile Device { get => _device; private set => Set(ref _device, value); }
    public HardwareSnapshot Latest { get => _latest; private set => Set(ref _latest, value); }
    public bool IsElevated { get; private set; }

    public IPageViewModel? CurrentPage
    {
        get => _currentPage;
        set
        {
            if (!Set(ref _currentPage, value)) return;
            _ = SwitchPageAsync(value);
        }
    }

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public bool IsInitialized { get => _initialized; private set => Set(ref _initialized, value); }

    public event EventHandler<HardwareSnapshot>? SnapshotUpdated;
    public event EventHandler<DeviceProfile>? ProfileUpdated;

    /// <summary>启动流程：检测硬件 → 启动监控 → 恢复上次模式。</summary>
    public async Task InitializeAsync()
    {
        SetStatus("正在检测硬件…");
        try
        {
            IsElevated = ElevationHelper.IsElevated;
            Device = await Detection.DetectAsync();
            ProfileUpdated?.Invoke(this, Device);
            AppLog.Instance.Info($"设备检测完成：{Device.DisplayName}｜{Device.Platform}｜管理员={IsElevated}");
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("硬件检测失败", ex);
            SetStatus("硬件检测失败：" + ex.Message);
        }

        try
        {
            await Monitor.StartAsync(Math.Max(1000, Settings.RefreshIntervalSeconds * 1000));
            Monitor.SnapshotReady += OnSnapshot;
            AppLog.Instance.Info("监控已启动：" + string.Join(", ", Monitor.ActiveBackends));

            // 首次检测发生在监控启动之前：那时引擎还没读出温度，会把「CPU 监控」误判成不可用。
            // 等第一张真实快照落地后重算一次能力矩阵，页面随后才构建，保证显示的是真实能力。
            await WaitFirstSnapshotAsync(TimeSpan.FromSeconds(4));
            await RedetectCapabilitiesAsync();
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("监控启动失败", ex);
            SetStatus("监控不可用：" + ex.Message);
        }

        BuildPages();
        Automation.Start();

        if (Settings.Fan.Mode != FanControlMode.Auto)
        {
            await SafeApplyAsync("恢复风扇设置", () => Fan.SetModeAsync(Settings.Fan.Mode, Settings.Fan.ActiveCurve, Settings.Fan.ManualPercent));
        }

        IsInitialized = true;
        SetStatus($"就绪 · {Device.DisplayName}");
    }

    /// <summary>等待监控产出第一张快照；超时就算了，不当成错误。</summary>
    private async Task WaitFirstSnapshotAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!_firstSnapshotReceived && DateTime.UtcNow < deadline)
        {
            await Task.Delay(150).ConfigureAwait(true);
        }
    }

    /// <summary>监控出数后重算能力矩阵（只影响「能不能用」的判定，不会改任何硬件设置）。</summary>
    private async Task RedetectCapabilitiesAsync()
    {
        if (!_firstSnapshotReceived) return;

        try
        {
            var profile = await Detection.DetectAsync().ConfigureAwait(true);
            Device = profile;
            ProfileUpdated?.Invoke(this, profile);

            var available = profile.Capabilities.Count(c => c.Supported);
            AppLog.Instance.Info($"能力矩阵已按实时传感器重算：{available}/{profile.Capabilities.Count} 项可用");
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("重算能力矩阵失败（保留首次检测结果）", ex);
        }
    }

    private void BuildPages()
    {
        Pages.Clear();
        Pages.Add(new DashboardViewModel(this));
        Pages.Add(new PerformanceViewModel(this));
        Pages.Add(new FanControlViewModel(this));
        Pages.Add(new MonitoringViewModel(this));
        Pages.Add(new AutomationViewModel(this));
        Pages.Add(new SettingsViewModel(this));
        CurrentPage = Pages.FirstOrDefault();
    }

    private async Task SwitchPageAsync(IPageViewModel? page)
    {
        if (page is null) return;
        try
        {
            if (page is IActivatablePageForRefresh refreshable) await refreshable.RefreshAsync();
            else await page.ActivateAsync();
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error($"页面 {page.Key} 激活失败", ex);
        }
    }

    private void OnSnapshot(object? sender, HardwareSnapshot snapshot)
    {
        _firstSnapshotReceived = true;
        Latest = snapshot;
        try { History.Append(snapshot); }
        catch (Exception ex) { AppLog.Instance.Error("写入历史数据失败", ex); }

        SnapshotUpdated?.Invoke(this, snapshot);

        _ = Task.Run(async () =>
        {
            try { await Automation.EvaluateAsync(snapshot); }
            catch (Exception ex) { AppLog.Instance.Error("自动化规则执行失败", ex); }
        });
    }

    public async Task RefreshOnceAsync()
    {
        var snapshot = await Monitor.RefreshOnceAsync();
        OnSnapshot(this, snapshot);
    }

    public void SetStatus(string text)
    {
        StatusText = text;
        AppLog.Instance.Info("状态：" + text);
    }

    public async Task SaveSettingsAsync()
    {
        Settings.RefreshIntervalSeconds = Math.Clamp(Settings.RefreshIntervalSeconds, 1, 10);
        Store.Save(Settings);
        await Task.CompletedTask;
    }

    /// <summary>提升权限重启（硬件访问被拒绝时使用），当前实例会先正常退出并恢复控制权。</summary>
    public async Task RestartElevatedAsync()
    {
        await RestoreAllAsync();
        Infrastructure.ElevationHelper.RestartElevated();
    }

    /// <summary>退出前恢复系统原始性能/风扇策略。</summary>
    public async Task RestoreAllAsync()
    {
        if (_restoreDone) return;
        _restoreDone = true;
        if (!Settings.RestoreOnExit) return;

        await SafeApplyAsync("恢复性能策略", () => Performance.RestoreAsync());
        await SafeApplyAsync("恢复风扇自动控制", () => Fan.RestoreAutoAsync());
    }

    private static async Task SafeApplyAsync(string label, Func<Task<OpResult>> action)
    {
        try
        {
            var result = await action();
            AppLog.Instance.Info($"{label}：{(result.Success ? "成功" : "失败")} {result.Message}");
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error($"{label} 异常", ex);
        }
    }
}

/// <summary>可主动刷新的页面（切换进来时拉一次最新数据）。</summary>
public interface IActivatablePageForRefresh
{
    Task RefreshAsync();
}