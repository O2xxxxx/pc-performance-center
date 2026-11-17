using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ppc.Core.Services;

namespace Ppc.App.Infrastructure;

/// <summary>
/// 对话框服务：统一 MessageBox 的标题与 owner，并提供“长文本只读窗口”用于日志/诊断输出。
/// 所有方法内部都做异常保护，只会写日志，不会向调用方抛出。
/// </summary>
public static class DialogService
{
    /// <summary>默认标题。</summary>
    public const string DefaultTitle = "PC Performance Center";

    /// <summary>显示普通提示。</summary>
    public static void ShowInfo(string message, string? title = null)
        => ShowMessage(message, title, MessageBoxImage.Information);

    /// <summary>显示警告。</summary>
    public static void ShowWarning(string message, string? title = null)
        => ShowMessage(message, title, MessageBoxImage.Warning);

    /// <summary>显示错误。</summary>
    public static void ShowError(string message, string? title = null)
        => ShowMessage(message, title, MessageBoxImage.Error);

    /// <summary>确认对话框；用户点“是”返回 true。</summary>
    public static bool Confirm(string message, string? title = null, bool danger = false)
    {
        try
        {
            var caption = string.IsNullOrWhiteSpace(title) ? DefaultTitle : title!;
            var icon = danger ? MessageBoxImage.Warning : MessageBoxImage.Question;
            var confirmed = false;

            RunOnUi(() =>
            {
                var owner = GetOwner();
                var result = owner is null
                    ? MessageBox.Show(message, caption, MessageBoxButton.YesNo, icon, MessageBoxResult.No)
                    : MessageBox.Show(owner, message, caption, MessageBoxButton.YesNo, icon, MessageBoxResult.No);
                confirmed = result == MessageBoxResult.Yes;
            });

            return confirmed;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("显示确认对话框失败", ex);
            return false;
        }
    }

    /// <summary>
    /// 显示只读长文本窗口（诊断信息、日志片段等）。带滚动条与“复制全部”，
    /// 不用 MessageBox 显示长文本。
    /// </summary>
    public static void ShowTextDialog(string title, string text)
    {
        try
        {
            RunOnUi(() =>
            {
                try
                {
                    var window = BuildTextWindow(title, text);
                    window.ShowDialog();
                }
                catch (Exception ex)
                {
                    AppLog.Instance.Error("显示文本窗口失败", ex);
                    MessageBox.Show(ex.Message, string.IsNullOrWhiteSpace(title) ? DefaultTitle : title,
                        MessageBoxButton.OK, MessageBoxImage.Error);
                }
            });
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("显示文本窗口失败（界面线程不可用）", ex);
        }
    }

    private static void ShowMessage(string message, string? title, MessageBoxImage icon)
    {
        try
        {
            var caption = string.IsNullOrWhiteSpace(title) ? DefaultTitle : title!;
            RunOnUi(() =>
            {
                var owner = GetOwner();
                if (owner is null) MessageBox.Show(message, caption, MessageBoxButton.OK, icon);
                else MessageBox.Show(owner, message, caption, MessageBoxButton.OK, icon);
            });
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("显示对话框失败：" + (title ?? DefaultTitle), ex);
        }
    }

    private static Window BuildTextWindow(string title, string text)
    {
        var window = new Window
        {
            Title = string.IsNullOrWhiteSpace(title) ? DefaultTitle : title,
            Width = 880,
            Height = 580,
            MinWidth = 520,
            MinHeight = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.CanResize,
            UseLayoutRounding = true,
            SnapsToDevicePixels = true,
        };

        var owner = GetOwner();
        if (owner is not null && !ReferenceEquals(owner, window)) window.Owner = owner;

        ApplyResource(window, Control.BackgroundProperty, "Bg.Window", Color.FromRgb(0x0E, 0x11, 0x16));
        ApplyResource(window, Control.ForegroundProperty, "Text.Primary", Color.FromRgb(0xED, 0xF1, 0xF7));

        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var textBox = new TextBox
        {
            Text = text ?? string.Empty,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Cascadia Mono, Consolas, Microsoft YaHei UI"),
            FontSize = 12,
            Padding = new Thickness(10),
            BorderThickness = new Thickness(1),
        };
        ApplyResource(textBox, Control.BackgroundProperty, "Bg.Surface", Color.FromRgb(0x14, 0x19, 0x22));
        ApplyResource(textBox, Control.ForegroundProperty, "Text.Primary", Color.FromRgb(0xED, 0xF1, 0xF7));
        ApplyResource(textBox, Control.BorderBrushProperty, "Border.Subtle", Color.FromRgb(0x23, 0x2B, 0x38));
        Grid.SetRow(textBox, 0);
        root.Children.Add(textBox);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        Grid.SetRow(buttons, 1);

        var status = new TextBlock
        {
            Text = string.Empty,
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
        };
        ApplyResource(status, TextBlock.ForegroundProperty, "Text.Tertiary", Color.FromRgb(0x6E, 0x7B, 0x8D));

        var copyButton = new Button
        {
            Content = "复制全部",
            MinWidth = 96,
            Padding = new Thickness(12, 6, 12, 6),
            Margin = new Thickness(0, 0, 8, 0),
        };
        ApplyStyle(copyButton, "SecondaryButton");
        copyButton.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(text ?? string.Empty);
                status.Text = "已复制到剪贴板";
            }
            catch (Exception ex)
            {
                AppLog.Instance.Warn("复制文本失败：" + ex.Message);
                status.Text = "复制失败：" + ex.Message;
            }
        };

        var closeButton = new Button
        {
            Content = "关闭",
            MinWidth = 88,
            Padding = new Thickness(12, 6, 12, 6),
            IsCancel = true,
        };
        ApplyStyle(closeButton, "PrimaryButton");
        closeButton.Click += (_, _) => window.Close();

        buttons.Children.Add(status);
        buttons.Children.Add(copyButton);
        buttons.Children.Add(closeButton);
        root.Children.Add(buttons);

        window.Content = root;
        return window;
    }

    private static Window? GetOwner()
    {
        try
        {
            var main = System.Windows.Application.Current?.MainWindow;
            return main is null || !main.IsLoaded ? (main is null ? null : main) : main;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("获取主窗口失败：" + ex.Message);
            return null;
        }
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            AppLog.Instance.Warn("应用尚未初始化，无法显示对话框");
            return;
        }

        if (dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action);
    }

    /// <summary>优先用主题资源里的画刷，资源缺失时回退到给定的兜底色（保证对话框始终可读）。</summary>
    private static void ApplyResource(FrameworkElement element, DependencyProperty property, string key, Color fallback)
    {
        try
        {
            if (System.Windows.Application.Current?.TryFindResource(key) is not null)
            {
                element.SetResourceReference(property, key);
            }
            else
            {
                element.SetValue(property, new SolidColorBrush(fallback));
            }
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn($"应用主题资源 {key} 失败：{ex.Message}");
            try { element.SetValue(property, new SolidColorBrush(fallback)); } catch { /* 忽略 */ }
        }
    }

    /// <summary>主题样式存在且目标类型匹配时才套用，避免样式缺失导致异常。</summary>
    private static void ApplyStyle(FrameworkElement element, string key)
    {
        try
        {
            if (System.Windows.Application.Current?.TryFindResource(key) is Style style
                && style.TargetType.IsInstanceOfType(element))
            {
                element.Style = style;
            }
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn($"应用控件样式 {key} 失败：{ex.Message}");
        }
    }
}
