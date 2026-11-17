using Ppc.Core.Abstractions;
using Ppc.Core.Models;
using Ppc.Core.Services;

namespace Ppc.Hardware.Lenovo;

/// <summary>
/// 联想风扇控制服务。
///
/// 设计原则（对应本机实测事实）：
/// 1. 构造后立刻在后台做只读探测，绝不阻塞 UI 线程；
/// 2. 探测不过就永久只读：所有写操作返回 Unsupported，绝不用假数据充数；
/// 3. 任何适配器异常都捕获、记日志，并降级为只读；
/// 4. 风扇百分比一律夹在 FanCurve.MinSafePercent..FanCurve.MaxPercent，绝不下发 0%。
///
/// 实测机型：联想 83AQ（XiaoXinPro 16 IRH8）。LENOVO_GAMEZONE_DATA 与 LENOVO_FAN_METHOD
/// 都存在但调用被固件拒绝，因此本机 SupportsDirectControl 为 false。
/// </summary>
public sealed class LenovoFanControlService : IFanControlService, IDisposable
{
    private static readonly Metric[] RpmMetrics =
    {
        Metric.Fan1Rpm, Metric.Fan2Rpm, Metric.Fan3Rpm, Metric.Fan4Rpm
    };

    private readonly IMonitorService _monitor;
    private readonly LenovoGameZoneAdapter _gameZone = new();
    private readonly LenovoFanTableAdapter _fanTable = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _applyLock = new(1, 1);
    private readonly TaskCompletionSource<bool> _probeCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly object _stateLock = new();
    private volatile HardwareSnapshot? _latestSnapshot;

    private FanControlMode _mode = FanControlMode.Auto;
    private string _controlSource = "厂商自动控制";
    private int? _appliedPercent;
    private FanCurve? _activeCurve;
    private LenovoSmartFanMode? _appliedVendorMode;
    private string? _degradedReason;
    private string? _lastNotifiedSignature;

    // 曲线应用状态：回滞比较与延迟任务去重
    private double? _lastCurveTemperature;
    private int? _lastCurveTarget;
    private CancellationTokenSource? _pendingCurveCts;
    private int? _pendingCurveTarget;
    private bool _disposed;

    public LenovoFanControlService(IMonitorService monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        _monitor = monitor;
        _monitor.SnapshotReady += OnSnapshotReady;

        // 后台探测：不占用构造线程，也不让异常冒到调用方
        _ = Task.Run(RunProbeAsync, _lifetime.Token);
    }

    public event EventHandler<FanState>? StateChanged;

    /// <summary>
    /// 只有只读探测真的通过、且运行期没有降级时才为 true。
    /// 探测未完成时返回 false（宁可不给控制权，也不冒险写固件）。
    /// </summary>
    public bool SupportsDirectControl =>
        !_disposed && _gameZone.ProbeAttempted && _gameZone.IsUsable && _degradedReason is null;

    public string SupportDetail
    {
        get
        {
            var input = new FanCapabilityInput(
                SupportsControl: SupportsDirectControl,
                PlatformName: LenovoPlatformInfo.DisplayName,
                GameZonePresent: _gameZone.IsPresent,
                GameZoneInstance: string.IsNullOrWhiteSpace(_gameZone.InstanceName) ? "（未连接）" : _gameZone.InstanceName,
                GameZoneProbeDetail: _gameZone.ProbeAttempted ? _gameZone.ProbeDetail : "正在后台做只读探测…",
                FanTablePresent: _fanTable.IsPresent,
                FanTableProbeDetail: _fanTable.ProbeAttempted ? _fanTable.ProbeDetail : "正在后台做只读探测…",
                ChannelCount: CountMonitorChannels(),
                DegradedReason: _degradedReason);

            return FanCapabilityReporter.Describe(input);
        }
    }

    /// <summary>探测是否已经完成（供 UI 判断「检测中」状态）。</summary>
    public bool IsProbeComplete => _probeCompletion.Task.IsCompleted;

