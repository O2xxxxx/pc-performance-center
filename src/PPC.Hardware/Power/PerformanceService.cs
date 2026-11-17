using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ppc.Core.Abstractions;
using Ppc.Core.Models;
using Ppc.Core.Services;

namespace Ppc.Hardware.Power;

/// <summary>
/// 性能模式服务：全部通过 Windows 官方接口（powercfg.exe + 注册表电源设置 + powrprof 电源模式覆盖）真实写入，
/// 不做任何模拟。写入失败一律返回真实原因，绝不伪造成功。
/// </summary>
public sealed class PerformanceService : IPerformanceService
{
    private const int CliTimeoutMs = 8000;
    private const int MinAllowedPercent = 5;
    private const int MaxAllowedPercent = 100;

    private static readonly JsonSerializerOptions SnapshotJsonOptions = new() { WriteIndented = true };
    private static readonly Regex GuidRegex = new(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);
    private static readonly Regex HexValueRegex = new(@"0x([0-9a-fA-F]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AcMarkerRegex = new(@"AC", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex DcMarkerRegex = new(@"DC", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly SettingsStore _store;
    private readonly string _snapshotPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly PowerCfgRunner _runner = PowerCfgRunner.Instance;

    public PerformanceService(SettingsStore store)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _snapshotPath = Path.Combine(_store.Directory, "power-snapshot.json");
    }

    /// <summary>读取当前电源方案与处理器设置的真实状态。</summary>
    public async Task<PerformanceState> GetStateAsync()
    {
        try
        {
            if (!_runner.IsAvailable)
            {
                AppLog.Instance.Warn("GetStateAsync：未找到 powercfg.exe，无法读取 Windows 电源方案");
                return new PerformanceState { Summary = "未找到 powercfg.exe，无法读取 Windows 电源方案；性能调节不可用。" };
            }

            var capture = await Task.Run(Capture).ConfigureAwait(false);
            return capture.State;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("读取电源状态失败", ex);
            return new PerformanceState { Summary = "读取电源状态失败：" + ex.Message };
        }
    }

