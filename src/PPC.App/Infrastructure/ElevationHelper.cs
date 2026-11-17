using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Ppc.Core.Services;

namespace Ppc.App.Infrastructure;

/// <summary>
/// 管理员权限辅助：判断当前进程是否已提权，以及通过标准 UAC（runas）重启自身。
/// 只使用 Windows 官方提权流程，不做任何权限绕过或静默提权。
/// </summary>
public static class ElevationHelper
{
    /// <summary>UAC 被用户取消时 Process.Start 抛出的 Win32 错误码。</summary>
    private const int ErrorCancelled = 1223;

    private static readonly object Gate = new();
    private static bool? _cached;

    /// <summary>当前进程是否以管理员身份运行。</summary>
    public static bool IsElevated
    {
        get
        {
            lock (Gate)
            {
                if (_cached.HasValue) return _cached.Value;

                try
                {
                    using var identity = WindowsIdentity.GetCurrent();
                    var principal = new WindowsPrincipal(identity);
                    _cached = principal.IsInRole(WindowsBuiltInRole.Administrator);
                }
                catch (Exception ex)
                {
                    AppLog.Instance.Error("读取当前进程权限失败，按非管理员处理", ex);
                    _cached = false;
                }

                return _cached.Value;
            }
        }
    }

    /// <summary>
    /// 以管理员权限重启当前程序。成功时新进程已启动，本进程立即以退出码 0 结束；
    /// 用户取消 UAC 或启动失败时返回 false，调用方可以继续以普通权限运行。
    /// </summary>
    /// <param name="arguments">传给新实例的命令行参数（原样传递）。</param>
    public static bool RestartElevated(string arguments = "")
    {
        var exePath = AppPaths.ExecutablePath;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
        {
            AppLog.Instance.Error("提权重启失败：找不到当前可执行文件（" + (exePath ?? "<空>") + "）");
            return false;
        }

        if (IsElevated)
        {
            AppLog.Instance.Info("当前已是管理员权限，无需重复提权");
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = arguments ?? string.Empty,
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppPaths.ExecutableDirectory,
                ErrorDialog = false,
            };

            var process = Process.Start(startInfo);
            if (process is null)
            {
                AppLog.Instance.Error("提权重启失败：系统没有返回新进程句柄（可能被安全策略拦截）");
                return false;
            }

            AppLog.Instance.Info($"已通过 UAC 请求提权重启：{exePath} {arguments}".TrimEnd());

            // 旧实例必须尽快退出，否则新实例会撞上单实例互斥体。
            Environment.Exit(0);
            return true;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
        {
            AppLog.Instance.Warn("用户取消了 UAC 提权，程序继续以普通权限运行");
            return false;
        }
        catch (Win32Exception ex)
        {
            AppLog.Instance.Error($"UAC 提权失败（Win32 错误码 {ex.NativeErrorCode}：{ex.Message}）", ex);
            return false;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("提权重启失败", ex);
            return false;
        }
    }
}
