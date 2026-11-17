using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Ppc.Core.Services;

namespace Ppc.Hardware.Power;

/// <summary>
/// 一次 powercfg.exe 调用的原始结果。成功与否只看 ExitCode，绝不由调用方臆测。
/// </summary>
internal readonly struct PowerCfgResult
{
    public PowerCfgResult(int exitCode, string standardOutput, string standardError)
    {
        ExitCode = exitCode;
        StandardOutput = standardOutput ?? string.Empty;
        StandardError = standardError ?? string.Empty;
    }

    /// <summary>进程退出码；-1 表示根本没启动起来，-2 表示超时被终止。</summary>
    public int ExitCode { get; }

    public string StandardOutput { get; }

    public string StandardError { get; }

    public bool Success => ExitCode == 0;

    /// <summary>把 stderr（没输出就用 stdout）压成一行，方便写进 OpResult.Message。</summary>
    public string Detail(int maxLength = 300)
    {
        var text = string.IsNullOrWhiteSpace(StandardError) ? StandardOutput : StandardError;
        text = text.Trim();
        if (text.Length == 0) return "（powercfg 未输出任何信息）";
        text = string.Join(" ", text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim()));
        return text.Length <= maxLength ? text : text[..maxLength] + "…";
    }
}

/// <summary>
/// powercfg.exe 的薄封装：无窗口、重定向输出、UTF-8 优先解码、默认 8 秒超时。
/// 唯一职责是把「命令行调用 + 原始结果」这件事实事求是地带回来。
/// </summary>
internal sealed class PowerCfgRunner
{
    private const int DefaultTimeoutMs = 8000;

    private static readonly Lazy<PowerCfgRunner> LazyInstance = new(() => new PowerCfgRunner(), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private PowerCfgRunner()
    {
        ExecutablePath = ResolveExecutable();
    }

    public static PowerCfgRunner Instance => LazyInstance.Value;

    /// <summary>powercfg.exe 的绝对路径；为 null 表示系统上找不到（极罕见，只有精简系统才会发生）。</summary>
    public string? ExecutablePath { get; }

    public bool IsAvailable => !string.IsNullOrEmpty(ExecutablePath);

    /// <summary>同步执行一条 powercfg 命令。调用方负责用 Task.Run 包住以免阻塞 UI 线程。</summary>
    public PowerCfgResult Run(IEnumerable<string> arguments, int timeoutMs = DefaultTimeoutMs)
    {
        if (!IsAvailable)
            return new PowerCfgResult(-1, string.Empty, "未找到 powercfg.exe，无法访问 Windows 电源方案");

        var startInfo = new ProcessStartInfo
        {
            FileName = ExecutablePath!,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                return new PowerCfgResult(-1, string.Empty, "powercfg 进程未能启动");
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("启动 powercfg 失败", ex);
            return new PowerCfgResult(-1, string.Empty, "无法启动 powercfg：" + ex.Message);
        }

        // 先异步把两个管道抽干，避免子进程因管道写满而卡住
        var stdoutTask = Task.Run(() => ReadAllBytes(process.StandardOutput.BaseStream));
        var stderrTask = Task.Run(() => ReadAllBytes(process.StandardError.BaseStream));

        bool exited;
        try
        {
            exited = process.WaitForExit(timeoutMs);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("等待 powercfg 退出失败", ex);
            exited = false;
        }

        if (!exited)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (Exception ex) { AppLog.Instance.Warn("终止超时的 powercfg 失败：" + ex.Message); }
            return new PowerCfgResult(-2, string.Empty, $"powercfg 超过 {timeoutMs} 毫秒未返回，已强制终止");
        }

        try { Task.WaitAll(new Task[] { stdoutTask, stderrTask }, 2000); }
        catch (Exception ex) { AppLog.Instance.Warn("等待 powercfg 输出读取任务失败：" + ex.Message); }

        var stdout = Decode(stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : Array.Empty<byte>());
        var stderr = Decode(stderrTask.IsCompletedSuccessfully ? stderrTask.Result : Array.Empty<byte>());

        int exitCode;
        try { exitCode = process.ExitCode; }
        catch (Exception ex)
        {
            AppLog.Instance.Error("读取 powercfg 退出码失败", ex);
            exitCode = -1;
        }

        return new PowerCfgResult(exitCode, stdout, stderr);
    }

    private static byte[] ReadAllBytes(Stream stream)
    {
        try
        {
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            return buffer.ToArray();
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("读取 powercfg 输出流失败：" + ex.Message);
            return Array.Empty<byte>();
        }
    }

    /// <summary>
    /// powercfg 的输出编码跟随控制台代码页：GUI 进程没有控制台时通常是系统 ANSI 代码页（简体中文系统为 GBK/936）。
    /// 先按严格 UTF-8 试（终端/UTF-8 控制台场景），失败再用 MultiByteToWideChar(CP_ACP) 解码，最后退化为 Latin1。
    /// </summary>
    private static string Decode(byte[] bytes)
    {
        if (bytes.Length == 0) return string.Empty;

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // 不是 UTF-8，按系统 ANSI 代码页解码
        }

        return DecodeAnsi(bytes);
    }

    private static string DecodeAnsi(byte[] bytes)
    {
        try
        {
            var needed = MultiByteToWideChar(CpAcp, 0, bytes, bytes.Length, null, 0);
            if (needed <= 0) return Encoding.Latin1.GetString(bytes);

            var buffer = new char[needed];
            var written = MultiByteToWideChar(CpAcp, 0, bytes, bytes.Length, buffer, needed);
            if (written <= 0) return Encoding.Latin1.GetString(bytes);

            return new string(buffer, 0, written);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("按系统 ANSI 代码页解码 powercfg 输出失败，退化为 Latin1：" + ex.Message);
            return Encoding.Latin1.GetString(bytes);
        }
    }

    private const uint CpAcp = 0;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MultiByteToWideChar(uint codePage, uint flags, byte[] bytes, int byteCount, char[]? wideChars, int wideCharCount);

    private static string? ResolveExecutable()
    {
        var candidates = new List<string>();
        try
        {
            var system32 = Environment.SystemDirectory;
            if (!string.IsNullOrEmpty(system32)) candidates.Add(Path.Combine(system32, "powercfg.exe"));
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("读取 System32 目录失败：" + ex.Message);
        }

        try
        {
            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            if (!string.IsNullOrEmpty(windows))
            {
                candidates.Add(Path.Combine(windows, "System32", "powercfg.exe"));
                // 32 位进程在 64 位系统上访问 System32 会被重定向，Sysnative 才是真实的 64 位目录
                if (!Environment.Is64BitProcess) candidates.Add(Path.Combine(windows, "Sysnative", "powercfg.exe"));
            }
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("读取 Windows 目录失败：" + ex.Message);
        }

        foreach (var candidate in candidates)
        {
            try
            {
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception ex)
            {
                AppLog.Instance.Warn("检查 powercfg.exe 是否存在失败：" + ex.Message);
            }
        }

        try
        {
            var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            foreach (var directory in path.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = directory.Trim().Trim('"');
                if (trimmed.Length == 0) continue;
                var candidate = Path.Combine(trimmed, "powercfg.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("在 PATH 中查找 powercfg.exe 失败：" + ex.Message);
        }

        AppLog.Instance.Warn("未找到 powercfg.exe（System32 与 PATH 均无），性能调节功能将返回「不支持」");
        return null;
    }
}
