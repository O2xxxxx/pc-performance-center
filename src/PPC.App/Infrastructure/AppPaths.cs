using System.Diagnostics;
using System.Reflection;
using Ppc.Core.Services;

namespace Ppc.App.Infrastructure;

/// <summary>
/// 当前可执行文件的定位工具：开机自启项与 UAC 提权重启都需要真实 exe 路径而不是 dll 路径。
/// 结果只解析一次，失败时写日志并回退到 AppContext.BaseDirectory。
/// </summary>
public static class AppPaths
{
    private static string? _executablePath;
    private static string? _directory;
    private static readonly object Gate = new();

    /// <summary>当前进程的可执行文件完整路径；无法解析时返回空字符串。</summary>
    public static string ExecutablePath
    {
        get
        {
            if (!string.IsNullOrEmpty(_executablePath)) return _executablePath!;
            lock (Gate)
            {
                _executablePath ??= ResolveExecutablePath();
                return _executablePath!;
            }
        }
    }

    /// <summary>当前进程所在目录。</summary>
    public static string ExecutableDirectory
    {
        get
        {
            if (!string.IsNullOrEmpty(_directory)) return _directory!;
            lock (Gate)
            {
                if (string.IsNullOrEmpty(_directory))
                {
                    try
                    {
                        var path = ExecutablePath;
                        if (!string.IsNullOrWhiteSpace(path)) _directory = Path.GetDirectoryName(path);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Instance.Warn("解析程序所在目录失败：" + ex.Message);
                    }

                    _directory ??= AppContext.BaseDirectory;
                }

                return _directory!;
            }
        }
    }

    /// <summary>给路径加英文双引号（注册表 Run 项要求带引号，避免空格路径被拆开）。</summary>
    public static string Quote(string? path)
        => string.IsNullOrWhiteSpace(path) ? string.Empty : "\"" + path.Trim().Trim('"') + "\"";

    private static string ResolveExecutablePath()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath) && File.Exists(processPath)) return processPath!;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("读取 Environment.ProcessPath 失败：" + ex.Message);
        }

        try
        {
            var mainModule = Process.GetCurrentProcess().MainModule?.FileName;
            if (!string.IsNullOrWhiteSpace(mainModule) && File.Exists(mainModule)) return mainModule!;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("读取进程主模块路径失败：" + ex.Message);
        }

        try
        {
            var location = Assembly.GetEntryAssembly()?.Location;
            if (!string.IsNullOrWhiteSpace(location))
            {
                var exe = Path.ChangeExtension(location, ".exe");
                if (File.Exists(exe)) return exe;
                return location!;
            }
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("读取入口程序集路径失败：" + ex.Message);
        }

        AppLog.Instance.Warn("无法解析当前可执行文件路径");
        return string.Empty;
    }
}
