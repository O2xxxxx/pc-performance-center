using Microsoft.Win32;
using Ppc.Core.Services;

namespace Ppc.App.Infrastructure;

/// <summary>
/// 开机自启管理：读写当前用户注册表 <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c>。
/// 只在当前用户范围写入，不需要管理员权限，也不改系统级启动项；
/// 值的格式是带双引号的 exe 完整路径（路径含空格也不会被拆开）。
/// </summary>
public static class StartupManager
{
    /// <summary>注册表中的值名。</summary>
    public const string ValueName = "PCPerformanceCenter";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>当前可执行文件路径。</summary>
    public static string ExecutablePath => AppPaths.ExecutablePath;

    /// <summary>将要写入 Run 项的完整命令（带引号的 exe 路径）。</summary>
    public static string Command => AppPaths.Quote(ExecutablePath);

    /// <summary>是否已登记开机自启（要求登记的路径就是当前 exe）。</summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                var current = ExecutablePath;
                if (string.IsNullOrWhiteSpace(current)) return false;

                foreach (var view in Views())
                {
                    if (Matches(ReadCommand(view), current)) return true;
                }

                return false;
            }
            catch (Exception ex)
            {
                AppLog.Instance.Error("读取开机启动状态失败", ex);
                return false;
            }
        }
    }

    /// <summary>读取注册表里当前登记的启动命令；没有则返回 null。</summary>
    public static string? RegisteredCommand
    {
        get
        {
            try
            {
                foreach (var view in Views())
                {
                    var command = ReadCommand(view);
                    if (!string.IsNullOrWhiteSpace(command)) return command;
                }

                return null;
            }
            catch (Exception ex)
            {
                AppLog.Instance.Error("读取开机启动命令失败", ex);
                return null;
            }
        }
    }

    /// <summary>
    /// 开启或关闭开机自启；返回操作后真实生效的状态（写失败或校验不通过都返回 false）。
    /// </summary>
    public static bool SetEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                var exe = ExecutablePath;
                if (string.IsNullOrWhiteSpace(exe))
                {
                    AppLog.Instance.Error("开启开机自启失败：无法确定当前可执行文件路径");
                    return false;
                }

                var command = AppPaths.Quote(exe);
                var written = false;
                var views = Views().ToList();

                foreach (var view in views)
                {
                    try
                    {
                        if (!written)
                        {
                            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
                            using var key = baseKey.CreateSubKey(RunKeyPath, writable: true);
                            if (key is null)
                            {
                                AppLog.Instance.Warn($"打开注册表启动项失败（{view}），尝试下一个视图");
                                continue;
                            }

                            key.SetValue(ValueName, command, RegistryValueKind.String);
                            written = true;
                            AppLog.Instance.Info($"已写入开机启动项（{view}）：{command}");
                        }
                        else
                        {
                            // 另一个注册表视图里的同名旧值会导致重复启动，顺手删掉。
                            DeleteCommand(view);
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLog.Instance.Warn($"写入开机启动项失败（{view}）：{ex.Message}");
                    }
                }

                if (!written)
                {
                    AppLog.Instance.Error("开启开机自启失败：没有任何注册表视图写入成功");
                    return false;
                }

                var verified = IsEnabled;
                if (verified)
                {
                    AppLog.Instance.Info("开机自启已开启并通过校验");
                }
                else
                {
                    AppLog.Instance.Error("开机自启写入后校验失败，请手动检查 " + RunKeyPath);
                }

                return verified;
            }

            foreach (var view in Views())
            {
                try
                {
                    DeleteCommand(view);
                }
                catch (Exception ex)
                {
                    AppLog.Instance.Warn($"删除开机启动项失败（{view}）：{ex.Message}");
                }
            }

            var disabled = !IsEnabled;
            AppLog.Instance.Info(disabled
                ? "开机自启已关闭"
                : "关闭开机自启后仍检测到启动项，请手动检查 " + RunKeyPath);
            return disabled;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error((enabled ? "开启" : "关闭") + "开机自启失败", ex);
            return false;
        }
    }

    /// <summary>
    /// 如果已开启自启但登记的仍是旧路径（程序被移动过），自动改写为当前 exe。
    /// 未开启自启时不做任何事并返回 true；写失败返回 false。
    /// </summary>
    public static bool SyncIfNeeded()
    {
        try
        {
            if (!IsEnabled) return true;

            var current = ExecutablePath;
            if (string.IsNullOrWhiteSpace(current)) return false;

            if (Matches(RegisteredCommand, current)) return true;

            AppLog.Instance.Info("开机启动项指向旧路径，正在更新为当前程序：" + current);
            return SetEnabled(true);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("同步开机启动项失败", ex);
            return false;
        }
    }

    /// <summary>枚举需要检查的注册表视图：64 位系统先看 64 位视图，再看 32 位视图。</summary>
    private static IEnumerable<RegistryView> Views()
    {
        if (!Environment.Is64BitOperatingSystem)
        {
            yield return RegistryView.Registry32;
            yield break;
        }

        yield return RegistryView.Registry64;
        yield return RegistryView.Registry32;
    }

    private static string? ReadCommand(RegistryView view)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
        using var key = baseKey.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(ValueName) as string;
    }

    private static void DeleteCommand(RegistryView view)
    {
        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, view);
        using var key = baseKey.OpenSubKey(RunKeyPath, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    private static bool Matches(string? command, string currentPath)
    {
        var registered = NormalizePath(command);
        return registered is not null && PathEquals(registered, currentPath);
    }

    /// <summary>把 Run 项的值解析成注册的 exe 路径（去掉引号、环境变量与参数）。</summary>
    private static string? NormalizePath(string? command)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(command)) return null;

            var text = command.Trim();

            if (text.StartsWith('"'))
            {
                var end = text.IndexOf('"', 1);
                if (end > 1) text = text[1..end];
            }
            else
            {
                // 未加引号时以 .exe 结尾截断，兼容带参数的手写命令。
                var index = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
                if (index > 0) text = text[..(index + 4)];
            }

            text = Environment.ExpandEnvironmentVariables(text).Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("解析开机启动命令失败：" + ex.Message);
            return null;
        }
    }

    private static bool PathEquals(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left).TrimEnd('\\'),
                Path.GetFullPath(right).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
