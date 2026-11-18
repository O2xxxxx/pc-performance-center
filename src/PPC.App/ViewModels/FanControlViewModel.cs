using Ppc.App.Infrastructure;
using Ppc.Core.Models;
using Ppc.Core.Services;

namespace Ppc.App.ViewModels;

/// <summary>
/// 风扇控制页。所有状态都来自 <see cref="Ppc.Core.Abstractions.IFanControlService"/> 的真实查询结果：
/// 本机（小新 Pro 16 2023）风扇传感器读不到、GameZone WMI 调不通，因此界面必须如实显示「不支持 + 原因」，
/// 并把手动滑块与曲线应用按钮禁用，绝不伪造转速或假装写入成功。
/// </summary>
public sealed class FanControlViewModel : ObservableObject, IPageViewModel, IActivatablePageForRefresh
{
    private readonly AppServices _app;

    private bool _supportsControl;
    private string _supportDetail = "正在检测风扇控制接口…";
    private FanControlMode _selectedMode = FanControlMode.Auto;
    private int _manualPercent = 50;
    private FanCurve? _activeCurve;
    private int _hysteresisCelsius = 3;
    private int _responseDelaySeconds = 5;
    private bool _smoothing = true;
    private string _applyMessage = "";
    private string _applyState = "info";
    private string _controlSourceText = "控制来源：读取中…";
    private string _fanCountText = "风扇数量：读取中…";
    private string _channelDetailText = "正在读取风扇通道…";
    private string? _appliedPercentText = "未报告";
    private int? _appliedPercent;
    private string _curveName = "";
    private string _curvePreviewText = "未选择曲线";
    private double? _currentCpuTemperature;
    private bool _isBusy;

    public FanControlViewModel(AppServices app)
    {
        _app = app;

        FanTiles = new ObservableCollection<MetricTile>
        {
            new(Metric.Fan1Rpm, "风扇 1 转速"),
            new(Metric.Fan2Rpm, "风扇 2 转速"),
            new(Metric.Fan3Rpm, "风扇 3 转速"),
            new(Metric.Fan4Rpm, "风扇 4 转速"),
        };

        ModeOptions = new ObservableCollection<FanModeOption>
        {
            new(FanControlMode.Auto, "自动", "交给厂商固件自动调速，本页不做任何干预。"),
            new(FanControlMode.Silent, "静音", "优先降低噪音，温度会相应升高。"),
            new(FanControlMode.Performance, "性能", "优先散热，风扇转速更高、噪音更大。"),
            new(FanControlMode.Manual, "手动", "固定一个转速百分比，不随温度变化。"),
            new(FanControlMode.Curve, "曲线", "按温度曲线自动调速，可编辑节点。"),
        };

        Curves = new ObservableCollection<FanCurve>();

        ApplyModeCommand = new RelayCommand(() => _ = ApplyModeAsync(), () => !IsBusy);
        ApplyManualCommand = new RelayCommand(() =>
        {
            // 「应用手动转速」必须真的切到手动模式，否则会沿用其它模式导致点了没反应
            SelectedMode = FanControlMode.Manual;
            _ = ApplyModeAsync();
        }, () => !IsBusy && SupportsControl);
        RestoreAutoCommand = new RelayCommand(() => _ = RestoreAutoAsync(), () => !IsBusy);
        ApplyCurveCommand = new RelayCommand(() => _ = ApplyCurveAsync(), () => !IsBusy && SupportsControl);
        NewCurveCommand = new RelayCommand(NewCurve);
        DeleteCurveCommand = new RelayCommand(DeleteCurve);
        SaveCurveCommand = new RelayCommand(() => _ = SaveCurveAsync());
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync());

        _app.Fan.StateChanged += OnFanStateChanged;
        _app.SnapshotUpdated += OnSnapshotUpdated;

