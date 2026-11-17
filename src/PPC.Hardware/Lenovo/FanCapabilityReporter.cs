namespace Ppc.Hardware.Lenovo;

/// <summary>风扇能力上报所需的原始事实（全部来自真实探测，不含推测）。</summary>
public sealed record FanCapabilityInput(
    bool SupportsControl,
    string PlatformName,
    bool GameZonePresent,
    string GameZoneInstance,
    string GameZoneProbeDetail,
    bool FanTablePresent,
    string FanTableProbeDetail,
    int ChannelCount,
    string? DegradedReason);

/// <summary>
/// 把联想风扇的监控/控制支持情况写成中文多行文本，供风扇页与设置页直接展示。
/// 只陈述探测到的事实与真实异常文本，不美化、不承诺。
/// </summary>
public static class FanCapabilityReporter
{
    /// <summary>本机型的固定结论句（机型名从注册表读取）。</summary>
    public static string UnsupportedSentence(string platformName) =>
        $"本机型（{platformName}）的 ACPI/WMI 接口存在但调用被固件拒绝，无法第三方控制风扇；已保留监控与自动模式。";

    public static string Describe(FanCapabilityInput input)
    {
        var lines = new List<string>
        {
            $"联想风扇支持情况（{input.PlatformName}）"
        };

        // 监控部分
        if (input.ChannelCount > 0)
        {
            lines.Add($"监控：可显示 {input.ChannelCount} 个风扇通道的转速（LibreHardwareMonitor 提供的温度/功耗同时在用）。");
        }
        else
        {
            lines.Add("监控：可以显示 CPU/GPU 温度与功耗，但系统没有提供可读的风扇转速，风扇转速显示为「不可用」。");
        }

        // 控制部分
        if (input.SupportsControl)
        {
            lines.Add("控制：可用。已通过厂商 ACPI/WMI 只读探测，写操作会在探测通过后才执行。");
        }
        else
        {
            lines.Add("控制：不可用。");
            if (!string.IsNullOrWhiteSpace(input.DegradedReason))
            {
                lines.Add($"原因：运行期间厂商接口报错，已自动降级为只读——{input.DegradedReason}");
            }
            else
            {
                lines.Add("原因：" + UnsupportedSentence(input.PlatformName));
            }
        }

        // 探测细节
        lines.Add("探测细节：");
        var gameZoneState = input.GameZonePresent
            ? $"实例 {input.GameZoneInstance}"
            : "未找到该类实例";
        lines.Add($"- LENOVO_GAMEZONE_DATA：{gameZoneState}；只读探测结果：{Flatten(input.GameZoneProbeDetail)}");

        var fanTableState = input.FanTablePresent ? "类存在" : "未找到该类实例";
        lines.Add($"- LENOVO_FAN_METHOD：{fanTableState}；只读探测结果：{Flatten(input.FanTableProbeDetail)}");

        // 边界说明
        lines.Add("说明：本程序不直写 EC 寄存器、不修改 BIOS、不绕过固件保护，也不会把风扇设成 0%。");
        if (!input.SupportsControl)
        {
            lines.Add("建议：散热模式可在厂商自带软件（联想电脑管家 / Lenovo Vantage）里调整；本程序只做监控与只读诊断。");
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>把多行探测文本压成一行，便于嵌进列表项。</summary>
    private static string Flatten(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "无";
        var oneLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
        while (oneLine.Contains("  ", StringComparison.Ordinal))
        {
            oneLine = oneLine.Replace("  ", " ", StringComparison.Ordinal);
        }

        return oneLine;
    }
}