    // ---------------------------------------------------------------- 探测

    private async Task RunProbeAsync()
    {
        try
        {
            var gameZoneOk = await Task.Run(() => _gameZone.TryProbe(), _lifetime.Token).ConfigureAwait(false);
            var fanTableOk = await Task.Run(() => _fanTable.TryProbe(), _lifetime.Token).ConfigureAwait(false);

            AppLog.Instance.Info(
                $"联想风扇探测完成：GameZone={(gameZoneOk ? "可用" : "不可用")}，" +
                $"FanTable={(fanTableOk ? "可用" : "不可用")}；GameZone 细节：{_gameZone.ProbeDetail}");

            _probeCompletion.TrySetResult(gameZoneOk);
            await RaiseStateChangedAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _probeCompletion.TrySetResult(false);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("联想风扇只读探测异常，按不支持直接控制处理", ex);
            _probeCompletion.TrySetResult(false);
        }
    }

    // ---------------------------------------------------------------- 读取状态

    public Task<FanState> GetStateAsync()
    {
        var snapshot = _latestSnapshot;
        var channels = BuildChannels(snapshot);

        FanControlMode mode;
        string controlSource;
        int? appliedPercent;
        FanCurve? curve;
        string? degraded;
        lock (_stateLock)
        {
            mode = _mode;
            controlSource = _controlSource;
            appliedPercent = _appliedPercent;
            curve = _activeCurve;
            degraded = _degradedReason;
        }

        var canControl = SupportsDirectControl;
        var state = new FanState
        {
            Channels = channels,
            Mode = canControl ? mode : FanControlMode.Auto,
            ControlSource = canControl ? controlSource : "厂商自动控制（仅监控）",
            CanControl = canControl,
            ControlUnavailableReason = canControl ? null : BuildUnavailableReason(degraded),
            ActiveCurve = curve,
            AppliedPercent = canControl ? appliedPercent : null
        };

        return Task.FromResult(state);
    }

    private string BuildUnavailableReason(string? degraded)
    {
        if (!string.IsNullOrWhiteSpace(degraded))
        {
            return $"厂商接口运行中报错，已降级为只读：{degraded}";
        }

        if (!_gameZone.ProbeAttempted)
        {
            return "正在后台检测厂商风扇接口，检测完成前不开放控制。";
        }

        return FanCapabilityReporter.UnsupportedSentence(LenovoPlatformInfo.DisplayName);
    }

    private List<FanChannelState> BuildChannels(HardwareSnapshot? snapshot)
    {
        // RPM 以监控快照为准；读不到就是 null，绝不用 0 代替
        var rpm = new int?[4];
        for (var i = 0; i < RpmMetrics.Length; i++)
        {
            if (snapshot is null) break;
            var reading = snapshot[RpmMetrics[i]];
            if (reading.HasValue && reading.Value is double value && value >= 0)
            {
                rpm[i] = (int)Math.Round(value);
            }
        }

        var observed = rpm.Count(v => v.HasValue);

        // 通道数优先取厂商接口；不可用时退回监控里能读到的风扇数量
        var count = 0;
        if (_gameZone.IsUsable)
        {
            if (observed == 0)
            {
                var vendorSpeeds = _gameZone.ReadFanSpeeds(out _);
                for (var i = 0; i < vendorSpeeds.Count && i < rpm.Length; i++)
                {
                    if (rpm[i] is null && vendorSpeeds[i] > 0) rpm[i] = vendorSpeeds[i];
                }

                observed = rpm.Count(v => v.HasValue);
            }

            count = _gameZone.ReadFanCount() ?? observed;
        }

        if (count <= 0) count = observed;
        if (count <= 0) return new List<FanChannelState>();

        var maxRpm = _gameZone.IsUsable ? _gameZone.ReadFanMaxSpeed() : null;
        int? percent;
        lock (_stateLock)
        {
            percent = _appliedPercent;
        }

        var canControl = SupportsDirectControl;
        var channels = new List<FanChannelState>(count);
        for (var i = 0; i < count && i < 4; i++)
        {
            channels.Add(new FanChannelState
            {
                Index = i + 1,
                Name = $"风扇 {i + 1}",
                Rpm = rpm[i],
                MaxRpm = maxRpm,
                Percent = canControl ? percent : null
            });
        }

        return channels;
    }

