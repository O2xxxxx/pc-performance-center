using System.Windows;
using Ppc.Core.Services;

namespace Ppc.App.Infrastructure;

/// <summary>
/// 主题管理：运行时替换应用资源中的明/暗色资源词典。
/// 只替换 <c>Themes/Dark.xaml</c> 与 <c>Themes/Light.xaml</c> 两份词典，
/// Shared.xaml（样式）与 Converters.xaml（转换器）保持原位不动；
/// 页面统一使用 DynamicResource 引用画刷，因此换肤后无需重建窗口即可即时生效。
/// </summary>
public static class ThemeManager
{
    /// <summary>深色主题标识。</summary>
    public const string DarkTheme = "Dark";

    /// <summary>浅色主题标识。</summary>
    public const string LightTheme = "Light";

    private static readonly object Gate = new();
    private static readonly Uri DarkUri = new("pack://application:,,,/Themes/Dark.xaml", UriKind.Absolute);
    private static readonly Uri LightUri = new("pack://application:,,,/Themes/Light.xaml", UriKind.Absolute);

    private static string _current = DarkTheme;
    private static bool _applying;

    /// <summary>当前主题名称（"Dark" 或 "Light"）。</summary>
    public static string Current
    {
        get => _current;
        private set => _current = value;
    }

    /// <summary>当前是否为深色主题。</summary>
    public static bool IsDark => string.Equals(_current, DarkTheme, StringComparison.OrdinalIgnoreCase);

    /// <summary>主题切换完成后触发，订阅方读取 <see cref="Current"/> 获取新主题。</summary>
    public static event EventHandler? ThemeChanged;

    /// <summary>把任意字符串规范成受支持的主题名称，未知值一律按深色处理。</summary>
    public static string Normalize(string? theme)
        => string.Equals(theme?.Trim(), LightTheme, StringComparison.OrdinalIgnoreCase) ? LightTheme : DarkTheme;

    /// <summary>应用指定主题；返回是否成功替换了资源词典。</summary>
    public static bool Apply(string theme) => ApplyCore(Normalize(theme));

    /// <summary>在深色/浅色之间切换。</summary>
    public static bool Toggle() => Apply(IsDark ? LightTheme : DarkTheme);

    /// <summary>按设置对象里的 Theme 字段应用主题。</summary>
    public static bool Apply(Ppc.Core.Models.AppSettings? settings) => Apply(settings?.Theme ?? DarkTheme);

    private static bool ApplyCore(string target)
    {
        var raiseEvent = false;
        var success = false;

        lock (Gate)
        {
            if (_applying)
            {
                AppLog.Instance.Warn("主题切换正在进行中，忽略重入请求：" + target);
                return false;
            }

            _applying = true;
            try
            {
                var app = System.Windows.Application.Current;
                if (app is null)
                {
                    // 应用尚未创建（例如单元测试或极早期启动），只记录状态，不编造成功。
                    _current = target;
                    AppLog.Instance.Warn("Application.Current 为空，主题仅记录为 " + target + "，资源未替换");
                    return false;
                }

                var dictionaries = app.Resources.MergedDictionaries;
                var targetUri = target == LightTheme ? LightUri : DarkUri;

                // 找出所有主题词典（正常情况下只有一份），保持其余词典的位置不变。
                var themeIndexes = new List<int>();
                for (var i = 0; i < dictionaries.Count; i++)
                {
                    if (IsThemeDictionary(dictionaries[i])) themeIndexes.Add(i);
                }

                if (themeIndexes.Count == 0)
                {
                    // 找不到时按契约约定把主题词典放到首位，后面仍是 Shared/Converters。
                    dictionaries.Insert(0, new ResourceDictionary { Source = targetUri });
                    AppLog.Instance.Warn("资源字典中没有找到 Dark/Light 主题词典，已在首位插入 " + target);
                }
                else
                {
                    if (!SourceMatches(dictionaries[themeIndexes[0]], target))
                    {
                        dictionaries[themeIndexes[0]] = new ResourceDictionary { Source = targetUri };
                    }

                    // 移除多余的主题词典（从后往前删，避免下标漂移）。
                    for (var i = themeIndexes.Count - 1; i >= 1; i--)
                    {
                        dictionaries.RemoveAt(themeIndexes[i]);
                    }
                }

                _current = target;
                raiseEvent = true;
                success = true;
                AppLog.Instance.Info($"主题已切换为 {target}（MergedDictionaries={dictionaries.Count}）");
            }
            catch (Exception ex)
            {
                AppLog.Instance.Error("切换主题失败：" + target, ex);
                success = false;
            }
            finally
            {
                _applying = false;
            }
        }

        if (raiseEvent)
        {
            try
            {
                ThemeChanged?.Invoke(null, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                // 订阅方出错不能影响换肤本身
                AppLog.Instance.Error("主题变更事件订阅方异常", ex);
            }
        }

        return success;
    }

    private static bool IsThemeDictionary(ResourceDictionary dictionary)
        => SourceMatches(dictionary, DarkTheme) || SourceMatches(dictionary, LightTheme);

    private static bool SourceMatches(ResourceDictionary dictionary, string theme)
    {
        try
        {
            var source = dictionary.Source?.OriginalString;
            if (string.IsNullOrWhiteSpace(source)) return false;

            var normalized = source.Replace('\\', '/');
            return normalized.EndsWith($"themes/{theme.ToLowerInvariant()}.xaml", StringComparison.OrdinalIgnoreCase)
                   || normalized.EndsWith($"themes/{theme}.xaml", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
