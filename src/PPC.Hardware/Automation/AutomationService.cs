using System.Diagnostics;
using Ppc.Core.Abstractions;
using Ppc.Core.Models;
using Ppc.Core.Services;

namespace Ppc.Hardware.Automation;

/// <summary>
/// 自动化规则引擎：每秒按优先级评估规则，命中后真实切换性能模式/风扇模式；
/// 另外提供 CPU 温度保护（超过阈值强制降档 + 风扇全速，回落 5 度后恢复）。
/// 任何一次评估都不允许抛出异常，失败一律记日志。
/// </summary>
public sealed class AutomationService : IDisposable
{
    private static readonly TimeSpan ProcessCacheLifetime = TimeSpan.FromSeconds(2);

    private readonly AppSettings _settings;
    private readonly SettingsStore _store;
    private readonly IPerformanceService _performance;
    private readonly IFanControlService _fan;
    private readonly IMonitorService _monitor;

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, RuleRuntimeState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _processCache = new(StringComparer.OrdinalIgnoreCase);

    private DateTimeOffset _lastEvaluatedSnapshot = DateTimeOffset.MinValue;
    private DateTimeOffset _processCacheAt = DateTimeOffset.MinValue;
    private bool _processCacheValid;
    private bool _started;
    private bool _disposed;
    private bool _enabled;

    private bool _thermalGuardActive;
    private PerformanceMode _thermalGuardRestoreMode = PerformanceMode.Balanced;
    private FanControlMode? _thermalGuardRestoreFanMode;

