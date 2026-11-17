using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Ppc.Core.Services;

namespace Ppc.Hardware.Detection;

/// <summary>powercfg 的只读探测结果。ExitCode != 0 或 Error 非空都视为「不可用」。</summary>
internal readonly record struct PowerCfgResult(int ExitCode, string Output, string? Error);

/// <summary>电源相关能力的实测结论。</summary>
internal sealed class PowerCfgFacts
{
    /// <summary>powercfg 是否能正常执行（能列出当前方案）。</summary>
    internal bool Available { get; set; }

    internal string UnavailableReason { get; set; } = string.Empty;
    internal string ActiveSchemeName { get; set; } = string.Empty;
    internal string ActiveSchemeGuid { get; set; } = string.Empty;
    internal int SchemeCount { get; set; }

    /// <summary>当前电源方案是否暴露 PROCTHROTTLEMAX（最大处理器性能状态）。</summary>
    internal bool HasProcessorMaxState { get; set; }

    /// <summary>当前电源方案是否暴露 PERFBOOSTMODE（处理器性能提升模式）。</summary>
    internal bool HasBoostMode { get; set; }

    internal string ProcessorStateNote { get; set; } = string.Empty;
    internal string BoostModeNote { get; set; } = string.Empty;
}

/// <summary>
/// 调用 Windows 自带 powercfg.exe 做只读查询（/getactivescheme、/q）。
/// 只用查询参数，绝不执行 /setacvalueindex、/setactive 之类的写入。
/// 进程输出按 UTF-8 解码：中文可能变成乱码，但 GUID 与退出码都是 ASCII，判定不受影响；
/// 方案名称另从 WMI 的 Win32_PowerPlan 取，避免依赖控制台代码页。
/// </summary>
internal static class PowerCfgProbe
{
    private const string ProcessorMaxStateGuid = "893dee8e-2bef-41e0-89c6-b55d0929964c";
    private const string BoostModeGuid = "be337238-0d82-4146-a960-4f3749d470c7";
    private const int DefaultTimeoutMilliseconds = 8000;

