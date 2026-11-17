using System.Runtime.InteropServices;
using Ppc.Core.Services;

namespace Ppc.Hardware.Power;

/// <summary>Windows 电源方案 / 处理器电源设置的官方 GUID 常量（全部来自 Windows 公开文档，本机实测有效）。</summary>
internal static class PowerGuids
{
    /// <summary>节能（Power saver）方案。本机默认未出现在 powercfg /list 里，需要 -duplicatescheme 复制。</summary>
    public static readonly Guid SchemeEco = new("a1841308-3541-4fab-bc81-f71556f20b4a");

    /// <summary>平衡方案。</summary>
    public static readonly Guid SchemeBalanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");

    /// <summary>高性能方案。</summary>
    public static readonly Guid SchemeHighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    /// <summary>卓越性能方案（部分版本才有）。</summary>
    public static readonly Guid SchemeUltimatePerformance = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    /// <summary>处理器电源管理子组 SUB_PROCESSOR。</summary>
    public static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");

    /// <summary>最大处理器状态 PROCTHROTTLEMAX。</summary>
    public static readonly Guid MaxProcessorState = new("bc5038f7-23e0-4960-96da-33abaf5935ec");

    /// <summary>最小处理器状态 PROCTHROTTLEMIN（官方 GUID；本机 powercfg /q 实测为 893dee8e-2bef-41e0-89c6-b55d0929964c）。</summary>
    public static readonly Guid MinProcessorState = new("893dee8e-2bef-41e0-89c6-b55d0929964c");

    /// <summary>处理器性能提升模式 PERFBOOSTMODE（隐藏设置，powercfg /q 不列出，需要读注册表）。</summary>
    public static readonly Guid BoostMode = new("be337238-0d82-4146-a960-4f3749d470c7");

    /// <summary>Windows 11「电源模式」覆盖：平衡（等价于无覆盖）。</summary>
    public static readonly Guid OverlayBalanced = Guid.Empty;

    /// <summary>Windows 11「电源模式」覆盖：最佳能效。</summary>
    public static readonly Guid OverlayBestEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");

    /// <summary>Windows 11「电源模式」覆盖：最佳性能。</summary>
    public static readonly Guid OverlayBestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");
}

/// <summary>
/// 调用 powrprof.dll 的 PowerSetActiveOverlayScheme（Windows 10 1809+ 提供的官方电源模式覆盖 API）。
/// powercfg /overlaySetScheme 在部分版本上不可用，这里作为第二条真实通路。
/// </summary>
internal static class NativePowerOverlay
{
    [DllImport("powrprof.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint PowerSetActiveOverlayScheme(Guid overlaySchemeGuid);

    public static bool TryApply(Guid overlay, out string message)
    {
        try
        {
            var code = PowerSetActiveOverlayScheme(overlay);
            if (code == 0)
            {
                message = "PowerSetActiveOverlayScheme 返回 0";
                return true;
            }

            message = $"PowerSetActiveOverlayScheme 返回错误码 {code}";
            AppLog.Instance.Warn(message);
            return false;
        }
        catch (DllNotFoundException ex)
        {
            message = "powrprof.dll 不可用：" + ex.Message;
            AppLog.Instance.Warn(message);
            return false;
        }
        catch (EntryPointNotFoundException ex)
        {
            message = "powrprof.dll 未导出 PowerSetActiveOverlayScheme（该 Windows 版本不支持电源模式覆盖）：" + ex.Message;
            AppLog.Instance.Warn(message);
            return false;
        }
        catch (Exception ex)
        {
            message = "PowerSetActiveOverlayScheme 调用异常：" + ex.Message;
            AppLog.Instance.Error(message, ex);
            return false;
        }
    }
}

/// <summary>首次改动电源设置前保存的原始状态，用于 RestoreAsync 还原。</summary>
internal sealed class PowerSnapshot
{
    public DateTimeOffset CapturedAt { get; set; }

    public string ActiveSchemeGuid { get; set; } = string.Empty;

    public string ActiveSchemeName { get; set; } = string.Empty;

    public string OverlayGuidAc { get; set; } = string.Empty;

    public string OverlayGuidDc { get; set; } = string.Empty;

    public int? MaxProcessorStateAc { get; set; }

    public int? MaxProcessorStateDc { get; set; }

    public int? MinProcessorStateAc { get; set; }

    public int? MinProcessorStateDc { get; set; }

    public int? BoostModeAc { get; set; }

    public int? BoostModeDc { get; set; }
}
