using System.Globalization;
using System.Text;
using Ppc.Core.Models;
using Ppc.Core.Services;

namespace Ppc.App.Infrastructure;

/// <summary>
/// 系统托盘图标服务：基于 WinForms 的 NotifyIcon 实现（项目已启用 UseWindowsForms）。
/// WPF 与 WinForms 存在同名类型（Application / MessageBox 等），本文件一律写全名。
/// 托盘图标只在界面线程创建；快照驱动的提示文本更新会切回界面线程并做节流。
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private const string AppTitle = "PC Performance Center";

    /// <summary>NotifyIcon.Text 的硬限制（WinForms 超过 63 会抛异常）。</summary>
    private const int TooltipMaxLength = 63;

    private static readonly PerformanceMode[] Modes =
    {
        PerformanceMode.Eco,
        PerformanceMode.Balanced,
        PerformanceMode.Performance,
        PerformanceMode.Custom,
    };

    private readonly AppServices _app;
    private readonly Action _showWindow;
    private readonly Action _exitApp;
    private readonly Dictionary<PerformanceMode, System.Windows.Forms.ToolStripMenuItem> _modeItems = new();

    private System.Windows.Forms.NotifyIcon? _notify;
    private System.Windows.Forms.ContextMenuStrip? _menu;
    private System.Windows.Forms.ToolStripMenuItem? _headerItem;
    private System.Drawing.Icon? _icon;
    private Stream? _iconStream;
    private DateTime _lastSnapshotRefresh = DateTime.MinValue;
    private bool _initialized;
    private bool _disposed;

    public TrayIconService(AppServices app, Action showWindow, Action exitApp)
    {
        _app = app ?? throw new ArgumentNullException(nameof(app));
        _showWindow = showWindow ?? throw new ArgumentNullException(nameof(showWindow));
        _exitApp = exitApp ?? throw new ArgumentNullException(nameof(exitApp));
    }

    /// <summary>创建托盘图标与右键菜单。可重复调用，只会初始化一次；失败只写日志。</summary>
    public void Initialize()
    {
        if (_disposed || _initialized) return;

        try
        {
            _icon = LoadIcon();
            _menu = BuildMenu();

            _notify = new System.Windows.Forms.NotifyIcon
            {
                Icon = _icon,
                Text = Truncate(AppTitle, TooltipMaxLength),
                ContextMenuStrip = _menu,
                Visible = true,
            };
            _notify.DoubleClick += OnNotifyDoubleClick;
            _notify.MouseClick += OnNotifyMouseClick;

            _app.SnapshotUpdated += OnSnapshotUpdated;
            _initialized = true;

            AppLog.Instance.Info("托盘图标已创建");
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("创建托盘图标失败（主界面不受影响）", ex);
        }

        Refresh();
    }

    /// <summary>根据最新快照刷新托盘提示、菜单标题与模式勾选。</summary>
    public void Refresh()
    {
        if (_disposed) return;

        try
        {
            UpdateTooltip();
            UpdateHeader();
            UpdateModeChecks();
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("刷新托盘状态失败", ex);
        }
    }

    /// <summary>用最新快照生成简短状态文本并写入托盘提示。</summary>
    public void UpdateTooltip()
    {
        if (_disposed || _notify is null) return;

        try
        {
            var text = BuildTooltipText();
            RunOnUi(() =>
            {
                if (_notify is null || _disposed) return;
                _notify.Text = Truncate(text, TooltipMaxLength);
            });
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("更新托盘提示失败：" + ex.Message);
        }
    }

    /// <summary>显示气泡提示（系统关闭通知时会静默失败，不影响功能）。</summary>
    public void ShowBalloon(string title, string message)
    {
        if (_disposed || _notify is null) return;

        try
        {
            RunOnUi(() =>
            {
                if (_notify is null || _disposed) return;
                _notify.BalloonTipTitle = Truncate(title ?? string.Empty, TooltipMaxLength);
                _notify.BalloonTipText = Truncate(message ?? string.Empty, 255);
                _notify.BalloonTipIcon = System.Windows.Forms.ToolTipIcon.Info;
                _notify.ShowBalloonTip(4000);
            });
        }
        catch (Exception ex)
        {
            // 气泡提示失败不算功能失败，只记日志
            AppLog.Instance.Warn("显示托盘气泡失败：" + ex.Message);
        }
    }

    /// <summary>真正释放托盘图标（先隐藏再 Dispose，避免残留幽灵图标）。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _app.SnapshotUpdated -= OnSnapshotUpdated;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("取消托盘快照订阅失败：" + ex.Message);
        }

        try
        {
            if (_notify is not null)
            {
                _notify.DoubleClick -= OnNotifyDoubleClick;
            _notify.MouseClick -= OnNotifyMouseClick;
                _notify.Visible = false;
                _notify.ContextMenuStrip = null;
                _notify.Dispose();
            }
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("释放托盘图标失败：" + ex.Message);
        }
        finally
        {
            _notify = null;
        }

        try
        {
            _menu?.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("释放托盘菜单失败：" + ex.Message);
        }
        finally
        {
            _menu = null;
            _headerItem = null;
            _modeItems.Clear();
        }

        try
        {
            _icon?.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("释放托盘图标句柄失败：" + ex.Message);
        }
        finally
        {
            _icon = null;
        }

        try
        {
            _iconStream?.Dispose();
        }
        catch
        {
            // 忽略
        }
        finally
        {
            _iconStream = null;
        }

        _initialized = false;
    }

    private System.Drawing.Icon LoadIcon()
    {
        // 优先用嵌入资源里的 app.ico（与窗口图标保持一致）
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute));

            if (resource?.Stream is not null)
            {
                using var source = resource.Stream;
                var buffer = new MemoryStream();
                source.CopyTo(buffer);
                buffer.Position = 0;

                // Icon(Stream) 要求流在图标生命周期内保持打开，因此把内存流保存在字段里。
                _iconStream = buffer;
                return new System.Drawing.Icon(buffer);
            }
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("从资源加载托盘图标失败，改用可执行文件图标：" + ex.Message);
        }

        // 退路：从当前 exe 提取图标
        try
        {
            var exe = AppPaths.ExecutablePath;
            if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
            {
                var extracted = System.Drawing.Icon.ExtractAssociatedIcon(exe);
                if (extracted is not null) return extracted;
            }
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("提取可执行文件图标失败：" + ex.Message);
        }

        AppLog.Instance.Warn("托盘图标回退为系统默认图标");
        return (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
    }

    private System.Windows.Forms.ContextMenuStrip BuildMenu()
    {
        var menu = new System.Windows.Forms.ContextMenuStrip
        {
            ShowImageMargin = false,
            ShowCheckMargin = true,
        };

        _headerItem = new System.Windows.Forms.ToolStripMenuItem("正在读取状态…") { Enabled = false };
        menu.Items.Add(_headerItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var showItem = new System.Windows.Forms.ToolStripMenuItem("显示主窗口");
        showItem.Click += (_, _) => ShowMainWindow();
        menu.Items.Add(showItem);

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        foreach (var mode in Modes)
        {
            var item = new System.Windows.Forms.ToolStripMenuItem(ModeName(mode) + "模式") { Tag = mode };
            item.Click += (_, _) => _ = ApplyModeAsync(mode);
            _modeItems[mode] = item;
            menu.Items.Add(item);
        }

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var minimizeItem = new System.Windows.Forms.ToolStripMenuItem("最小化到托盘");
        minimizeItem.Click += (_, _) => HideMainWindow();
        menu.Items.Add(minimizeItem);

        var exitItem = new System.Windows.Forms.ToolStripMenuItem("退出");
        exitItem.Click += (_, _) => ExitApplication();
        menu.Items.Add(exitItem);

        menu.Opening += (_, _) =>
        {
            UpdateHeader();
            UpdateModeChecks();
        };

        return menu;
    }

    private void OnNotifyDoubleClick(object? sender, EventArgs e) => ShowMainWindow();

    /// <summary>左键单击托盘图标 → 打开主窗口（右键仍弹菜单）。</summary>
    private void OnNotifyMouseClick(object? sender, System.Windows.Forms.MouseEventArgs e)
    {
        if (e.Button == System.Windows.Forms.MouseButtons.Left) ShowMainWindow();
    }

    private void OnSnapshotUpdated(object? sender, HardwareSnapshot snapshot)
    {
        if (_disposed) return;

        // 监控默认 2 秒一次，这里再做一次节流，避免频繁改写托盘窗口文本。
        var now = DateTime.UtcNow;
        if ((now - _lastSnapshotRefresh).TotalMilliseconds < 2000) return;
        _lastSnapshotRefresh = now;

        UpdateTooltip();
    }

    private async Task ApplyModeAsync(PerformanceMode mode)
    {
        try
        {
            var result = await _app.Performance.ApplyModeAsync(mode, _app.Settings.CustomProfile);
            if (result.Success)
            {
                _app.Settings.CurrentMode = mode;
                await _app.SaveSettingsAsync();
                _app.SetStatus($"性能模式已切换为「{ModeName(mode)}」");
                ShowBalloon("性能模式已切换", $"{ModeName(mode)}：{result.Message}");
                AppLog.Instance.Info($"托盘切换性能模式成功：{mode} → {result.Message}");
            }
            else
            {
                ShowBalloon("切换性能模式失败", result.Message);
                AppLog.Instance.Warn($"托盘切换性能模式失败：{mode} → {result.Message}");
            }
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error($"托盘切换性能模式异常：{mode}", ex);
            ShowBalloon("切换性能模式失败", ex.Message);
        }
        finally
        {
            UpdateModeChecks();
        }
    }

    private void ShowMainWindow()
    {
        RunOnUi(() =>
        {
            try
            {
                _showWindow();
            }
            catch (Exception ex)
            {
                AppLog.Instance.Error("从托盘恢复主窗口失败", ex);
            }
        });
    }

    private void HideMainWindow()
    {
        RunOnUi(() =>
        {
            try
            {
                var window = System.Windows.Application.Current?.MainWindow;
                if (window is null)
                {
                    AppLog.Instance.Warn("最小化到托盘失败：主窗口不存在");
                    return;
                }

                window.Hide();
                AppLog.Instance.Info("主窗口已最小化到托盘");
            }
            catch (Exception ex)
            {
                AppLog.Instance.Error("最小化到托盘失败", ex);
            }
        });
    }

    private void ExitApplication()
    {
        RunOnUi(() =>
        {
            try
            {
                _exitApp();
            }
            catch (Exception ex)
            {
                AppLog.Instance.Error("从托盘退出程序失败", ex);
            }
        });
    }

    private void UpdateHeader()
    {
        if (_headerItem is null) return;

        try
        {
            var parts = new List<string> { ModeName(_app.Settings.CurrentMode) };
            var cpu = FormatValue(_app.Latest, Metric.CpuTemperature, "0");
            var usage = FormatValue(_app.Latest, Metric.CpuUsage, "0");

            if (cpu is not null) parts.Add("CPU " + cpu + "°C");
            if (usage is not null) parts.Add(usage + "%");

            _headerItem.Text = string.Join(" · ", parts);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("更新托盘菜单标题失败：" + ex.Message);
        }
    }

    private void UpdateModeChecks()
    {
        try
        {
            foreach (var pair in _modeItems)
            {
                pair.Value.Checked = _app.Settings.CurrentMode == pair.Key;
            }
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("更新托盘模式勾选失败：" + ex.Message);
        }
    }

    private string BuildTooltipText()
    {
        var snapshot = _app.Latest;
        var builder = new StringBuilder();
        builder.Append("PC 性能中心 · ").Append(ModeName(_app.Settings.CurrentMode));

        var cpuTemp = FormatValue(snapshot, Metric.CpuTemperature, "0");
        var cpuUsage = FormatValue(snapshot, Metric.CpuUsage, "0");
        if (cpuTemp is not null || cpuUsage is not null)
        {
            builder.AppendLine().Append("CPU ").Append(cpuTemp ?? "—").Append("°C ")
                .Append(cpuUsage ?? "—").Append('%');
        }

        var gpuTemp = FormatValue(snapshot, Metric.GpuTemperature, "0");
        var gpuUsage = FormatValue(snapshot, Metric.GpuUsage, "0");
        if (gpuTemp is not null || gpuUsage is not null)
        {
            builder.AppendLine().Append("GPU ").Append(gpuTemp ?? "—").Append("°C ")
                .Append(gpuUsage ?? "—").Append('%');
        }

        return builder.ToString();
    }

    private static string? FormatValue(HardwareSnapshot snapshot, Metric metric, string format)
    {
        try
        {
            var reading = snapshot[metric];
            if (!reading.Value.HasValue) return null;

            return reading.Value.Value.ToString(format, CultureInfo.InvariantCulture);
        }
        catch
        {
            return null;
        }
    }

    private static string ModeName(PerformanceMode mode) => mode switch
    {
        PerformanceMode.Eco => "节能",
        PerformanceMode.Balanced => "平衡",
        PerformanceMode.Performance => "性能",
        PerformanceMode.Custom => "自定义",
        _ => mode.ToString(),
    };

    private static string Truncate(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        return text.Length <= maxLength ? text : text[..maxLength];
    }

    /// <summary>把动作切到 WPF 界面线程执行；调度器已关闭时只记日志。</summary>
    private static void RunOnUi(Action action)
    {
        try
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                // 极早期/退出阶段没有 Dispatcher，直接在当前线程执行（调用方已做 try/catch）。
                action();
                return;
            }

            if (dispatcher.CheckAccess())
            {
                action();
                return;
            }

            dispatcher.BeginInvoke(action);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("切换到界面线程失败：" + ex.Message);
        }
    }
}