    public AutomationService(AppSettings settings, SettingsStore store, IPerformanceService perf, IFanControlService fan, IMonitorService monitor)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _performance = perf ?? throw new ArgumentNullException(nameof(perf));
        _fan = fan ?? throw new ArgumentNullException(nameof(fan));
        _monitor = monitor ?? throw new ArgumentNullException(nameof(monitor));
        _enabled = _settings.Automation.Enabled;
    }

    /// <summary>状态变化的中文提示（规则触发、规则解除、温度保护介入）。</summary>
    public event EventHandler<string>? StatusChanged;

    /// <summary>自动化总开关：关闭后 EvaluateAsync 直接返回，不做任何硬件写入。</summary>
    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            AppLog.Instance.Info("自动化服务" + (value ? "已启用" : "已暂停"));
            Raise(value ? "自动化已启用" : "自动化已暂停");
        }
    }

    /// <summary>直接引用 settings 里的同一个列表，避免复制导致 UI 修改不同步。</summary>
    public IReadOnlyList<AutomationRule> Rules => _settings.Automation.Rules;

    public void Start()
    {
        if (_disposed || _started) return;
        _started = true;
        _enabled = _settings.Automation.Enabled;

        try
        {
            // 主链路由 AppServices 每帧调用 EvaluateAsync；这里额外订阅一次作为兜底，
            // 同一份快照（Timestamp 相同）只会被评估一次，不会重复触发。
            _monitor.SnapshotReady += OnMonitorSnapshot;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("订阅监控快照事件失败", ex);
        }

        var automation = _settings.Automation;
        AppLog.Instance.Info($"自动化服务已启动：启用={_enabled}，规则 {Rules.Count} 条，温度保护={automation.ThermalGuardEnabled}（阈值 {automation.ThermalGuardCelsius}°C）");
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        try
        {
            _monitor.SnapshotReady -= OnMonitorSnapshot;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("取消订阅监控快照事件失败", ex);
        }
        AppLog.Instance.Info("自动化服务已停止");
    }

    /// <summary>被每秒调用一次：评估温度保护与所有启用的规则。绝不抛异常。</summary>
    public async Task EvaluateAsync(HardwareSnapshot snapshot)
    {
        if (_disposed || !_enabled || snapshot is null) return;

        // 上一轮还没跑完就跳过这一轮，避免规则状态机被并发打乱
        if (!await _gate.WaitAsync(0).ConfigureAwait(false)) return;

        try
        {
            if (snapshot.Timestamp == _lastEvaluatedSnapshot) return;
            _lastEvaluatedSnapshot = snapshot.Timestamp;

            await EvaluateThermalGuardAsync(snapshot).ConfigureAwait(false);

            if (_thermalGuardActive)
            {
                AppLog.Instance.Info("温度保护生效中，本轮跳过自动化规则");
                return;
            }

            await EvaluateRulesAsync(snapshot).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("自动化规则评估失败", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        try
        {
            _gate.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("释放自动化服务信号量失败：" + ex.Message);
        }
    }

    // ---------- 温度保护 ----------

    private async Task EvaluateThermalGuardAsync(HardwareSnapshot snapshot)
    {
        var automation = _settings.Automation;

        if (!automation.ThermalGuardEnabled)
        {
            if (_thermalGuardActive) await ReleaseThermalGuardAsync("温度保护已关闭，恢复之前的性能模式").ConfigureAwait(false);
            return;
        }

        var limit = Math.Clamp(automation.ThermalGuardCelsius, 60, 110);
        var temperature = snapshot.Get(Metric.CpuTemperature);

        if (!_thermalGuardActive)
        {
            if (temperature is double current && current >= limit) await EngageThermalGuardAsync(current, limit).ConfigureAwait(false);
            return;
        }

        // 读不到温度时保持保护状态，宁可保守
        if (temperature is not double now) return;
        if (now <= limit - 5) await ReleaseThermalGuardAsync($"CPU 温度已回落到 {now:0}°C，恢复之前的性能模式").ConfigureAwait(false);
    }

    private async Task EngageThermalGuardAsync(double temperature, int limit)
    {
        _thermalGuardRestoreMode = _settings.CurrentMode;
        _thermalGuardRestoreFanMode = _fan.SupportsDirectControl ? _settings.Fan.Mode : null;
        _thermalGuardActive = true;

        var applied = new List<string>();

        var modeResult = await _performance.ApplyModeAsync(PerformanceMode.Balanced, _settings.CustomProfile).ConfigureAwait(false);
        if (modeResult.Success)
        {
            _settings.CurrentMode = PerformanceMode.Balanced;
            applied.Add("把性能模式降到「平衡」");
        }
        else
        {
            AppLog.Instance.Warn("温度保护降档失败：" + modeResult.Message);
        }

        if (_fan.SupportsDirectControl)
        {
            var fanResult = await _fan.SetModeAsync(FanControlMode.Performance, null, 100).ConfigureAwait(false);
            if (fanResult.Success) applied.Add("把风扇转速提到最高");
            else AppLog.Instance.Warn("温度保护提高风扇转速失败：" + fanResult.Message);
        }
        else
        {
            AppLog.Instance.Info("温度保护：本机风扇不支持直接控制，只能靠电源方案降档降温");
        }

        PersistSettings();
        var detail = applied.Count > 0 ? "已" + string.Join("、", applied) : "未能实际调整（原因见日志）";
        Raise($"CPU 温度 {temperature:0}°C 达到保护阈值 {limit}°C，{detail}");
    }

    private async Task ReleaseThermalGuardAsync(string reason)
    {
        if (!_thermalGuardActive) return;
        _thermalGuardActive = false;

        var restoreMode = _thermalGuardRestoreMode;
        var modeResult = await _performance.ApplyModeAsync(restoreMode, _settings.CustomProfile).ConfigureAwait(false);
        if (modeResult.Success)
        {
            _settings.CurrentMode = restoreMode;
            AppLog.Instance.Info("温度保护解除，性能模式恢复为 " + ModeName(restoreMode));
        }
        else
        {
            AppLog.Instance.Warn("温度保护解除后恢复性能模式失败：" + modeResult.Message);
        }

        if (_thermalGuardRestoreFanMode is { } fanMode)
        {
            var fanResult = await _fan.SetModeAsync(fanMode, _settings.Fan.ActiveCurve, _settings.Fan.ManualPercent).ConfigureAwait(false);
            if (!fanResult.Success) AppLog.Instance.Warn("温度保护解除后恢复风扇模式失败：" + fanResult.Message);
        }

        PersistSettings();
        Raise(reason);
    }

    // ---------- 规则评估 ----------

    private async Task EvaluateRulesAsync(HardwareSnapshot snapshot)
    {
        List<AutomationRule> rules;
        try
        {
            rules = Rules.Where(r => r.Enabled).OrderByDescending(r => r.Priority).ToList();
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("读取自动化规则失败", ex);
            return;
        }

        foreach (var rule in rules)
        {
            bool hit;
            string description;
            try
            {
                (hit, description) = EvaluateCondition(rule, snapshot);
            }
            catch (Exception ex)
            {
                AppLog.Instance.Error($"评估规则「{rule.Name}」失败", ex);
                continue;
            }

            var state = GetState(rule);

            if (hit)
            {
                if (state.Active) continue;   // 已经处于触发状态，不重复应用
                state.Active = true;

                var now = DateTimeOffset.Now;
                var cooldown = TimeSpan.FromSeconds(Math.Clamp(rule.CooldownSeconds, 0, 3600));
                if (state.LastAppliedAt != default && now - state.LastAppliedAt < cooldown)
                {
                    AppLog.Instance.Info($"规则「{rule.Name}」命中，但仍在冷却期（{cooldown.TotalSeconds:0} 秒）内，本次不重复应用");
                    continue;
                }

                state.LastAppliedAt = now;
                await ApplyRuleAsync(rule, description).ConfigureAwait(false);
            }
            else
            {
                if (!state.Active) continue;
                state.Active = false;
                if (rule.RestorePrevious) await RestoreRuleAsync(rule, state).ConfigureAwait(false);
            }
        }
    }

    private (bool Hit, string Description) EvaluateCondition(AutomationRule rule, HardwareSnapshot snapshot)
    {
        switch (rule.Trigger)
        {
            case RuleTrigger.AcConnected:
            {
                var ac = snapshot.Get(Metric.AcOnline);
                if (ac is null) return (false, "电源适配器状态未知");
                var online = ac.Value >= 0.5;
                return (online, online ? "已接入电源适配器" : "未接入电源适配器");
            }

            case RuleTrigger.AcDisconnected:
            {
                var ac = snapshot.Get(Metric.AcOnline);
                if (ac is null) return (false, "电源适配器状态未知");
                var offline = ac.Value < 0.5;
                return (offline, offline ? "已拔掉电源适配器" : "仍在接入电源");
            }

            case RuleTrigger.BatteryBelow:
            {
                var battery = snapshot.Get(Metric.BatteryPercent);
                if (battery is null) return (false, "电池电量未知");
                return (battery.Value < rule.Threshold, $"电池电量 {battery.Value:0}% 低于阈值 {rule.Threshold:0}%");
            }

            case RuleTrigger.ProcessRunning:
            {
                var name = NormalizeProcessName(rule.ProcessName);
                if (name.Length == 0) return (false, "规则未填写进程名");
                var running = EnumerateProcessNames().Contains(name);
                return (running, running ? $"进程 {name} 正在运行" : $"进程 {name} 未运行");
            }

            case RuleTrigger.TemperatureAbove:
            {
                var temperature = snapshot.Get(Metric.CpuTemperature);
                if (temperature is null) return (false, "CPU 温度未知");
                return (temperature.Value > rule.Threshold, $"CPU 温度 {temperature.Value:0}°C 高于阈值 {rule.Threshold:0}°C");
            }

            case RuleTrigger.TemperatureBelow:
            {
                var temperature = snapshot.Get(Metric.CpuTemperature);
                if (temperature is null) return (false, "CPU 温度未知");
                return (temperature.Value < rule.Threshold, $"CPU 温度 {temperature.Value:0}°C 低于阈值 {rule.Threshold:0}°C");
            }

            default:
                return (false, "未知的触发条件");
        }
    }

    private async Task ApplyRuleAsync(AutomationRule rule, string description)
    {
        var state = GetState(rule);
        if (rule.RestorePrevious)
        {
            state.PreviousMode = _settings.CurrentMode;
            state.PreviousFanMode = _settings.Fan.Mode;
        }

        var applied = new List<string>();

        if (_settings.CurrentMode != rule.TargetMode)
        {
            var result = await _performance.ApplyModeAsync(rule.TargetMode, _settings.CustomProfile).ConfigureAwait(false);
            if (result.Success)
            {
                _settings.CurrentMode = rule.TargetMode;
                applied.Add("性能模式切到「" + ModeName(rule.TargetMode) + "」");
            }
            else
            {
                AppLog.Instance.Warn($"规则「{rule.Name}」切换性能模式失败：{result.Message}");
            }
        }
        else
        {
            applied.Add("性能模式保持「" + ModeName(rule.TargetMode) + "」");
        }

        if (rule.TargetFanMode is { } fanMode)
        {
            var fanResult = await _fan.SetModeAsync(fanMode, _settings.Fan.ActiveCurve, _settings.Fan.ManualPercent).ConfigureAwait(false);
            if (fanResult.Success)
            {
                _settings.Fan.Mode = fanMode;
                applied.Add("风扇模式设为「" + FanModeName(fanMode) + "」");
            }
            else
            {
                AppLog.Instance.Warn($"规则「{rule.Name}」设置风扇模式失败：{fanResult.Message}");
            }
        }

        PersistSettings();
        Raise($"规则「{rule.Name}」已触发：{description}；{string.Join("，", applied)}");
    }

    private async Task RestoreRuleAsync(AutomationRule rule, RuleRuntimeState state)
    {
        var messages = new List<string>();

        if (state.PreviousMode is { } previousMode && previousMode != _settings.CurrentMode)
        {
            var result = await _performance.ApplyModeAsync(previousMode, _settings.CustomProfile).ConfigureAwait(false);
            if (result.Success)
            {
                _settings.CurrentMode = previousMode;
                messages.Add("性能模式恢复「" + ModeName(previousMode) + "」");
            }
            else
            {
                AppLog.Instance.Warn($"规则「{rule.Name}」条件解除后恢复性能模式失败：{result.Message}");
            }
        }

        if (state.PreviousFanMode is { } previousFan && previousFan != _settings.Fan.Mode)
        {
            var fanResult = await _fan.SetModeAsync(previousFan, _settings.Fan.ActiveCurve, _settings.Fan.ManualPercent).ConfigureAwait(false);
            if (fanResult.Success)
            {
                _settings.Fan.Mode = previousFan;
                messages.Add("风扇模式恢复「" + FanModeName(previousFan) + "」");
            }
            else
            {
                AppLog.Instance.Warn($"规则「{rule.Name}」条件解除后恢复风扇模式失败：{fanResult.Message}");
            }
        }

        state.PreviousMode = null;
        state.PreviousFanMode = null;
        PersistSettings();

        if (messages.Count > 0) Raise($"规则「{rule.Name}」条件已解除，已{string.Join("、", messages)}");
    }

    // ---------- 辅助 ----------

    private void OnMonitorSnapshot(object? sender, HardwareSnapshot snapshot)
    {
        _ = EvaluateAsync(snapshot);
    }

    private RuleRuntimeState GetState(AutomationRule rule)
    {
        var key = string.IsNullOrWhiteSpace(rule.Id) ? rule.Name : rule.Id;
        if (!_states.TryGetValue(key, out var state))
        {
            state = new RuleRuntimeState();
            _states[key] = state;
        }
        return state;
    }

    private HashSet<string> EnumerateProcessNames()
    {
        var now = DateTimeOffset.Now;
        if (_processCacheValid && now - _processCacheAt < ProcessCacheLifetime) return _processCache;

        _processCache.Clear();
        var skipped = 0;
        try
        {
            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    _processCache.Add(process.ProcessName);
                }
                catch (Exception)
                {
                    // 受保护进程读不到名字，直接跳过（不逐条刷日志）
                    skipped++;
                }
                finally
                {
                    process.Dispose();
                }
            }

            _processCacheValid = true;
            _processCacheAt = now;
            if (skipped > 0) AppLog.Instance.Info($"枚举进程时跳过 {skipped} 个受保护进程");
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("枚举进程列表失败", ex);
        }

        return _processCache;
    }

    private static string NormalizeProcessName(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var name = raw.Trim().Trim('"');
        try
        {
            name = Path.GetFileName(name);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Warn("解析进程名失败：" + ex.Message);
        }

        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.Trim();
    }

    private void PersistSettings()
    {
        try
        {
            _store.Save(_settings);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("保存自动化设置失败", ex);
        }
    }

    private void Raise(string message)
    {
        try
        {
            AppLog.Instance.Info("[自动化] " + message);
            StatusChanged?.Invoke(this, message);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("自动化状态事件回调失败", ex);
        }
    }

    private static string ModeName(PerformanceMode mode) => mode switch
    {
        PerformanceMode.Eco => "节能",
        PerformanceMode.Balanced => "平衡",
        PerformanceMode.Performance => "性能",
        PerformanceMode.Custom => "自定义",
        _ => mode.ToString()
    };

    private static string FanModeName(FanControlMode mode) => mode switch
    {
        FanControlMode.Auto => "自动",
        FanControlMode.Silent => "静音",
        FanControlMode.Performance => "性能",
        FanControlMode.Manual => "手动",
        FanControlMode.Curve => "曲线",
        _ => mode.ToString()
    };

    /// <summary>单条规则的运行时状态（不落盘：重启后从默认状态重新判断）。</summary>
    private sealed class RuleRuntimeState
    {
        public bool Active { get; set; }

        public DateTimeOffset LastAppliedAt { get; set; }

        public PerformanceMode? PreviousMode { get; set; }

        public FanControlMode? PreviousFanMode { get; set; }
    }
}