    private int CountMonitorChannels()
    {
        var snapshot = _latestSnapshot;
        if (snapshot is null) return 0;

        var count = 0;
        foreach (var metric in RpmMetrics)
        {
            if (snapshot[metric].HasValue) count++;
        }

        if (count == 0)
        {
            foreach (var fan in snapshot.Fans)
            {
                if (fan.HasValue) count++;
            }
        }

        return count;
    }

    // ---------------------------------------------------------------- 控制

    public async Task<OpResult> SetManualAsync(int percent)
    {
        if (!SupportsDirectControl) return OpResult.Unsupported(BuildUnavailableReason(_degradedReason));

        var clamped = Math.Clamp(percent, FanCurve.MinSafePercent, FanCurve.MaxPercent);
        var clampedNote = clamped == percent
            ? string.Empty
            : $"请求的 {percent}% 超出安全区间 {FanCurve.MinSafePercent}%..{FanCurve.MaxPercent}%，已夹到 {clamped}%。";

        // 厂商接口只暴露离散档位：把百分比映射到最接近的档位，并在结果里说清实际生效值
        var vendorMode = LenovoGameZoneAdapter.MapPercentToMode(clamped);
        var result = await RunAdapterAsync(
            () => _gameZone.SetSmartFanMode(vendorMode),
            "设置手动风扇档位").ConfigureAwait(false);

        if (!result.Success)
        {
            return OpResult.Fail(clampedNote + result.Message);
        }

        var approximate = LenovoGameZoneAdapter.ModeToApproximatePercent(vendorMode);
        lock (_stateLock)
        {
            _mode = FanControlMode.Manual;
            _controlSource = $"厂商{LenovoGameZoneAdapter.DescribeMode(vendorMode)}档（手动）";
            _appliedPercent = approximate;
            _appliedVendorMode = vendorMode;
            _activeCurve = null;
        }

        CancelPendingCurveApply();
        await RaiseStateChangedAsync().ConfigureAwait(false);

        return OpResult.Ok(
            clampedNote +
            $"已写入厂商{LenovoGameZoneAdapter.DescribeMode(vendorMode)}档。厂商接口只支持离散档位，" +
            $"请求的 {clamped}% 实际生效为约 {approximate}%。");
    }

    public async Task<OpResult> SetModeAsync(FanControlMode mode, FanCurve? curve, int manualPercent)
    {
        if (!SupportsDirectControl && mode != FanControlMode.Auto)
        {
            return OpResult.Unsupported(BuildUnavailableReason(_degradedReason));
        }

        switch (mode)
        {
            case FanControlMode.Auto:
                return await RestoreAutoAsync().ConfigureAwait(false);

            case FanControlMode.Silent:
                return await ApplyVendorModeAsync(
                    LenovoSmartFanMode.Silent,
                    FanControlMode.Silent,
                    "厂商静音档",
                    null).ConfigureAwait(false);

            case FanControlMode.Performance:
                return await ApplyVendorModeAsync(
                    LenovoSmartFanMode.Performance,
                    FanControlMode.Performance,
                    "厂商性能档",
                    null).ConfigureAwait(false);

            case FanControlMode.Manual:
                return await SetManualAsync(manualPercent).ConfigureAwait(false);

            case FanControlMode.Curve:
                if (curve is null)
                {
                    return OpResult.Fail("已切换到曲线模式失败：没有提供风扇曲线。");
                }

                lock (_stateLock)
                {
                    _mode = FanControlMode.Curve;
                    _activeCurve = curve;
                    _controlSource = $"自定义曲线「{curve.Name}」";
                }

                var temperature = _latestSnapshot?[Metric.CpuTemperature].Value;
                if (temperature is double current && current > 0)
                {
                    var applied = await ApplyCurveAsync(curve, current).ConfigureAwait(false);
                    if (!applied.Success)
                    {
                        // 曲线已记录，但硬件未写入：如实告诉调用方
                        return OpResult.Fail("曲线已记录，但立即应用失败：" + applied.Message);
                    }

                    return OpResult.Ok("已切换到曲线模式：" + applied.Message);
                }

                await RaiseStateChangedAsync().ConfigureAwait(false);
                return OpResult.Ok("已切换到曲线模式并记录曲线；当前没有可用的温度读数，等下一次监控快照到达后再按曲线应用。");

            default:
                return OpResult.Unsupported($"未定义的散热模式：{mode}。");
        }
    }