        LoadCurvesFromSettings();
        UpdateModeAvailability();
    }

    public string Key => "fan";
    public string Title => "风扇控制";
    public string Icon => "M12,12 L12,4 A4,4 0 0 1 16,8 Z M12,12 L19,16 A4,4 0 0 1 14,19 Z M12,12 L5,16 A4,4 0 0 1 8,7 Z";
    public string Subtitle => "风扇转速策略与温度曲线";

    /// <summary>四个风扇通道的转速格子（无值时显示「不支持」+ 真实原因）。</summary>
    public ObservableCollection<MetricTile> FanTiles { get; }

    /// <summary>本机是否支持直接控制风扇（来自服务实现的真实探测结果）。</summary>
    public bool SupportsControl
    {
        get => _supportsControl;
        private set
        {
            if (!Set(ref _supportsControl, value)) return;
            Raise(nameof(CanManualFan));
            Raise(nameof(CanApplyCurve));
            Raise(nameof(IsControlUnavailable));
            ApplyCurveCommand?.RaiseCanExecuteChanged();
            ApplyManualCommand?.RaiseCanExecuteChanged();
            UpdateModeAvailability();
        }
    }

    /// <summary>不支持直接控制时的中文原因（原样来自服务，不编造）。</summary>
    public string SupportDetail
    {
        get => _supportDetail;
        private set
        {
            if (!Set(ref _supportDetail, value)) return;
            UpdateModeAvailability();
        }
    }

    public bool IsControlUnavailable => !_supportsControl;

    /// <summary>手动转速滑块是否可用（不支持直接控制时必须禁用）。</summary>
    public bool CanManualFan => _supportsControl;

    /// <summary>曲线应用按钮是否可用。</summary>
    public bool CanApplyCurve => _supportsControl;

    /// <summary>五种控制模式，不支持的模式 Enabled=false 并带 DisabledReason。</summary>
    public ObservableCollection<FanModeOption> ModeOptions { get; }

    public FanControlMode SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (!Set(ref _selectedMode, value)) return;
            if (value != FanControlMode.Auto && !SupportsControl)
                SetMessage($"「{ModeTitle(value)}」模式不可用：{SupportDetail}", "bad");
            Raise(nameof(IsManualModeSelected));
            Raise(nameof(IsCurveModeSelected));
        }
    }

    public bool IsManualModeSelected => _selectedMode == FanControlMode.Manual;

    public bool IsCurveModeSelected => _selectedMode == FanControlMode.Curve;

    /// <summary>手动转速百分比（安全范围 20..100，界面禁止 0%）。</summary>
    public int ManualPercent
    {
        get => _manualPercent;
        set
        {
            var clamped = Math.Clamp(value, FanCurve.MinSafePercent, FanCurve.MaxPercent);
            Set(ref _manualPercent, clamped);
        }
    }

    /// <summary>设置里保存的曲线集合（app.Settings.Fan.Curves 的镜像，元素是同一批对象）。</summary>
    public ObservableCollection<FanCurve> Curves { get; }

    public FanCurve? ActiveCurve
    {
        get => _activeCurve;
        set
        {
            if (!Set(ref _activeCurve, value)) return;
            Raise(nameof(HasActiveCurve));
            if (value is not null)
            {
                HysteresisCelsius = value.HysteresisCelsius;
                ResponseDelaySeconds = value.ResponseDelaySeconds;
                Smoothing = value.Smoothing;
                _curveName = value.Name;
                Raise(nameof(CurveName));
                _app.Settings.Fan.ActiveCurveId = value.Id;
            }
            UpdateCurvePreview();
        }
    }

    public bool HasActiveCurve => _activeCurve is not null;

    /// <summary>曲线名称（写回 ActiveCurve.Name，因此曲线列表名字会随保存刷新）。</summary>
    public string CurveName
    {
        get => _curveName;
        set
        {
            var text = (value ?? string.Empty).Trim();
            if (!Set(ref _curveName, value ?? string.Empty)) return;
            if (_activeCurve is not null && text.Length > 0 && _activeCurve.Name != text)
            {
                _activeCurve.Name = text;
                Raise(nameof(ActiveCurve));
            }
        }
    }

    /// <summary>回滞温度 0..15℃。</summary>
    public int HysteresisCelsius
    {
        get => _hysteresisCelsius;
        set
        {
            var clamped = Math.Clamp(value, 0, 15);
            if (!Set(ref _hysteresisCelsius, clamped)) return;
            if (_activeCurve is not null) _activeCurve.HysteresisCelsius = clamped;
            UpdateCurvePreview();
        }
    }

    /// <summary>响应延迟 1..60 秒。</summary>
    public int ResponseDelaySeconds
    {
        get => _responseDelaySeconds;
        set
        {
            var clamped = Math.Clamp(value, 1, 60);
            if (!Set(ref _responseDelaySeconds, clamped)) return;
            if (_activeCurve is not null) _activeCurve.ResponseDelaySeconds = clamped;
        }
    }

    public bool Smoothing
    {
        get => _smoothing;
        set
        {
            if (!Set(ref _smoothing, value)) return;
            if (_activeCurve is not null) _activeCurve.Smoothing = value;
        }
    }

    /// <summary>当前温度下这条曲线的建议转速（真实插值计算，不是占位文本）。</summary>
    public string CurvePreviewText
    {
        get => _curvePreviewText;
        private set => Set(ref _curvePreviewText, value);
    }

    public string CurveCountText => $"共 {Curves.Count} 条曲线";

    /// <summary>当前 CPU 温度（给曲线编辑器画参考线用）。</summary>
    public double? CurrentCpuTemperature
    {
        get => _currentCpuTemperature;
        private set
        {
            if (!Set(ref _currentCpuTemperature, value)) return;
            UpdateCurvePreview();
        }
    }

    public string ControlSourceText
    {
        get => _controlSourceText;
        private set => Set(ref _controlSourceText, value);
    }

    public string FanCountText
    {
        get => _fanCountText;
        private set => Set(ref _fanCountText, value);
    }

    public string ChannelDetailText
    {
        get => _channelDetailText;
        private set => Set(ref _channelDetailText, value);
    }

    public int? AppliedPercent
    {
        get => _appliedPercent;
        private set
        {
            if (!Set(ref _appliedPercent, value)) return;
            AppliedPercentText = value is { } v ? v + "%" : "未报告";
        }
    }

    public string AppliedPercentText
    {
        get => _appliedPercentText ?? "未报告";
        private set => Set(ref _appliedPercentText, value);
    }

    /// <summary>最近一次操作的真实返回消息。</summary>
    public string ApplyMessage
    {
        get => _applyMessage;
        private set
        {
            if (!Set(ref _applyMessage, value)) return;
            Raise(nameof(HasApplyMessage));
        }
    }

    public string ApplyState
    {
        get => _applyState;
        private set => Set(ref _applyState, value);
    }

    public bool HasApplyMessage => !string.IsNullOrWhiteSpace(_applyMessage);

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            ApplyModeCommand?.RaiseCanExecuteChanged();
            ApplyManualCommand?.RaiseCanExecuteChanged();
            RestoreAutoCommand?.RaiseCanExecuteChanged();
            ApplyCurveCommand?.RaiseCanExecuteChanged();
        }
    }

    public RelayCommand ApplyModeCommand { get; }
    public RelayCommand ApplyManualCommand { get; }
    public RelayCommand RestoreAutoCommand { get; }
    public RelayCommand ApplyCurveCommand { get; }
    public RelayCommand NewCurveCommand { get; }
    public RelayCommand DeleteCurveCommand { get; }
    public RelayCommand SaveCurveCommand { get; }
    public RelayCommand RefreshCommand { get; }

    public Task ActivateAsync() => RefreshAsync();
    public Task DeactivateAsync() => Task.CompletedTask;

    /// <summary>曲线编辑器里拖动/增删节点后由视图调用，刷新「当前温度 → 建议转速」等派生文本。</summary>
    public void NotifyCurveEdited()
    {
        UpdateCurvePreview();
        Raise(nameof(CurveCountText));
    }

    /// <summary>页面激活 / 切换进入时拉取真实状态。</summary>
    public async Task RefreshAsync()
    {
        try
        {
            SupportsControl = _app.Fan.SupportsDirectControl;
            SupportDetail = string.IsNullOrWhiteSpace(_app.Fan.SupportDetail)
                ? "服务未提供不可用原因说明。"
                : _app.Fan.SupportDetail;
        }
        catch (Exception ex)
        {
            SupportsControl = false;
            SupportDetail = "读取风扇控制能力时异常：" + ex.Message;
            AppLog.Instance.Error("读取风扇控制能力失败", ex);
        }

        UpdateModeAvailability();
        ApplySnapshot(_app.Latest);
        await RefreshStateAsync();
    }

    private async Task RefreshStateAsync()
    {
        try
        {
            var state = await _app.Fan.GetStateAsync();
            ApplyFanState(state);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("读取风扇状态失败", ex);
            ControlSourceText = "控制来源：读取失败";
            SetMessage("读取风扇状态失败：" + ex.Message, "bad");
        }
    }

    private void ApplyFanState(Ppc.Core.Abstractions.FanState state)
    {
        try
        {
            var source = string.IsNullOrWhiteSpace(state.ControlSource) ? "未报告" : state.ControlSource;
            var controlText = state.CanControl ? "可直接控制" : "不可直接控制";
            var reason = string.IsNullOrWhiteSpace(state.ControlUnavailableReason)
                ? string.Empty
                : "（" + state.ControlUnavailableReason + "）";
            ControlSourceText = $"控制来源：{source} · {controlText}{reason}";

            FanCountText = state.Channels.Count > 0
                ? $"风扇数量：{state.Channels.Count}"
                : "风扇数量：未检测到";

            if (state.Channels.Count == 0)
            {
                ChannelDetailText = "服务未报告风扇通道信息。";
            }
            else
            {
                var parts = new List<string>();
                foreach (var channel in state.Channels)
                {
                    var rpm = channel.Rpm is { } rpmValue ? rpmValue + " RPM" : "转速不可读";
                    var percent = channel.Percent is { } percentValue ? " · " + percentValue + "%" : string.Empty;
                    var max = channel.MaxRpm is { } maxValue ? "（量程 " + maxValue + " RPM）" : string.Empty;
                    parts.Add($"{channel.Name}（通道 {channel.Index}）：{rpm}{max}{percent}");
                }
                ChannelDetailText = string.Join(" ｜ ", parts);
            }

            AppliedPercent = state.AppliedPercent;

            // 服务端报告的控制不可用时，界面同步降级（不覆盖已探测到的支持状态，只补充原因）
            if (!state.CanControl && !string.IsNullOrWhiteSpace(state.ControlUnavailableReason))
            {
                if (SupportsControl)
                {
                    SupportsControl = false;
                    SupportDetail = state.ControlUnavailableReason!;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("解析风扇状态失败", ex);
            SetMessage("解析风扇状态失败：" + ex.Message, "bad");
        }
    }

    private async Task ApplyModeAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        SetMessage("正在应用风扇模式…", "info");
        try
        {
            var mode = SelectedMode;
            var percent = Math.Clamp(ManualPercent, FanCurve.MinSafePercent, FanCurve.MaxPercent);
            var clampNote = percent == ManualPercent
                ? string.Empty
                : $"（手动转速已按安全下限夹紧到 {percent}%，本程序不会把风扇设为 0%）";
            if (percent != ManualPercent) ManualPercent = percent;

            FanCurve? curve = null;
            if (mode == FanControlMode.Curve)
            {
                if (ActiveCurve is null)
                {
                    SetMessage("应用失败：当前没有可用的风扇曲线。", "bad");
                    return;
                }
                curve = BuildCurveFromEditor();
            }

            var result = await _app.Fan.SetModeAsync(mode, curve, percent);
            SetMessage(result.Message + clampNote, result.Success ? "ok" : "bad");
            AppLog.Instance.Info($"应用风扇模式 {mode}：{(result.Success ? "成功" : "失败")} {result.Message}");

            if (result.Success)
            {
                _app.Settings.Fan.Mode = mode;
                _app.Settings.Fan.ManualPercent = percent;
                if (curve is not null) _app.Settings.Fan.ActiveCurveId = curve.Id;
                await _app.SaveSettingsAsync();
                await RefreshStateAsync();
            }
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("应用风扇模式异常", ex);
            SetMessage("应用失败：" + ex.Message, "bad");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ApplyCurveAsync()
    {
        if (!SupportsControl)
        {
            SetMessage("曲线无法应用：" + SupportDetail, "bad");
            return;
        }
        if (IsBusy) return;
        if (ActiveCurve is null)
        {
            SetMessage("曲线无法应用：当前没有可用的曲线。", "bad");
            return;
        }

        var reading = _app.Latest[Metric.CpuTemperature];
        if (reading.Value is not { } temperature)
        {
            SetMessage("曲线无法应用：当前读不到 CPU 温度（" + (reading.UnavailableReason ?? "未知原因") + "），曲线需要实时温度才能调速。", "bad");
            return;
        }

        IsBusy = true;
        SetMessage("正在按当前温度应用曲线…", "info");
        try
        {
            var curve = BuildCurveFromEditor();
            var result = await _app.Fan.ApplyCurveAsync(curve, temperature);
            SetMessage(result.Message, result.Success ? "ok" : "bad");
            AppLog.Instance.Info($"应用风扇曲线「{curve.Name}」：{(result.Success ? "成功" : "失败")} {result.Message}");

            if (result.Success)
            {
                _app.Settings.Fan.Mode = FanControlMode.Curve;
                _app.Settings.Fan.ActiveCurveId = ActiveCurve!.Id;
                await _app.SaveSettingsAsync();
                await RefreshStateAsync();
            }
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("应用风扇曲线异常", ex);
            SetMessage("应用曲线失败：" + ex.Message, "bad");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RestoreAutoAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        SetMessage("正在恢复正常（厂商自动）风扇控制…", "info");
        try
        {
            var result = await _app.Fan.RestoreAutoAsync();
            SetMessage(result.Message, result.Success ? "ok" : "bad");
            AppLog.Instance.Info($"恢复厂商自动风扇控制：{(result.Success ? "成功" : "失败")} {result.Message}");

            if (result.Success)
            {
                _app.Settings.Fan.Mode = FanControlMode.Auto;
                _selectedMode = FanControlMode.Auto;
                Raise(nameof(SelectedMode));
                Raise(nameof(IsManualModeSelected));
                Raise(nameof(IsCurveModeSelected));
                await _app.SaveSettingsAsync();
                await RefreshStateAsync();
            }
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("恢复厂商自动风扇控制异常", ex);
            SetMessage("恢复失败：" + ex.Message, "bad");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void NewCurve()
    {
        try
        {
            var template = ActiveCurve ?? FanCurve.Default();
            var name = NextCurveName();
            var curve = new FanCurve
            {
                Name = name,
                HysteresisCelsius = Math.Clamp(HysteresisCelsius, 0, 15),
                ResponseDelaySeconds = Math.Clamp(ResponseDelaySeconds, 1, 60),
                Smoothing = Smoothing,
                SensorMetric = template.SensorMetric,
                Points = template.Points.Select(p => p.Clone()).ToList(),
            };
            if (curve.Points.Count < 2) curve.Points.Add(new FanCurvePoint(50, 50));

            _app.Settings.Fan.Curves.Add(curve);
            Curves.Add(curve);
            ActiveCurve = curve;
            Raise(nameof(CurveCountText));
            SetMessage($"已新建曲线「{name}」，记得点「保存」写入设置。", "info");
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("新建风扇曲线失败", ex);
            SetMessage("新建曲线失败：" + ex.Message, "bad");
        }
    }

    private void DeleteCurve()
    {
        try
        {
            if (ActiveCurve is null)
            {
                SetMessage("删除失败：当前没有选中的曲线。", "bad");
                return;
            }
            if (Curves.Count <= 1)
            {
                SetMessage("至少保留一条曲线，最后一条不能删除。", "bad");
                return;
            }

            var removed = ActiveCurve;
            Curves.Remove(removed);
            _app.Settings.Fan.Curves.RemoveAll(c => ReferenceEquals(c, removed) || c.Id == removed.Id);

            // 删掉最后一条曲线时要自动补一条默认曲线，否则别的机器上点删除会直接 IndexOutOfRange
            if (Curves.Count == 0)
            {
                var fallback = FanCurve.Default();
                Curves.Add(fallback);
                _app.Settings.Fan.Curves.Add(fallback);
            }

            var next = Curves[0];
            ActiveCurve = next;
            _app.Settings.Fan.ActiveCurveId = next.Id;
            Raise(nameof(CurveCountText));
            SetMessage($"已删除曲线「{removed.Name}」，当前选中「{next.Name}」。", "info");
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("删除风扇曲线失败", ex);
            SetMessage("删除曲线失败：" + ex.Message, "bad");
        }
    }

    private async Task SaveCurveAsync()
    {
        if (ActiveCurve is null)
        {
            SetMessage("保存失败：当前没有选中的曲线。", "bad");
            return;
        }

        try
        {
            var normalized = BuildCurveFromEditor();
            var target = ActiveCurve;
            target.Name = normalized.Name;
            target.HysteresisCelsius = normalized.HysteresisCelsius;
            target.ResponseDelaySeconds = normalized.ResponseDelaySeconds;
            target.Smoothing = normalized.Smoothing;
            target.SensorMetric = normalized.SensorMetric;
            target.Points.Clear();
            foreach (var point in normalized.Points) target.Points.Add(point);

            var fan = _app.Settings.Fan;
            fan.Curves.Clear();
            foreach (var curve in Curves) fan.Curves.Add(curve);
            fan.ActiveCurveId = target.Id;
            fan.Mode = SelectedMode;
            fan.ManualPercent = Math.Clamp(ManualPercent, FanCurve.MinSafePercent, FanCurve.MaxPercent);

            await _app.SaveSettingsAsync();
            SetMessage($"已保存 {Curves.Count} 条曲线到 {_app.Store.FilePath}", "ok");
            AppLog.Instance.Info($"保存风扇曲线：{Curves.Count} 条 -> {_app.Store.FilePath}");
            UpdateCurvePreview();
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("保存风扇曲线失败", ex);
            SetMessage("保存失败：" + ex.Message, "bad");
        }
    }

    /// <summary>读取当前编辑器状态，并返回夹紧/排序后的副本（不会把非法值写给硬件）。</summary>
    private FanCurve BuildCurveFromEditor()
    {
        var curve = ActiveCurve ?? FanCurve.Default();
        curve.Name = string.IsNullOrWhiteSpace(CurveName) ? curve.Name : CurveName.Trim();
        curve.HysteresisCelsius = Math.Clamp(HysteresisCelsius, 0, 15);
        curve.ResponseDelaySeconds = Math.Clamp(ResponseDelaySeconds, 1, 60);
        curve.Smoothing = Smoothing;
        return curve.Normalized();
    }

    private string NextCurveName()
    {
        var index = Curves.Count + 1;
        while (Curves.Any(c => c.Name == $"曲线 {index}")) index++;
        return $"曲线 {index}";
    }

    private void LoadCurvesFromSettings()
    {
        var fan = _app.Settings.Fan;
        if (fan.Curves.Count == 0) fan.Curves.Add(FanCurve.Default());

        Curves.Clear();
        foreach (var curve in fan.Curves) Curves.Add(curve);

        var activeId = fan.ActiveCurveId;
        var active = (string.IsNullOrEmpty(activeId) ? null : Curves.FirstOrDefault(c => c.Id == activeId)) ?? Curves[0];
        ActiveCurve = active;
        ManualPercent = Math.Clamp(fan.ManualPercent, FanCurve.MinSafePercent, FanCurve.MaxPercent);
        _selectedMode = fan.Mode;
        Raise(nameof(SelectedMode));
        Raise(nameof(CurveCountText));
    }

    private void UpdateCurvePreview()
    {
        var curve = _activeCurve;
        if (curve is null)
        {
            CurvePreviewText = "未选择曲线";
            return;
        }
        if (CurrentCpuTemperature is { } temperature)
        {
            var percent = curve.Evaluate(temperature);
            CurvePreviewText = $"当前 {temperature:0.0}℃ → 该曲线建议 {percent}%";
        }
        else
        {
            CurvePreviewText = "当前温度不可读，无法计算曲线建议转速。";
        }
    }

    /// <summary>不支持直接控制时，所有非「自动」模式置灰并给出真实原因。</summary>
    private void UpdateModeAvailability()
    {
        var reason = string.IsNullOrWhiteSpace(_supportDetail) ? "本机不支持直接控制风扇转速。" : _supportDetail;
        foreach (var option in ModeOptions)
        {
            var needsControl = option.Mode != FanControlMode.Auto;
            option.Enabled = !needsControl || SupportsControl;
            option.DisabledReason = option.Enabled ? string.Empty : reason;
        }

        if (!SupportsControl && _selectedMode != FanControlMode.Auto)
        {
            _selectedMode = FanControlMode.Auto;
            Raise(nameof(SelectedMode));
            Raise(nameof(IsManualModeSelected));
            Raise(nameof(IsCurveModeSelected));
            SetMessage("风扇控制不可用，已退回「自动」模式：" + reason, "bad");
        }
    }

    private void ApplySnapshot(HardwareSnapshot snapshot)
    {
        try
        {
            foreach (var tile in FanTiles) tile.Update(snapshot[tile.Metric]);
            CurrentCpuTemperature = snapshot[Metric.CpuTemperature].Value;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("刷新风扇页快照失败", ex);
        }
    }

    private void OnSnapshotUpdated(object? sender, HardwareSnapshot snapshot) =>
        Ui(() => ApplySnapshot(snapshot));

    private void OnFanStateChanged(object? sender, Ppc.Core.Abstractions.FanState state) =>
        Ui(() => ApplyFanState(state));

    private void SetMessage(string message, string state)
    {
        ApplyState = state;
        ApplyMessage = message;
    }

    private static string ModeTitle(FanControlMode mode) => mode switch
    {
        FanControlMode.Auto => "自动",
        FanControlMode.Silent => "静音",
        FanControlMode.Performance => "性能",
        FanControlMode.Manual => "手动",
        FanControlMode.Curve => "曲线",
        _ => mode.ToString()
    };

    private static void Ui(Action action)
    {
        try
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess()) action();
            else dispatcher.BeginInvoke(action);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("UI 线程调度失败", ex);
        }
    }
}

/// <summary>风扇页的模式选项：不支持的硬件上 Enabled=false，并给出真实原因。</summary>
public sealed class FanModeOption : ObservableObject
{
    private bool _enabled = true;
    private string _disabledReason = string.Empty;

    public FanModeOption(FanControlMode mode, string name, string description)
    {
        Mode = mode;
        Name = name;
        Description = description;
    }

    public FanControlMode Mode { get; }

    public string Name { get; }

    public string Description { get; }

    public bool Enabled
    {
        get => _enabled;
        set => Set(ref _enabled, value);
    }

    public string DisabledReason
    {
        get => _disabledReason;
        set
        {
            if (!Set(ref _disabledReason, value)) return;
            Raise(nameof(HasDisabledReason));
        }
    }

    public bool HasDisabledReason => !string.IsNullOrWhiteSpace(_disabledReason);
}