    /// <summary>应用指定的性能模式；首次调用会先保存原始电源状态快照。</summary>
    public async Task<OpResult> ApplyModeAsync(PerformanceMode mode, CustomPerfProfile custom)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_runner.IsAvailable)
                return OpResult.Unsupported("未找到 powercfg.exe，无法修改 Windows 电源方案");

            custom ??= new CustomPerfProfile();
            await EnsureSnapshotAsync().ConfigureAwait(false);

            OpResult result;
            switch (mode)
            {
                case PerformanceMode.Eco:
                    result = await ApplyEcoAsync().ConfigureAwait(false);
                    break;
                case PerformanceMode.Balanced:
                    result = await ApplyBalancedAsync().ConfigureAwait(false);
                    break;
                case PerformanceMode.Performance:
                    result = await ApplyPerformanceAsync().ConfigureAwait(false);
                    break;
                case PerformanceMode.Custom:
                    result = await ApplyCustomAsync(custom).ConfigureAwait(false);
                    break;
                default:
                    result = OpResult.Fail("未知的性能模式：" + mode);
                    break;
            }

            AppLog.Instance.Info($"应用性能模式 {mode}：{(result.Success ? "成功" : "失败")} {result.Message}");
            return result;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error($"应用性能模式 {mode} 时异常", ex);
            return OpResult.Fail("应用性能模式时发生异常：" + ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>用首次改动前保存的快照恢复原始电源状态；没有快照时直接返回「无需恢复」。</summary>
    public async Task<OpResult> RestoreAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!File.Exists(_snapshotPath)) return OpResult.Ok("无需恢复");
            if (!_runner.IsAvailable) return OpResult.Unsupported("未找到 powercfg.exe，无法恢复原始电源设置");

            var snapshot = LoadSnapshot();
            if (snapshot is null)
                return OpResult.Fail($"电源状态快照无法解析：{_snapshotPath}（未做任何改动）");

            var errors = new List<string>();

            if (Guid.TryParse(snapshot.ActiveSchemeGuid, out var originalScheme))
            {
                var activate = await Task.Run(() => _runner.Run(new[] { "-setactive", originalScheme.ToString("D") }, CliTimeoutMs)).ConfigureAwait(false);
                if (!activate.Success) errors.Add("恢复原始电源方案失败：" + Describe(activate));
            }
            else
            {
                errors.Add("快照中没有有效的电源方案 GUID");
            }

            await RestoreProcessorSettingsAsync(snapshot, errors).ConfigureAwait(false);
            await RestoreOverlayAsync(snapshot).ConfigureAwait(false);
            await ActivateCurrentAsync(errors).ConfigureAwait(false);

            if (errors.Count == 0)
            {
                TryDeleteSnapshot();
                var name = string.IsNullOrWhiteSpace(snapshot.ActiveSchemeName) ? snapshot.ActiveSchemeGuid : snapshot.ActiveSchemeName;
                AppLog.Instance.Info("已恢复原始电源状态：" + name);
                return OpResult.Ok($"已恢复原始电源方案「{name}」及其处理器设置");
            }

            AppLog.Instance.Warn("恢复原始电源状态未完全成功：" + string.Join("；", errors));
            return OpResult.Fail("恢复未完全成功：" + string.Join("；", errors) + "。快照已保留，可重试。");
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("恢复原始电源状态异常", ex);
            return OpResult.Fail("恢复原始电源状态时异常：" + ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>本机联想私有性能模式不可用（GameZone WMI 为空壳），接口保留但固定返回 false。</summary>
    public Task<bool> IsLenovoModeSupportedAsync()
    {
        AppLog.Instance.Info("联想私有性能模式：本机 LENOVO_GAMEZONE_DATA（ACPI\\PNP0C14\\GMZN_0）方法表存在但调用全部失败（0x8004102F / 无效的对象），因此判定为不支持");
        return Task.FromResult(false);
    }

    // ---------- 各模式的具体实现 ----------

    private async Task<OpResult> ApplyEcoAsync()
    {
        var errors = new List<string>();
        var activated = await EnsureSchemeAsync(PowerGuids.SchemeEco, "节能", errors).ConfigureAwait(false);
        if (!activated) return OpResult.Fail("切换到节能电源方案失败：" + string.Join("；", errors));

        // 节能优先：顺手把 Windows 电源模式覆盖设为「最佳能效」，失败不算错（老系统没有覆盖功能）
        await TrySetOverlayAsync(PowerGuids.OverlayBestEfficiency, null, "Windows 电源模式「最佳能效」").ConfigureAwait(false);
        await WriteProcessorValuesAsync(60, null, 0, errors).ConfigureAwait(false);
        await ActivateCurrentAsync(errors).ConfigureAwait(false);

        if (errors.Count > 0) return OpResult.Fail("节能模式未完全生效：" + string.Join("；", errors));
        return OpResult.Ok("已切换到节能模式：「节能」电源方案，最大处理器状态 60%，睿频已禁用。");
    }

    private async Task<OpResult> ApplyBalancedAsync()
    {
        var errors = new List<string>();
        var activated = await EnsureSchemeAsync(PowerGuids.SchemeBalanced, "平衡", errors).ConfigureAwait(false);
        if (!activated) return OpResult.Fail("切换到平衡电源方案失败：" + string.Join("；", errors));

        await TrySetOverlayAsync(PowerGuids.OverlayBalanced, null, "Windows 电源模式覆盖复位").ConfigureAwait(false);
        await WriteProcessorValuesAsync(100, null, 1, errors).ConfigureAwait(false);
        await ActivateCurrentAsync(errors).ConfigureAwait(false);

        if (errors.Count > 0) return OpResult.Fail("平衡模式未完全生效：" + string.Join("；", errors));
        return OpResult.Ok("已切换到平衡模式：「平衡」电源方案，最大处理器状态 100%，睿频已启用。");
    }

    private async Task<OpResult> ApplyPerformanceAsync()
    {
        var errors = new List<string>();

        // 优先走 Windows 11 电源模式覆盖；不支持时退化为「高性能」电源方案
        var overlayApplied = await TrySetOverlayAsync(PowerGuids.OverlayBestPerformance, null, "Windows 电源模式「最佳性能」").ConfigureAwait(false);
        string schemeText;
        if (overlayApplied)
        {
            schemeText = "Windows 电源模式覆盖「最佳性能」";
        }
        else
        {
            AppLog.Instance.Info("Windows 电源模式覆盖不可用，退化为「高性能」电源方案");
            var activated = await EnsureSchemeAsync(PowerGuids.SchemeHighPerformance, "高性能", errors).ConfigureAwait(false);
            if (!activated) return OpResult.Fail("切换到性能模式失败：" + string.Join("；", errors));
            schemeText = "「高性能」电源方案";
        }

        await WriteProcessorValuesAsync(100, null, 2, errors).ConfigureAwait(false);
        await ActivateCurrentAsync(errors).ConfigureAwait(false);

        if (errors.Count > 0) return OpResult.Fail("性能模式未完全生效：" + string.Join("；", errors));
        return OpResult.Ok($"已切换到性能模式：{schemeText}，最大处理器状态 100%，处理器性能提升模式为「高效」。");
    }

    private async Task<OpResult> ApplyCustomAsync(CustomPerfProfile custom)
    {
        var max = Math.Clamp(custom.MaxProcessorStatePercent ?? MaxAllowedPercent, MinAllowedPercent, MaxAllowedPercent);
        var min = Math.Clamp(custom.MinProcessorStatePercent ?? MinAllowedPercent, MinAllowedPercent, MaxAllowedPercent);
        if (min > max) min = max;
        var boost = custom.BoostMode.HasValue ? Math.Clamp(custom.BoostMode.Value, 0, 2) : 2;

        if (custom.UseLenovoPerformanceMode)
            AppLog.Instance.Info("自定义模式要求使用联想性能模式，但本机 GameZone WMI 不可用，已忽略该选项");

        var errors = new List<string>();
        await WriteProcessorValuesAsync(max, min, boost, errors).ConfigureAwait(false);
        await ActivateCurrentAsync(errors).ConfigureAwait(false);

        if (errors.Count > 0) return OpResult.Fail("自定义模式未完全生效：" + string.Join("；", errors));
        return OpResult.Ok($"已应用自定义模式：最大处理器状态 {max}%，最小处理器状态 {min}%，处理器性能提升模式「{DescribeBoost(boost)}」。");
    }

    // ---------- powercfg 写入 ----------

    private async Task<bool> EnsureSchemeAsync(Guid scheme, string label, List<string> errors)
    {
        var id = scheme.ToString("D");
        var activate = await Task.Run(() => _runner.Run(new[] { "-setactive", id }, CliTimeoutMs)).ConfigureAwait(false);
        if (activate.Success) return true;

        AppLog.Instance.Warn($"直接激活「{label}」方案失败：{Describe(activate)}，尝试复制内置方案后再激活");
        var before = await ListSchemesAsync().ConfigureAwait(false);
        var duplicate = await Task.Run(() => _runner.Run(new[] { "-duplicatescheme", id }, CliTimeoutMs)).ConfigureAwait(false);
        if (!duplicate.Success)
        {
            errors.Add($"激活「{label}」方案失败：{Describe(activate)}；复制内置方案也失败：{Describe(duplicate)}");
            return false;
        }

        var after = await ListSchemesAsync().ConfigureAwait(false);
        before.IntersectWith(after);
        var created = after.Except(before).FirstOrDefault();
        if (created == Guid.Empty)
        {
            errors.Add($"复制内置「{label}」方案后未能识别新方案 GUID：{duplicate.Detail()}");
            return false;
        }

        var activateCopy = await Task.Run(() => _runner.Run(new[] { "-setactive", created.ToString("D") }, CliTimeoutMs)).ConfigureAwait(false);
        if (activateCopy.Success)
        {
            AppLog.Instance.Info($"已把内置「{label}」方案复制为 {created:D} 并激活");
            return true;
        }

        errors.Add($"复制得到的「{label}」方案激活失败：{Describe(activateCopy)}");
        return false;
    }

    /// <summary>写入处理器设置（AC/DC 各写一次），收集真实失败原因。</summary>
    private async Task WriteProcessorValuesAsync(int? maxPercent, int? minPercent, int? boostMode, List<string> errors)
    {
        var settings = await QueryProcessorSettingsAsync().ConfigureAwait(false);
        var maxGuid = settings.Resolve("PROCTHROTTLEMAX", PowerGuids.MaxProcessorState);
        var minGuid = settings.Resolve("PROCTHROTTLEMIN", PowerGuids.MinProcessorState);
        var boostGuid = settings.Resolve("PERFBOOSTMODE", PowerGuids.BoostMode);

        await WriteSettingAsync(maxGuid, "最大处理器状态", maxPercent, maxPercent, errors).ConfigureAwait(false);
        await WriteSettingAsync(minGuid, "最小处理器状态", minPercent, minPercent, errors).ConfigureAwait(false);
        await WriteSettingAsync(boostGuid, "处理器性能提升模式", boostMode, boostMode, errors).ConfigureAwait(false);
    }

    private async Task WriteSettingAsync(Guid setting, string label, int? acValue, int? dcValue, List<string> errors)
    {
        var subgroup = PowerGuids.SubProcessor.ToString("D");
        var settingId = setting.ToString("D");

        if (acValue.HasValue)
        {
            var text = acValue.Value.ToString(CultureInfo.InvariantCulture);
            var ac = await Task.Run(() => _runner.Run(new[] { "-setacvalueindex", "SCHEME_CURRENT", subgroup, settingId, text }, CliTimeoutMs)).ConfigureAwait(false);
            if (!ac.Success) errors.Add($"{label}（交流）写入失败：{Describe(ac)}");
        }

        if (dcValue.HasValue)
        {
            var text = dcValue.Value.ToString(CultureInfo.InvariantCulture);
            var dc = await Task.Run(() => _runner.Run(new[] { "-setdcvalueindex", "SCHEME_CURRENT", subgroup, settingId, text }, CliTimeoutMs)).ConfigureAwait(false);
            if (!dc.Success) errors.Add($"{label}（电池）写入失败：{Describe(dc)}");
        }
    }

    private async Task ActivateCurrentAsync(List<string> errors)
    {
        var activate = await Task.Run(() => _runner.Run(new[] { "-setactive", "SCHEME_CURRENT" }, CliTimeoutMs)).ConfigureAwait(false);
        if (!activate.Success) errors.Add("让电源设置生效（-setactive SCHEME_CURRENT）失败：" + Describe(activate));
    }

    /// <summary>设置 Windows 11 电源模式覆盖；powercfg 与 powrprof API 两条通路都会尝试，并用注册表回读校验真实结果。</summary>
    private async Task<bool> TrySetOverlayAsync(Guid overlay, List<string>? errors, string label)
    {
        var id = overlay.ToString("D");
        var cli = await Task.Run(() => _runner.Run(new[] { "/overlaySetScheme", id }, CliTimeoutMs)).ConfigureAwait(false);
        if (cli.Success && await VerifyOverlayAsync(overlay).ConfigureAwait(false))
        {
            AppLog.Instance.Info($"{label} 已生效（powercfg /overlaySetScheme）");
            return true;
        }

        var apiApplied = NativePowerOverlay.TryApply(overlay, out var apiMessage);
        if (apiApplied && await VerifyOverlayAsync(overlay).ConfigureAwait(false))
        {
            AppLog.Instance.Info($"{label} 已生效（PowerSetActiveOverlayScheme）");
            return true;
        }

        var reason = $"powercfg /overlaySetScheme 退出码 {cli.ExitCode}（{cli.Detail(120)}）；{apiMessage}";
        if (errors is not null) errors.Add($"{label} 未生效：{reason}");
        else AppLog.Instance.Info($"{label} 未生效（该系统不支持电源模式覆盖，已跳过）：{reason}");
        return false;
    }

    private async Task<bool> VerifyOverlayAsync(Guid overlay)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await Task.Delay(80).ConfigureAwait(false);
            var ac = PowerRegistry.ReadOverlayAc();
            var dc = PowerRegistry.ReadOverlayDc();
            if (ac == overlay || dc == overlay) return true;
        }
        return false;
    }

    private async Task<SubgroupQuery> QueryProcessorSettingsAsync()
    {
        var query = await Task.Run(() => _runner.Run(new[] { "/q", "SCHEME_CURRENT", "SUB_PROCESSOR" }, CliTimeoutMs)).ConfigureAwait(false);
        if (!query.Success)
        {
            AppLog.Instance.Warn("查询处理器电源设置失败：" + Describe(query));
            return SubgroupQuery.Empty;
        }
        return ParseSubgroupQuery(query.StandardOutput);
    }

    private async Task<HashSet<Guid>> ListSchemesAsync()
    {
        var list = await Task.Run(() => _runner.Run(new[] { "/list" }, CliTimeoutMs)).ConfigureAwait(false);
        var schemes = new HashSet<Guid>();
        if (!list.Success)
        {
            AppLog.Instance.Warn("列出电源方案失败：" + Describe(list));
            return schemes;
        }

        foreach (Match match in GuidRegex.Matches(list.StandardOutput))
        {
            if (Guid.TryParse(match.Value, out var guid)) schemes.Add(guid);
        }
        return schemes;
    }

    // ---------- 状态读取与快照 ----------

    private (PerformanceState State, PowerSnapshot Snapshot) Capture()
    {
        var state = new PerformanceState();
        var snapshot = new PowerSnapshot { CapturedAt = DateTimeOffset.Now };

        var active = _runner.Run(new[] { "/getactivescheme" }, CliTimeoutMs);
        if (!active.Success)
        {
            state.Summary = "读取当前电源方案失败：" + Describe(active);
            AppLog.Instance.Warn(state.Summary);
            return (state, snapshot);
        }

        ParseActiveScheme(active.StandardOutput, out var schemeGuid, out var schemeName);
        state.PowerSchemeGuid = schemeGuid;
        state.PowerSchemeName = schemeName;
        snapshot.ActiveSchemeGuid = schemeGuid?.ToString("D") ?? string.Empty;
        snapshot.ActiveSchemeName = schemeName;

        var query = _runner.Run(new[] { "/q", "SCHEME_CURRENT", "SUB_PROCESSOR" }, CliTimeoutMs);
        var settings = query.Success ? ParseSubgroupQuery(query.StandardOutput) : SubgroupQuery.Empty;
        if (!query.Success) AppLog.Instance.Warn("读取处理器电源设置失败：" + Describe(query));

        var maxGuid = settings.Resolve("PROCTHROTTLEMAX", PowerGuids.MaxProcessorState);
        var minGuid = settings.Resolve("PROCTHROTTLEMIN", PowerGuids.MinProcessorState);
        var boostGuid = settings.Resolve("PERFBOOSTMODE", PowerGuids.BoostMode);

        var maxValues = settings.Get(maxGuid);
        var minValues = settings.Get(minGuid);
        var boostValues = settings.Get(boostGuid);

        int? maxAc = null;
        int? maxDc = null;
        if (maxValues.HasValue)
        {
            maxAc = maxValues.Value.Ac;
            maxDc = maxValues.Value.Dc;
        }

        int? minAc = null;
        int? minDc = null;
        if (minValues.HasValue)
        {
            minAc = minValues.Value.Ac;
            minDc = minValues.Value.Dc;
        }

        // 睿频策略是隐藏设置，powercfg /q 不输出，回退到注册表里的每方案覆盖值
        int? boostAc = null;
        int? boostDc = null;
        if (boostValues.HasValue)
        {
            boostAc = boostValues.Value.Ac;
            boostDc = boostValues.Value.Dc;
        }
        else if (schemeGuid.HasValue)
        {
            boostAc = PowerRegistry.ReadSetting(schemeGuid.Value, PowerGuids.SubProcessor, boostGuid, useAc: true);
            boostDc = PowerRegistry.ReadSetting(schemeGuid.Value, PowerGuids.SubProcessor, boostGuid, useAc: false);
        }

        state.MaxProcessorState = maxAc;
        state.MinProcessorState = minAc;
        state.BoostMode = boostAc;

        snapshot.MaxProcessorStateAc = maxAc;
        snapshot.MaxProcessorStateDc = maxDc;
        snapshot.MinProcessorStateAc = minAc;
        snapshot.MinProcessorStateDc = minDc;
        snapshot.BoostModeAc = boostAc;
        snapshot.BoostModeDc = boostDc;

        var overlayAc = PowerRegistry.ReadOverlayAc();
        var overlayDc = PowerRegistry.ReadOverlayDc();
        snapshot.OverlayGuidAc = overlayAc?.ToString("D") ?? string.Empty;
        snapshot.OverlayGuidDc = overlayDc?.ToString("D") ?? string.Empty;

        var effectiveOverlay = overlayAc is { } ac && ac != Guid.Empty ? overlayAc : overlayDc;
        state.WindowsPowerMode = DescribeOverlay(effectiveOverlay);
        state.LenovoThermalMode = "不支持（联想 GameZone WMI 在本机为空壳）";
        state.Mode = InferMode(schemeGuid, maxAc, effectiveOverlay);
        state.Summary = BuildSummary(state);
        return (state, snapshot);
    }

    private async Task EnsureSnapshotAsync()
    {
        try
        {
            if (File.Exists(_snapshotPath)) return;

            var capture = await Task.Run(Capture).ConfigureAwait(false);
            if (string.IsNullOrEmpty(capture.Snapshot.ActiveSchemeGuid))
            {
                AppLog.Instance.Warn("无法读取当前电源方案，跳过原始状态快照");
                return;
            }

            Directory.CreateDirectory(_store.Directory);
            var json = JsonSerializer.Serialize(capture.Snapshot, SnapshotJsonOptions);
            var temporary = _snapshotPath + ".tmp";
            await File.WriteAllTextAsync(temporary, json).ConfigureAwait(false);
            File.Move(temporary, _snapshotPath, overwrite: true);
            AppLog.Instance.Info($"已保存原始电源状态快照：{_snapshotPath}");
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("保存电源状态快照失败", ex);
        }
    }

    private PowerSnapshot? LoadSnapshot()
    {
        try
        {
            if (!File.Exists(_snapshotPath)) return null;
            var json = File.ReadAllText(_snapshotPath);
            return JsonSerializer.Deserialize<PowerSnapshot>(json);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("读取电源状态快照失败", ex);
            return null;
        }
    }

    private void TryDeleteSnapshot()
    {
        try
        {
            if (File.Exists(_snapshotPath)) File.Delete(_snapshotPath);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("删除电源状态快照失败：" + ex.Message);
        }
    }

    private async Task RestoreProcessorSettingsAsync(PowerSnapshot snapshot, List<string> errors)
    {
        var settings = await QueryProcessorSettingsAsync().ConfigureAwait(false);
        var maxGuid = settings.Resolve("PROCTHROTTLEMAX", PowerGuids.MaxProcessorState);
        var minGuid = settings.Resolve("PROCTHROTTLEMIN", PowerGuids.MinProcessorState);
        var boostGuid = settings.Resolve("PERFBOOSTMODE", PowerGuids.BoostMode);

        await WriteSettingAsync(maxGuid, "最大处理器状态", snapshot.MaxProcessorStateAc, snapshot.MaxProcessorStateDc, errors).ConfigureAwait(false);
        await WriteSettingAsync(minGuid, "最小处理器状态", snapshot.MinProcessorStateAc, snapshot.MinProcessorStateDc, errors).ConfigureAwait(false);

        if (snapshot.BoostModeAc.HasValue || snapshot.BoostModeDc.HasValue)
        {
            await WriteSettingAsync(boostGuid, "处理器性能提升模式", snapshot.BoostModeAc, snapshot.BoostModeDc, errors).ConfigureAwait(false);
            return;
        }

        // 快照里没有睿频策略的显式值，说明原本是系统默认：删掉每方案覆盖值才算真正还原
        if (!Guid.TryParse(snapshot.ActiveSchemeGuid, out var scheme)) return;
        if (PowerRegistry.TryClearSetting(scheme, PowerGuids.SubProcessor, boostGuid, out var message))
        {
            AppLog.Instance.Info("已清除睿频策略的每方案覆盖值，恢复系统默认");
            return;
        }

        // 删除覆盖值需要管理员权限（普通运行时被 ACL 拒绝）。退一步：用 powercfg 把该方案的
        // 官方默认值写回去——行为和「系统默认」一致，不会让睿频停留在被我们改过的档位。
        var defaultAc = PowerRegistry.ReadDefaultSetting(scheme, PowerGuids.SubProcessor, boostGuid, useAc: true);
        var defaultDc = PowerRegistry.ReadDefaultSetting(scheme, PowerGuids.SubProcessor, boostGuid, useAc: false);
        if (defaultAc.HasValue || defaultDc.HasValue)
        {
            var errorCountBefore = errors.Count;
            await WriteSettingAsync(boostGuid, "处理器性能提升模式", defaultAc, defaultDc, errors).ConfigureAwait(false);
            if (errors.Count == errorCountBefore)
            {
                AppLog.Instance.Info($"无法删除睿频策略覆盖值（{message}），已写回方案默认值 AC={defaultAc} / DC={defaultDc}");
                return;
            }
        }

        errors.Add("恢复睿频策略默认值失败：" + message);
    }

    private async Task RestoreOverlayAsync(PowerSnapshot snapshot)
    {
        Guid? recorded = null;
        if (Guid.TryParse(snapshot.OverlayGuidAc, out var acGuid) && acGuid != Guid.Empty) recorded = acGuid;
        else if (Guid.TryParse(snapshot.OverlayGuidDc, out var dcGuid) && dcGuid != Guid.Empty) recorded = dcGuid;

        if (!recorded.HasValue) return;
        var applied = await TrySetOverlayAsync(recorded.Value, null, "恢复 Windows 电源模式覆盖").ConfigureAwait(false);
        if (!applied) AppLog.Instance.Warn("恢复 Windows 电源模式覆盖未生效（电源方案本身已恢复，不影响其它设置）");
    }

    // ---------- 解析与措辞 ----------

    /// <summary>解析 powercfg /getactivescheme 的输出（「电源方案 GUID: xxx  (平衡)」/「Power Scheme GUID: xxx  (Balanced)」）。</summary>
    private static void ParseActiveScheme(string output, out Guid? guid, out string name)
    {
        guid = null;
        name = string.Empty;
        if (string.IsNullOrWhiteSpace(output)) return;

        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0) return;

        var first = lines[0];
        var match = GuidRegex.Match(first);
        if (match.Success && Guid.TryParse(match.Value, out var parsed)) guid = parsed;

        var open = first.LastIndexOf('(');
        var close = first.LastIndexOf(')');
        if (open >= 0 && close > open) name = first[(open + 1)..close].Trim().TrimEnd('*').Trim();
    }

    /// <summary>解析 powercfg /q SCHEME_CURRENT SUB_PROCESSOR 的分组输出，得到每个设置的 AC/DC 值与 GUID 别名。</summary>
    private static SubgroupQuery ParseSubgroupQuery(string output)
    {
        var result = new SubgroupQuery();
        if (string.IsNullOrWhiteSpace(output)) return result;

        Guid? currentSetting = null;
        var pendingValues = new List<int>();
        var lines = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        // 兜底：万一本地化关键字没命中，按 powercfg 每个设置块内的固定顺序取最后两个十六进制值当作 AC、DC
        void Flush()
        {
            if (currentSetting.HasValue && pendingValues.Count >= 2 && !result.Values.ContainsKey(currentSetting.Value))
                result.Values[currentSetting.Value] = (pendingValues[^2], pendingValues[^1]);
            pendingValues.Clear();
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;

            if (line.Contains("别名", StringComparison.Ordinal) || line.Contains("Alias", StringComparison.OrdinalIgnoreCase))
            {
                if (currentSetting.HasValue)
                {
                    var alias = AfterSeparator(line);
                    if (alias.Length > 0) result.Aliases[alias] = currentSetting.Value;
                }
                continue;
            }

            var guidMatch = GuidRegex.Match(line);
            if (guidMatch.Success && Guid.TryParse(guidMatch.Value, out var parsedGuid))
            {
                var isSubgroup = line.Contains("子组", StringComparison.Ordinal) || line.Contains("Subgroup", StringComparison.OrdinalIgnoreCase);
                var isScheme = line.Contains("电源方案", StringComparison.Ordinal) || line.Contains("Power Scheme", StringComparison.OrdinalIgnoreCase);

                Flush();
                currentSetting = isSubgroup || isScheme ? null : parsedGuid;
                continue;
            }

            if (!currentSetting.HasValue) continue;
            if (!line.Contains("0x", StringComparison.OrdinalIgnoreCase)) continue;

            var hexMatch = HexValueRegex.Match(line);
            if (!hexMatch.Success) continue;
            if (!int.TryParse(hexMatch.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)) continue;

            var isAc = IsAcLine(line);
            var isDc = IsDcLine(line);
            if (!isAc && !isDc)
            {
                pendingValues.Add(value);
                continue;
            }

            var existing = result.Get(currentSetting.Value) ?? (Ac: (int?)null, Dc: (int?)null);
            result.Values[currentSetting.Value] = isAc ? (value, existing.Dc) : (existing.Ac, value);
        }

        Flush();
        return result;
    }

    private static bool IsAcLine(string line) => line.Contains("交流", StringComparison.Ordinal) || AcMarkerRegex.IsMatch(line);

    private static bool IsDcLine(string line) => line.Contains("直流", StringComparison.Ordinal) || DcMarkerRegex.IsMatch(line);

    private static string AfterSeparator(string line)
    {
        var index = line.IndexOf(':');
        var fullWidth = line.IndexOf('：');
        if (fullWidth >= 0 && (index < 0 || fullWidth < index)) index = fullWidth;
        if (index < 0 || index + 1 >= line.Length) return string.Empty;
        return line[(index + 1)..].Trim();
    }

    private static PerformanceMode InferMode(Guid? scheme, int? maxProcessorState, Guid? overlay)
    {
        if (scheme == PowerGuids.SchemeEco) return PerformanceMode.Eco;
        if (scheme == PowerGuids.SchemeHighPerformance || scheme == PowerGuids.SchemeUltimatePerformance) return PerformanceMode.Performance;
        if (overlay == PowerGuids.OverlayBestPerformance) return PerformanceMode.Performance;
        // 注意：电池下的「最佳能效」是 Windows 自己的默认覆盖，不代表用户选了节能模式
        if (maxProcessorState is <= 60) return PerformanceMode.Eco;
        return PerformanceMode.Balanced;
    }

    private static string DescribeOverlay(Guid? overlay)
    {
        if (!overlay.HasValue) return "未知";
        if (overlay.Value == Guid.Empty) return "平衡";
        if (overlay.Value == PowerGuids.OverlayBestPerformance) return "最佳性能";
        if (overlay.Value == PowerGuids.OverlayBestEfficiency) return "最佳能效";
        return overlay.Value.ToString("D");
    }

    private static string DescribeBoost(int boost) => boost switch
    {
        0 => "已禁用",
        1 => "已启用",
        2 => "高效",
        3 => "高效激进",
        _ => "系统默认"
    };

    private static string BuildSummary(PerformanceState state)
    {
        var parts = new List<string>();
        parts.Add(string.IsNullOrWhiteSpace(state.PowerSchemeName)
            ? "当前电源方案未知"
            : $"电源方案「{state.PowerSchemeName}」");
        if (state.MaxProcessorState.HasValue) parts.Add($"最大处理器状态 {state.MaxProcessorState}%");
        if (state.MinProcessorState.HasValue) parts.Add($"最小 {state.MinProcessorState}%");
        parts.Add("睿频" + (state.BoostMode.HasValue ? DescribeBoost(state.BoostMode.Value) : "系统默认"));
        if (!string.IsNullOrWhiteSpace(state.WindowsPowerMode)) parts.Add("Windows 电源模式：" + state.WindowsPowerMode);
        return string.Join(" · ", parts);
    }

    private static string Describe(PowerCfgResult result)
    {
        var detail = result.Detail();
        var needsAdmin = result.ExitCode is 5 or -1
                         || detail.Contains("拒绝访问", StringComparison.Ordinal)
                         || detail.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
                         || detail.Contains("administrator", StringComparison.OrdinalIgnoreCase);
        var hint = needsAdmin ? "（需要管理员权限，可用设置页的「以管理员身份重启」后再试）" : string.Empty;
        return $"powercfg 退出码 {result.ExitCode}：{detail}{hint}";
    }

    /// <summary>一个子组查询的结果：设置 GUID → (AC, DC)，以及别名 → 设置 GUID。</summary>
    private sealed class SubgroupQuery
    {
        public static readonly SubgroupQuery Empty = new();

        public Dictionary<Guid, (int? Ac, int? Dc)> Values { get; } = new();

        public Dictionary<string, Guid> Aliases { get; } = new(StringComparer.OrdinalIgnoreCase);

        public (int? Ac, int? Dc)? Get(Guid setting) => Values.TryGetValue(setting, out var values) ? values : null;

        public Guid Resolve(string alias, Guid fallback) => Aliases.TryGetValue(alias, out var guid) ? guid : fallback;
    }
}