    public async Task<OpResult> ApplyCurveAsync(FanCurve curve, double currentTemperature)
    {
        ArgumentNullException.ThrowIfNull(curve);

        // 注意：PPC.Core 的 FanCurve 构造器存在递归缺陷（属性初始值调用 Default()），
        // 调用 Clone()/Normalized() 会直接栈溢出，因此这里沿用调用方传入的实例，不做复制。
        lock (_stateLock)
        {
            _activeCurve = curve;
            if (_mode != FanControlMode.Curve)
            {
                _mode = FanControlMode.Curve;
                _controlSource = $"自定义曲线「{curve.Name}」";
            }
        }

        if (double.IsNaN(currentTemperature) || double.IsInfinity(currentTemperature) || currentTemperature <= 0)
        {
            return OpResult.Fail("未应用曲线：当前温度是无效值，无法计算目标转速。");
        }

        var target = curve.Evaluate(currentTemperature);
        target = Math.Clamp(target, FanCurve.MinSafePercent, FanCurve.MaxPercent);

        if (!SupportsDirectControl)
        {
            await RaiseStateChangedAsync().ConfigureAwait(false);
            return OpResult.Unsupported(BuildUnavailableReason(_degradedReason));
        }

        // 温度回滞：温度变化小于回滞就不重新下发，避免风扇反复升降
        double? lastTemperature;
        int? lastTarget;
        lock (_stateLock)
        {
            lastTemperature = _lastCurveTemperature;
            lastTarget = _lastCurveTarget;
        }

        var hysteresis = Math.Clamp(curve.HysteresisCelsius, 0, 15);
        if (lastTemperature is double previousTemperature &&
            Math.Abs(currentTemperature - previousTemperature) < hysteresis)
        {
            return OpResult.Ok(
                $"温度 {currentTemperature:0.#}℃ 相对上次 {previousTemperature:0.#}℃ 的变化小于回滞 " +
                $"{hysteresis}℃，保持当前转速未改动。");
        }

        if (lastTarget == target)
        {
            lock (_stateLock)
            {
                _lastCurveTemperature = currentTemperature;
            }

            return OpResult.Ok($"温度 {currentTemperature:0.#}℃ 对应目标仍是 {target}%，与上次一致，未重复下发。");
        }

        // 延迟执行：同一目标已在排队就不重复排；目标变化则取消上一次延迟任务
        var delaySeconds = Math.Clamp(curve.ResponseDelaySeconds, 1, 60);
        CancellationTokenSource cts;
        lock (_stateLock)
        {
            if (_pendingCurveCts is not null && _pendingCurveTarget == target)
            {
                return OpResult.Ok($"目标 {target}% 已在延迟队列中（{delaySeconds} 秒后执行），本次不重复下发。");
            }

            _pendingCurveCts?.Cancel();
            _pendingCurveCts?.Dispose();
            cts = new CancellationTokenSource();
            _pendingCurveCts = cts;
            _pendingCurveTarget = target;
        }

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return OpResult.Ok("延迟应用已被更新的风扇请求取代，本次未下发。");
        }
        finally
        {
            lock (_stateLock)
            {
                if (ReferenceEquals(_pendingCurveCts, cts))
                {
                    _pendingCurveCts = null;
                    _pendingCurveTarget = null;
                }
            }

            cts.Dispose();
        }

