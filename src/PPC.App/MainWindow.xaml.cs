using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Ppc.Core.Services;

namespace Ppc.App;

public partial class MainWindow : Window
{
    private readonly AppServices _services;

    public MainWindow(AppServices services)
    {
        InitializeComponent();
        _services = services;
        DataContext = services;
    }

    public AppServices Services => _services;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        TryApplyRoundedCorners();
    }

    /// <summary>让无边框窗口在 Windows 11 上也有圆角（macOS 观感的必要条件）。</summary>
    private void TryApplyRoundedCorners()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;

            // DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWCP_ROUND = 2
            var preference = 2;
            if (DwmSetWindowAttribute(handle, 33, ref preference, sizeof(int)) != 0)
            {
                // 旧系统不支持圆角属性，保持默认方角即可，不影响功能
                AppLog.Instance.Info("当前系统不支持 DWM 圆角属性（可能是 Windows 10）");
            }
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("设置窗口圆角失败：" + ex.Message);
        }
    }

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        try { DragMove(); }
        catch (InvalidOperationException) { /* 鼠标已释放，忽略 */ }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object sender, RoutedEventArgs e) => RequestClose();

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    /// <summary>红灯行为：按设置最小化到托盘或真正退出。</summary>
    public void RequestClose()
    {
        if (_services.Settings.MinimizeToTray)
        {
            Hide();
            return;
        }

        if (Application.Current is App app) app.ExitApplication();
    }

    public Task PrepareShutdownAsync()
    {
        try { _services.Store.Save(_services.Settings); }
        catch (Exception ex) { AppLog.Instance.Error("退出前保存设置失败", ex); }
        return Task.CompletedTask;
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (_services is null)
        {
            base.OnClosing(e);
            return;
        }

        if (_services.Settings.MinimizeToTray && !(Application.Current is App { IsShuttingDown: true }))
        {
            e.Cancel = true;
            Hide();
            return;
        }

        base.OnClosing(e);
    }
}
