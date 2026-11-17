using System.Diagnostics;
using System.Security.Principal;
using System.Windows.Threading;
using Ppc.App.Infrastructure;
using Ppc.Core.Models;
using Ppc.Core.Services;

namespace Ppc.App.ViewModels;

/// <summary>
/// 设置页：主题、刷新与历史、开机启动、设备信息、能力矩阵和危险操作。
/// 所有开关都会真正落到 SettingsStore / 注册表 / 监控服务；失败一律如实回报，不伪装成功。
/// </summary>
public sealed class SettingsViewModel : ObservableObject, IPageViewModel
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "PCPerformanceCenter";

    private readonly AppServices _app;
    private readonly Dispatcher? _dispatcher;
    private bool _loading;

    private string _theme = "Dark";
    private string _language = "简体中文";
    private string _appVersion = "—";
    private string _dataDirectory = "—";
    private string _logPath = "—";
    private string _settingsFilePath = "—";
    private string _deviceSummary = "正在读取设备信息…";
    private string _biosText = "—";
    private string _capabilitiesText = "";
    private string _cpuText = "—";
    private string _gpuText = "—";
    private string _osText = "—";
    private string _serialText = "—";
    private string _memoryText = "—";
    private string _platformText = "—";
    private string _elevationText = "—";
    private bool _startWithWindows;
    private bool _minimizeToTray;
    private bool _restoreOnExit;
    private bool _showUnavailableMetrics;
    private int _historyWindowSeconds = 300;
    private string _saveMessage = "";
    private string _startupMessage = "";

    public SettingsViewModel(AppServices app)
    {
        _app = app;
        _dispatcher = Application.Current?.Dispatcher;

        Capabilities = new ObservableCollection<CapabilityItem>();

        SaveCommand = new RelayCommand(() => _ = SaveAsync());
        ResetSettingsCommand = new RelayCommand(() => _ = ReloadFromDiskAsync());
        OpenLogCommand = new RelayCommand(OpenLogDirectory);
        RestartElevatedCommand = new RelayCommand(() => _ = RestartElevatedAsync());
        RestoreDefaultsCommand = new RelayCommand(() => _ = RestoreFactoryDefaultsAsync());
        SetThemeCommand = new RelayCommand(parameter => ApplyTheme(parameter as string ?? "Dark"));
        OpenDataDirectoryCommand = new RelayCommand(OpenDataDirectory);

        LoadFromSettings();
        LoadDeviceInformation();
    }

    public string Key => "settings";
    public string Title => "软件设置";
    public string Icon => "M12,8 A4,4 0 1 0 12,16 A4,4 0 1 0 12,8 Z M12,2 L13.5,5 L17,4 L18,7.5 L21,9 L19.5,12 L21,15 L18,16.5 L17,20 L13.5,19 L12,22 L10.5,19 L7,20 L6,16.5 L3,15 L4.5,12 L3,9 L6,7.5 L7,4 L10.5,5 Z";
    public string Subtitle => "主题、刷新间隔与开机启动";

    public string Theme { get => _theme; private set => Set(ref _theme, value); }
    public string Language { get => _language; private set => Set(ref _language, value); }
    public string AppVersion { get => _appVersion; private set => Set(ref _appVersion, value); }
    public string DataDirectory { get => _dataDirectory; private set => Set(ref _dataDirectory, value); }
    public string LogPath { get => _logPath; private set => Set(ref _logPath, value); }
    public string SettingsFilePath { get => _settingsFilePath; private set => Set(ref _settingsFilePath, value); }
    public string DeviceSummary { get => _deviceSummary; private set => Set(ref _deviceSummary, value); }
    public string BiosText { get => _biosText; private set => Set(ref _biosText, value); }
    public string CapabilitiesText { get => _capabilitiesText; private set => Set(ref _capabilitiesText, value); }

    public string CpuText { get => _cpuText; private set => Set(ref _cpuText, value); }
    public string GpuText { get => _gpuText; private set => Set(ref _gpuText, value); }
    public string OsText { get => _osText; private set => Set(ref _osText, value); }
    public string SerialText { get => _serialText; private set => Set(ref _serialText, value); }
    public string MemoryText { get => _memoryText; private set => Set(ref _memoryText, value); }
    public string PlatformText { get => _platformText; private set => Set(ref _platformText, value); }
    public string ElevationText { get => _elevationText; private set => Set(ref _elevationText, value); }

    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            if (!Set(ref _startWithWindows, value)) return;
            if (_loading) return;
            ApplyStartupRegistration(value);
        }
    }

    public bool MinimizeToTray
    {
        get => _minimizeToTray;
        set
        {
            if (!Set(ref _minimizeToTray, value)) return;
            if (_loading) return;
            _app.Settings.MinimizeToTray = value;
            _ = SaveQuietlyAsync("最小化到托盘");
        }
    }

    public bool RestoreOnExit
    {
        get => _restoreOnExit;
        set
        {
            if (!Set(ref _restoreOnExit, value)) return;
            if (_loading) return;
            _app.Settings.RestoreOnExit = value;
            _ = SaveQuietlyAsync("退出时恢复默认策略");
        }
    }

    public bool ShowUnavailableMetrics
    {
        get => _showUnavailableMetrics;
        set
        {
            if (!Set(ref _showUnavailableMetrics, value)) return;
            if (_loading) return;
            _app.Settings.ShowUnavailableMetrics = value;
            _ = SaveQuietlyAsync("显示不可用项");
        }
    }

    /// <summary>历史窗口（秒），60–3600；保存时同步重建历史缓冲容量。</summary>
    public int HistoryWindowSeconds
    {
        get => _historyWindowSeconds;
        set
        {
            var clamped = Math.Clamp(value, 60, 3600);
            if (!Set(ref _historyWindowSeconds, clamped)) return;
            if (_loading) return;
            _app.Settings.HistoryWindowSeconds = clamped;
        }
    }

    public ObservableCollection<CapabilityItem> Capabilities { get; }

    public string SaveMessage { get => _saveMessage; private set => Set(ref _saveMessage, value); }
    public string StartupMessage { get => _startupMessage; private set => Set(ref _startupMessage, value); }

    public RelayCommand SaveCommand { get; }
    public RelayCommand ResetSettingsCommand { get; }
    public RelayCommand OpenLogCommand { get; }
    public RelayCommand RestartElevatedCommand { get; }
    public RelayCommand RestoreDefaultsCommand { get; }
    public RelayCommand SetThemeCommand { get; }
    public RelayCommand OpenDataDirectoryCommand { get; }

    public Task ActivateAsync()
    {
        LoadFromSettings();
        LoadDeviceInformation();
        return Task.CompletedTask;
    }

    public Task DeactivateAsync() => Task.CompletedTask;

    private void LoadFromSettings()
    {
        _loading = true;
        try
        {
            var s = _app.Settings;
            Theme = string.Equals(s.Theme, "Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";
            Language = string.Equals(s.Language, "zh-CN", StringComparison.OrdinalIgnoreCase)
                ? "简体中文 (zh-CN)"
                : s.Language;
            _historyWindowSeconds = Math.Clamp(s.HistoryWindowSeconds, 60, 3600);
            Raise(nameof(HistoryWindowSeconds));
            _minimizeToTray = s.MinimizeToTray;
            Raise(nameof(MinimizeToTray));
            _restoreOnExit = s.RestoreOnExit;
            Raise(nameof(RestoreOnExit));
            _showUnavailableMetrics = s.ShowUnavailableMetrics;
            Raise(nameof(ShowUnavailableMetrics));

            _startWithWindows = ReadStartupRegistration();
            Raise(nameof(StartWithWindows));

            AppVersion = _app.AppVersion;
            LogPath = AppLog.Instance.LogFilePath;
            DataDirectory = _app.Store.Directory;
            SettingsFilePath = _app.Store.FilePath;
            ElevationText = _app.IsElevated ? "管理员权限（可写电源方案）" : "普通用户权限（部分写入会被系统拒绝）";
            if (!string.Equals(Theme, s.Theme, StringComparison.OrdinalIgnoreCase))
                StartupMessage = "设置中的主题值「" + s.Theme + "」无法识别，已回退为深色。";
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("加载设置页数据失败", ex);
            SaveMessage = "读取设置失败：" + ex.Message;
        }
        finally
        {
            _loading = false;
        }
    }

    private void LoadDeviceInformation()
    {
        try
        {
            var device = _app.Device;
            if (device is null) return;

            DeviceSummary = string.Format("{0} {1}（型号 {2}）", device.Manufacturer, device.DisplayName, device.Model);
            BiosText = string.Format("{0}（{1}）· 主板 {2} {3}", device.BiosVersion, device.BiosReleaseDate,
                device.BoardVendor, device.BoardProduct);
            CpuText = string.Format("{0} · {1} 核 {2} 线程", device.CpuName, device.CpuCores, device.CpuThreads);
            GpuText = string.Join(" + ", new[] { device.GpuDiscrete, device.GpuIntegrated }.Where(g => !string.IsNullOrWhiteSpace(g)));
            if (string.IsNullOrWhiteSpace(GpuText)) GpuText = "未检测到显卡信息";
            OsText = string.Format("{0}（Build {1}）", device.OsCaption, device.OsBuild);
            SerialText = string.IsNullOrWhiteSpace(device.SerialNumber) ? "不可读" : device.SerialNumber;
            MemoryText = device.TotalMemoryGb > 0 ? device.TotalMemoryGb.ToString("0.0") + " GB" : "不可读";
            PlatformText = device.Platform;

            Capabilities.Clear();
            foreach (var item in device.Capabilities)
                Capabilities.Add(item);

            var total = device.Capabilities.Count;
            var supported = device.Capabilities.Count(c => c.Supported);
            CapabilitiesText = total == 0
                ? "本次检测没有返回能力项"
                : string.Format("共 {0} 项，支持 {1} 项，不支持 {2} 项（不支持项已注明原因）", total, supported, total - supported);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("加载设备信息失败", ex);
            DeviceSummary = "设备信息不可用：" + ex.Message;
        }
    }

    private void ApplyTheme(string themeName)
    {
        try
        {
            var normalized = string.Equals(themeName, "Light", StringComparison.OrdinalIgnoreCase) ? "Light" : "Dark";
            ThemeManager.Apply(normalized);
            Theme = normalized;
            _app.Settings.Theme = normalized;
            _ = SaveQuietlyAsync("主题");
            SaveMessage = normalized == "Light" ? "已切换到浅色主题" : "已切换到深色主题";
            AppLog.Instance.Info("切换主题：" + normalized);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("切换主题失败", ex);
            SaveMessage = "切换主题失败：" + ex.Message;
        }
    }

    private async Task SaveAsync()
    {
        try
        {
            var s = _app.Settings;
            s.Theme = Theme;
            s.HistoryWindowSeconds = Math.Clamp(_historyWindowSeconds, 60, 3600);
            s.MinimizeToTray = MinimizeToTray;
            s.RestoreOnExit = RestoreOnExit;
            s.ShowUnavailableMetrics = ShowUnavailableMetrics;

            // 历史缓冲容量 = 历史窗口 / 采样间隔
            var capacity = s.HistoryWindowSeconds / Math.Max(1, s.RefreshIntervalSeconds);
            if (_app.History is HistoryService history)
            {
                history.SetCapacity(Math.Max(10, capacity));
            }

            _app.Store.Save(s);
            SaveMessage = "设置已保存（" + DateTime.Now.ToString("HH:mm:ss") + "）";
            AppLog.Instance.Info("设置已保存：历史窗口 " + s.HistoryWindowSeconds + " 秒，缓冲容量 " + capacity);
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("保存设置失败", ex);
            SaveMessage = "保存失败：" + ex.Message;
        }
    }

    /// <summary>放弃未保存的修改：从磁盘重新读取设置并刷新界面。</summary>
    private async Task ReloadFromDiskAsync()
    {
        try
        {
            var loaded = _app.Store.Load();
            ApplyLoadedSettings(loaded, applyStartup: false);
            LoadFromSettings();
            SaveMessage = "已放弃未保存的修改（重新读取 " + _app.Store.FilePath + "）";
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("重新读取设置失败", ex);
            SaveMessage = "重新读取失败：" + ex.Message;
        }
    }

    private async Task RestoreFactoryDefaultsAsync()
    {
        if (!Confirm("恢复出厂默认设置？\n\n会重置主题、刷新间隔、历史窗口、开机启动与所有开关，并把当前设置备份到 settings.backup.json。硬件策略不会被写入。"))
        {
            SaveMessage = "已取消恢复出厂默认";
            return;
        }

        try
        {
            _app.Store.ResetToDefaults();
            var defaults = _app.Store.Load();
            ApplyLoadedSettings(defaults, applyStartup: true);

            ThemeManager.Apply(defaults.Theme);
            try { await _app.Monitor.StartAsync(Math.Max(1000, defaults.RefreshIntervalSeconds * 1000)); }
            catch (Exception ex) { AppLog.Instance.Error("恢复默认后重启监控失败", ex); }

            if (_app.History is HistoryService history)
                history.SetCapacity(Math.Max(10, defaults.HistoryWindowSeconds / Math.Max(1, defaults.RefreshIntervalSeconds)));

            _app.Store.Save(_app.Settings);
            LoadFromSettings();
            SaveMessage = "已恢复出厂默认设置（" + DateTime.Now.ToString("HH:mm:ss") + "）";
            AppLog.Instance.Info("用户恢复了出厂默认设置");
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("恢复出厂默认失败", ex);
            SaveMessage = "恢复出厂默认失败：" + ex.Message;
        }
    }

    /// <summary>把读取到的设置复制进运行中的 Settings 对象（该对象被各服务共享，不能整体替换）。</summary>
    private void ApplyLoadedSettings(AppSettings loaded, bool applyStartup)
    {
        try
        {
            var s = _app.Settings;
            s.Theme = loaded.Theme;
            s.Language = loaded.Language;
            s.RefreshIntervalSeconds = Math.Clamp(loaded.RefreshIntervalSeconds, 1, 10);
            s.HistoryWindowSeconds = Math.Clamp(loaded.HistoryWindowSeconds, 60, 3600);
            s.CurrentMode = loaded.CurrentMode;
            s.CustomProfile = loaded.CustomProfile;
            s.Fan = loaded.Fan;
            s.Automation = loaded.Automation;
            s.StartWithWindows = loaded.StartWithWindows;
            s.MinimizeToTray = loaded.MinimizeToTray;
            s.RestoreOnExit = loaded.RestoreOnExit;
            s.ShowUnavailableMetrics = loaded.ShowUnavailableMetrics;

            if (applyStartup) ApplyStartupRegistration(loaded.StartWithWindows);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("应用读取到的设置失败", ex);
        }
    }

    private void OpenLogDirectory()
    {
        try
        {
            var logFile = AppLog.Instance.LogFilePath;
            var folder = Path.GetDirectoryName(logFile);
            if (string.IsNullOrWhiteSpace(folder)) throw new InvalidOperationException("日志目录不可用");
            Directory.CreateDirectory(folder);

            if (File.Exists(logFile))
                Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + logFile + "\"") { UseShellExecute = true });
            else
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });

            SaveMessage = "已打开日志目录：" + folder;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("打开日志目录失败", ex);
            SaveMessage = "打开日志目录失败：" + ex.Message;
        }
    }

    private void OpenDataDirectory()
    {
        try
        {
            var folder = _app.Store.Directory;
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            SaveMessage = "已打开数据目录：" + folder;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("打开数据目录失败", ex);
            SaveMessage = "打开数据目录失败：" + ex.Message;
        }
    }

    private async Task RestartElevatedAsync()
    {
        if (_app.IsElevated)
        {
            SaveMessage = "当前已是管理员权限，无需重启。";
            return;
        }

        if (!Confirm("以管理员身份重启？\n\n程序会先恢复性能策略与风扇自动控制，然后退出并请求提升权限。"))
        {
            SaveMessage = "已取消提升权限重启";
            return;
        }

        try
        {
            SaveMessage = "正在请求提升权限并重启…";
            await _app.RestartElevatedAsync();
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("提升权限重启失败", ex);
            SaveMessage = "提升权限重启失败：" + ex.Message + "（可在日志中查看详情）";
        }
    }

    /// <summary>写入/清除 HKCU Run 项，真正决定是否随 Windows 启动。</summary>
    private void ApplyStartupRegistration(bool enabled)
    {
        try
        {
            // 统一走 StartupManager：它同时处理 32/64 位注册表视图，并在写完后回读校验，
            // 避免这里和托盘/启动自检两处各自维护一份 HKCU Run 逻辑。
            if (!StartupManager.SetEnabled(enabled))
                throw new InvalidOperationException("注册表启动项写入后校验未通过，可能被安全软件拦截");

            StartupMessage = enabled
                ? "已写入开机启动项：" + StartupManager.Command
                : "已移除开机启动项";

            _app.Settings.StartWithWindows = enabled;
            _ = SaveQuietlyAsync("开机启动");
            AppLog.Instance.Info(StartupMessage);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("设置开机启动失败", ex);
            StartupMessage = "设置开机启动失败：" + ex.Message;
            _loading = true;
            _startWithWindows = !enabled;
            Raise(nameof(StartWithWindows));
            _loading = false;
        }
    }

    private static bool ReadStartupRegistration()
    {
        try
        {
            return StartupManager.IsEnabled;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("读取开机启动项失败", ex);
            return false;
        }
    }

    private async Task SaveQuietlyAsync(string reason)
    {
        try
        {
            _app.Store.Save(_app.Settings);
            AppLog.Instance.Info("设置已自动保存（" + reason + "）");
            OnUi(() => SaveMessage = "已保存：" + reason + "（" + DateTime.Now.ToString("HH:mm:ss") + "）");
            await Task.CompletedTask;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("自动保存设置失败（" + reason + "）", ex);
            OnUi(() => SaveMessage = "自动保存失败：" + ex.Message);
        }
    }

    private static bool Confirm(string message)
    {
        try
        {
            if (Application.Current is null) return false;
            var result = MessageBox.Show(Application.Current.MainWindow, message, "PC Performance Center",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
            return result == MessageBoxResult.OK;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("确认对话框显示失败", ex);
            return false;
        }
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
}
