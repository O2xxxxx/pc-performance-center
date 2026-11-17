using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ppc.Core.Services;

namespace Ppc.App;

/// <summary>
/// 自检/截图模式：接管启动流程，把每个页面渲染成 PNG 供开发期验证（不参与正常使用）。
/// 用法：PCPerformanceCenter.exe --capture &lt;输出目录&gt; [--delay 毫秒]
/// </summary>
internal static class SelfTest
{
    public static async Task<int> RunAsync(App app, Window window, string outputDirectory, int delayMs = 1600)
    {
        try
        {
            Directory.CreateDirectory(outputDirectory);
            await Task.Delay(delayMs);

            var services = App.Services;
            foreach (var page in services.Pages.ToList())
            {
                services.CurrentPage = page;
                await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
                await Task.Delay(700);
                Capture(window, Path.Combine(outputDirectory, $"{page.Key}.png"));
                AppLog.Instance.Info("自检截图：" + page.Key);
            }

            Capture(window, Path.Combine(outputDirectory, "00-full-window.png"));
            return 0;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("自检截图失败", ex);
            return 1;
        }
    }

    private static void Capture(Window window, string path)
    {
        try
        {
            window.UpdateLayout();
            var content = window.Content as FrameworkElement;
            if (content is null) return;

            var width = (int)Math.Ceiling(content.ActualWidth);
            var height = (int)Math.Ceiling(content.ActualHeight);
            if (width <= 0) width = (int)window.Width;
            if (height <= 0) height = (int)window.Height;

            var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(content);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(path);
            encoder.Save(stream);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("截图失败：" + path, ex);
        }
    }
}