        if (!SupportsDirectControl)
        {
            return OpResult.Unsupported(BuildUnavailableReason(_degradedReason));
        }

        var vendorMode = LenovoGameZoneAdapter.MapPercentToMode(target);
        var result = await RunAdapterAsync(
            () => _gameZone.SetSmartFanMode(vendorMode),
            "按曲线设置风扇档位").ConfigureAwait(false);

        if (!result.Success)
        {
            return OpResult.Fail(result.Message);
        }

        var approximate = LenovoGameZoneAdapter.ModeToApproximatePercent(vendorMode);
        lock (_stateLock)
        {
            _lastCurveTemperature = currentTemperature;
            _lastCurveTarget = target;
            _appliedPercent = approximate;
            _appliedVendorMode = vendorMode;
            _mode = FanControlMode.Curve;
            _controlSource = $"自定义曲线「{curve.Name}」";
        }

        await RaiseStateChangedAsync().ConfigureAwait(false);

        return OpResult.Ok(
            $"温度 {currentTemperature:0.#}℃ 按曲线目标 {target}%，已写入厂商" +
            $"{LenovoGameZoneAdapter.DescribeMode(vendorMode)}档（约 {approximate}%）。");
    }

    public async Task<OpResult> RestoreAutoAsync()
    {
        CancelPendingCurveApply();

        if (!SupportsDirectControl)
        {
            lock (_stateLock)
            {
                _mode = FanControlMode.Auto;
                _controlSource = "厂商自动控制";
                _appliedPercent = null;
                _appliedVendorMode = null;
                _activeCurve = null;
                _lastCurveTarget = null;
                _lastCurveTemperature = null;
            }

            await RaiseStateChangedAsync().ConfigureAwait(false);
            return OpResult.Ok("当前机型无需恢复");
        }

        var cooling = await RunAdapterAsync(
            () => _gameZone.SetFanCooling(false),
            "关闭强制散热").ConfigureAwait(false);
        var autoMode = await RunAdapterAsync(
            () => _gameZone.SetSmartFanMode(LenovoSmartFanMode.Auto),
            "恢复厂商自动档位").ConfigureAwait(false);

        if (!cooling.Success || !autoMode.Success)
        {
            var details = string.Join("；", new[]
            {
                cooling.Success ? "强制散热已关闭" : "关闭强制散热失败：" + cooling.Message,
                autoMode.Success ? "自动档位已恢复" : "恢复自动档位失败：" + autoMode.Message
            });
            AppLog.Instance.Warn("联想风扇恢复自动控制未完全成功：" + details);
            return OpResult.Fail(details);
        }

        lock (_stateLock)
        {
            _mode = FanControlMode.Auto;
            _controlSource = "厂商自动控制";
            _appliedPercent = null;
            _appliedVendorMode = null;
            _activeCurve = null;
            _lastCurveTarget = null;
            _lastCurveTemperature = null;
        }

        await RaiseStateChangedAsync().ConfigureAwait(false);
        return OpResult.Ok("已恢复厂商自动风扇控制（关闭强制散热，档位设为自动）。");
    }

    // ---------------------------------------------------------------- 内部实现

    private async Task<OpResult> ApplyVendorModeAsync(
        LenovoSmartFanMode vendorMode,
        FanControlMode uiMode,
        string controlSource,
        FanCurve? curve)
    {
        var result = await RunAdapterAsync(
            () => _gameZone.SetSmartFanMode(vendorMode),
            $"设置{controlSource}").ConfigureAwait(false);

        if (!result.Success)
        {
            return OpResult.Fail(result.Message);
        }

        var approximate = LenovoGameZoneAdapter.ModeToApproximatePercent(vendorMode);
        lock (_stateLock)
        {
            _mode = uiMode;
            _controlSource = controlSource;
            _appliedVendorMode = vendorMode;
            _appliedPercent = approximate;
            _activeCurve = curve;
        }

        CancelPendingCurveApply();
        await RaiseStateChangedAsync().ConfigureAwait(false);

        return OpResult.Ok($"{result.Message}风扇进入{controlSource}（约 {approximate}%）。");
    }

    private async Task<OpResult> RunAdapterAsync(Func<OpResult> action, string context)
    {
        try
        {
            return await Task.Run(action, _lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return OpResult.Fail($"{context} 未执行：风扇服务正在关闭。");
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error($"{context} 时适配器异常，已降级为只读", ex);
            DegradeToReadOnly($"{context} 异常：{ex.Message}");
            return OpResult.Fail($"{context} 时适配器异常，已降级为只读：{ex.Message}");
        }
    }

    /// <summary>写操作失败/异常后降级为只读，之后的写请求一律 Unsupported。</summary>
    private void DegradeToReadOnly(string reason)
    {
        lock (_stateLock)
        {
            if (_degradedReason is not null) return;
            _degradedReason = reason;
            _mode = FanControlMode.Auto;
            _controlSource = "厂商自动控制（已降级只读）";
            _appliedPercent = null;
            _appliedVendorMode = null;
        }

        AppLog.Instance.Warn("联想风扇控制已降级为只读：" + reason);
    }

    private void CancelPendingCurveApply()
    {
        lock (_stateLock)
        {
            try
            {
                _pendingCurveCts?.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // 已经释放，忽略
            }

            _pendingCurveCts?.Dispose();
            _pendingCurveCts = null;
            _pendingCurveTarget = null;
        }
    }

    private void OnSnapshotReady(object? sender, HardwareSnapshot snapshot)
    {
        _latestSnapshot = snapshot;

        try
        {
            FanControlMode mode;
            FanCurve? curve;
            lock (_stateLock)
            {
                mode = _mode;
                curve = _activeCurve;
            }

            if (SupportsDirectControl && mode == FanControlMode.Curve && curve is not null)
            {
                var temperature = snapshot[Metric.CpuTemperature].Value;
                if (temperature is double current && current > 0)
                {
                    // 曲线应用内部有回滞与延迟去重，不会每秒狂写风扇
                    FireAndForget(() => ApplyCurveAsync(curve, current), "按监控快照应用风扇曲线");
                }
            }

            FireAndForget(RaiseStateChangedAsync, "推送风扇状态变化");
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("处理监控快照时发生异常（风扇控制继续以只读方式运行）", ex);
        }
    }

    private void FireAndForget(Func<Task> action, string context)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // 服务正在关闭
            }
            catch (Exception ex)
            {
                AppLog.Instance.Error(context + " 失败", ex);
            }
        }, _lifetime.Token);
    }

    private async Task RaiseStateChangedAsync()
    {
        if (_disposed) return;

        var state = await GetStateAsync().ConfigureAwait(false);
        var signature = BuildSignature(state);

        lock (_stateLock)
        {
            if (string.Equals(_lastNotifiedSignature, signature, StringComparison.Ordinal)) return;
            _lastNotifiedSignature = signature;
        }

        try
        {
            StateChanged?.Invoke(this, state);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("风扇 StateChanged 订阅者抛出异常", ex);
        }
    }

    private static string BuildSignature(FanState state)
    {
        var channels = string.Join(",", state.Channels.Select(c => $"{c.Index}:{c.Rpm?.ToString() ?? "-"}"));
        return string.Join("|",
            state.Mode.ToString(),
            state.CanControl ? "1" : "0",
            state.AppliedPercent?.ToString() ?? "-",
            state.ControlSource,
            channels);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _monitor.SnapshotReady -= OnSnapshotReady; }
        catch (Exception ex) { AppLog.Instance.Warn("取消订阅监控快照失败：" + ex.Message); }

        CancelPendingCurveApply();
        try { _lifetime.Cancel(); } catch (Exception) { /* 关闭阶段忽略 */ }

        _gameZone.Dispose();
        _fanTable.Dispose();
        _lifetime.Dispose();
        _applyLock.Dispose();
    }
}