    private static readonly Regex GuidPattern = new(
        "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled);

    internal static PowerCfgFacts Collect(bool isElevated)
    {
        var facts = new PowerCfgFacts();

        var active = Run("/getactivescheme");
        if (active.ExitCode != 0)
        {
            facts.Available = false;
            facts.UnavailableReason = string.IsNullOrWhiteSpace(active.Error)
                ? $"powercfg /getactivescheme 返回退出码 {active.ExitCode}"
                : active.Error!;
            AppLog.Instance.Warn("powercfg 不可用：" + facts.UnavailableReason);
            return facts;
        }

        facts.Available = true;
        var match = GuidPattern.Match(active.Output ?? string.Empty);
        if (match.Success) facts.ActiveSchemeGuid = match.Value;

        facts.ActiveSchemeName = QueryActiveSchemeName(facts.ActiveSchemeGuid, out var schemeCount);
        facts.SchemeCount = schemeCount;
        if (facts.ActiveSchemeName.Length == 0)
        {
            facts.ActiveSchemeName = facts.ActiveSchemeGuid.Length > 0 ? "（名称未能读取）" : "未知";
        }

        var processor = Run("/q SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX");
        facts.HasProcessorMaxState = processor.ExitCode == 0;
        facts.ProcessorStateNote = processor.ExitCode == 0
            ? (ContainsIgnoreCase(processor.Output, ProcessorMaxStateGuid)
                ? "已确认设置项存在（GUID 893dee8e-2bef-41e0-89c6-b55d0929964c）"
                : "powercfg 查询成功")
            : $"powercfg /q 返回退出码 {processor.ExitCode}" + (string.IsNullOrWhiteSpace(processor.Error) ? "（当前方案没有该设置项）" : $"：{processor.Error}");

        var boost = Run("/q SCHEME_CURRENT SUB_PROCESSOR PERFBOOSTMODE");
        facts.HasBoostMode = boost.ExitCode == 0;
        facts.BoostModeNote = boost.ExitCode == 0
            ? (ContainsIgnoreCase(boost.Output, BoostModeGuid)
                ? "已确认设置项存在（GUID be337238-0d82-4146-a960-4f3749d470c7）"
                : "powercfg 查询成功")
            : $"powercfg /q 返回退出码 {boost.ExitCode}" + (string.IsNullOrWhiteSpace(boost.Error) ? "（当前方案没有该设置项）" : $"：{boost.Error}");

        AppLog.Instance.Info(
            $"powercfg 探测：方案「{facts.ActiveSchemeName}」{facts.ActiveSchemeGuid}，共 {facts.SchemeCount} 个方案；" +
            $"PROCTHROTTLEMAX={(facts.HasProcessorMaxState ? "有" : "无")}，PERFBOOSTMODE={(facts.HasBoostMode ? "有" : "无")}，管理员={isElevated}");

        return facts;
    }

    private static string QueryActiveSchemeName(string activeGuid, out int schemeCount)
    {
        schemeCount = 0;
        var rows = WmiProbe.Query(WmiProbe.PowerNamespace, "Win32_PowerPlan", "ElementName", "InstanceID", "IsActive");
        if (rows.Count == 0) return string.Empty;

        schemeCount = rows.Count;
        var fallback = string.Empty;
        foreach (var row in rows)
        {
            var name = WmiProbe.GetString(row, "ElementName");
            if (name.Length == 0) continue;
            if (fallback.Length == 0) fallback = name;

            if (WmiProbe.GetBool(row, "IsActive")) return name;
            if (activeGuid.Length > 0 &&
                WmiProbe.GetString(row, "InstanceID").Contains(activeGuid, StringComparison.OrdinalIgnoreCase))
            {
                return name;
            }
        }
        return fallback;
    }

    private static bool ContainsIgnoreCase(string? text, string value)
        => !string.IsNullOrEmpty(text) && text.Contains(value, StringComparison.OrdinalIgnoreCase);

    private static PowerCfgResult Run(string arguments, int timeoutMilliseconds = DefaultTimeoutMilliseconds)
    {
        try
        {
            var startInfo = new ProcessStartInfo(ResolveExecutable(), arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var process = Process.Start(startInfo);
            if (process is null) return new PowerCfgResult(-1, string.Empty, "无法启动 powercfg.exe");

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeoutMilliseconds))
            {
                try { process.Kill(true); }
                catch (Exception ex) { AppLog.Instance.Warn("结束 powercfg 进程失败：" + ex.Message); }
                return new PowerCfgResult(-1, ReadSafely(outputTask), $"powercfg 执行超时（{timeoutMilliseconds} ms）");
            }

            process.WaitForExit();
            var output = ReadSafely(outputTask);
            var error = ReadSafely(errorTask);
            return new PowerCfgResult(process.ExitCode, output, string.IsNullOrWhiteSpace(error) ? null : error.Trim());
        }
        catch (Exception ex)
        {
            var message = $"{ex.GetType().Name}：{ex.Message}";
            AppLog.Instance.Warn("执行 powercfg 失败：" + message);
            return new PowerCfgResult(-1, string.Empty, message);
        }
    }

    private static string ReadSafely(Task<string> task)
    {
        try
        {
            if (!task.Wait(TimeSpan.FromMilliseconds(1500))) return string.Empty;
            return task.IsCompletedSuccessfully ? task.Result ?? string.Empty : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ResolveExecutable()
    {
        try
        {
            var systemDirectory = Environment.SystemDirectory;
            if (!string.IsNullOrWhiteSpace(systemDirectory))
            {
                var candidate = Path.Combine(systemDirectory, "powercfg.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("定位 System32 失败，改用 PATH 查找 powercfg：" + ex.Message);
        }
        return "powercfg.exe";
    }
}
