using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Ppc.App.Infrastructure;
using Ppc.Core.Services;

namespace Ppc.App;

public partial class App : Application
{
    private Mutex? _singleInstance;
    private TrayIconService? _tray;
    private bool _shuttingDown;

    public static AppServices Services { get; private set; } = null!;

    /// <summary>是否正在执行退出流程（此时允许主窗口真正关闭）。</summary>
    public bool IsShuttingDown => _shuttingDown;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, "PCPerformanceCenter.SingleInstance.{9F3C1B62-8B1E-4E77-9F9B-2C6A1D0E4F31}", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show("PC Performance Center 已经在运行。", "PC Performance Center",
                MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppLog.Instance.Error("未处理异常", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            AppLog.Instance.Error("未观察的任务异常", args.Exception);
            args.SetObserved();
        };

        try
        {
            ThemeManager.Apply(LoadThemePreference());

            Services = new AppServices();
            MainWindow = new MainWindow(Services);
            _tray = new TrayIconService(Services, ShowMainWindow, ExitApplication);
            _tray.Initialize();
            MainWindow.Show();

            await Services.InitializeAsync();
            _tray.Refresh();

            var captureIndex = Array.IndexOf(e.Args, "--capture");
            if (captureIndex >= 0 && captureIndex + 1 < e.Args.Length)
            {
                var code = await SelfTest.RunAsync(this, MainWindow!, e.Args[captureIndex + 1]);
                Shutdown(code);
                return;
            }
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("启动失败", ex);
            MessageBox.Show("启动失败：" + ex.Message + "\n\n日志：" + AppLog.Instance.LogFilePath,
                "PC Performance Center", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static string LoadThemePreference()
    {
        try
        {
            var store = new SettingsStore();
            return store.Load().Theme;
        }
        catch
        {
            return "Dark";
        }
    }

    public void ShowMainWindow()
    {
        if (MainWindow is null) return;
        MainWindow.Show();
        MainWindow.WindowState = WindowState.Normal;
        MainWindow.Activate();
    }

    public async void ExitApplication()
    {
        if (_shuttingDown) return;
        _shuttingDown = true;
        try
        {
            if (MainWindow is MainWindow window) await window.PrepareShutdownAsync();
            if (Services is not null) await Services.RestoreAllAsync();
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("退出清理失败", ex);
        }
        finally
        {
            _tray?.Dispose();
            Shutdown(0);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _singleInstance?.ReleaseMutex(); } catch { /* ignore */ }
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Instance.Error("界面线程未处理异常", e.Exception);
        MessageBox.Show("出现问题：\n" + e.Exception.Message + "\n\n程序会继续运行，详细信息已写入日志。",
            "PC Performance Center", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }
}