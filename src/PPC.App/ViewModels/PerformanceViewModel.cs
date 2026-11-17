using Ppc.App.Infrastructure;
using Ppc.Core.Models;
using Ppc.Core.Services;

namespace Ppc.App.ViewModels;

/// <summary>
/// 性能调节页。所有展示的数值都来自 <see cref="Ppc.Core.Abstractions.IPerformanceService"/> 的真实返回值；
/// 联想私有性能模式（GameZone WMI）探测失败时，相关卡片置灰并显示真实原因，
/// 不使用任何写死寄存器或模拟数据。
/// </summary>
public sealed class PerformanceViewModel : ObservableObject, IPageViewModel, IActivatablePageForRefresh
{
    private readonly AppServices _app;

    private PerformanceMode _selectedMode = PerformanceMode.Balanced;
    private int _maxProcessorState = 100;
    private int _minProcessorState = 5;
    private int _selectedBoostMode = 1;
    private string _currentSummary = "正在读取系统电源状态…";
    private string _powerPlanText = "电源方案：读取中…";
    private string _applyMessage = "";
    private string _applyState = "info";
    private bool _canAdjust;
    private bool _isBusy;
    private bool _lenovoModeSupported;
    private bool _useLenovoMode;
    private bool _stateReady;
    private string _lenovoModeDetail = "正在探测联想私有性能模式（GameZone WMI）…";

    public PerformanceViewModel(AppServices app)
    {
        _app = app;

        Options = new ObservableCollection<PerfOptionViewModel>();
        BoostOptions = new ObservableCollection<BoostOptionViewModel>
        {
            new("禁用（0）：不超频，功耗与温度最低", 0),
            new("启用（1）：Intel 默认睿频策略", 1),
            new("高效（2）：厂商定义的高性能睿频", 2),
        };

        ApplyCommand = new RelayCommand(() => _ = ApplyAsync(), () => !IsBusy);
        RestoreDefaultsCommand = new RelayCommand(() => _ = RestoreSystemDefaultsAsync(), () => !IsBusy);
        SelectModeCommand = new RelayCommand(SelectMode);
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync());

        _app.ProfileUpdated += OnProfileUpdated;
        RebuildOptions();
    }

    public string Key => "performance";
    public string Title => "性能调节";
    public string Icon => "M13,2 L5,14 L11,14 L10,22 L19,9 L13,9 Z";
    public string Subtitle => "电源方案、处理器状态与睿频策略";

    /// <summary>当前选中的性能模式（四张模式卡绑定它）。</summary>
    public PerformanceMode SelectedMode
    {
        get => _selectedMode;
        set
        {
            if (!Set(ref _selectedMode, value)) return;
            UpdateAdjustability();
            SetMessage($"已选中「{ModeTitle(value)}」，点「应用」后才会写入系统。", "info");
        }
    }

    /// <summary>每项能力的真实支持状态（来自设备探测结果）。</summary>
    public ObservableCollection<PerfOptionViewModel> Options { get; }

    /// <summary>最大处理器状态（PROCTHROTTLEMAX），5..100。</summary>
    public int MaxProcessorState
    {
        get => _maxProcessorState;
        set
        {
            var clamped = Math.Clamp(value, 5, 100);
            if (!Set(ref _maxProcessorState, clamped)) return;
            if (_minProcessorState > clamped)
            {
                _minProcessorState = clamped;
                Raise(nameof(MinProcessorState));
            }
            NudgeToCustom();
        }
    }

    /// <summary>最小处理器状态（PROCTHROTTLEMIN），5..100。</summary>
    public int MinProcessorState
    {
        get => _minProcessorState;
        set
        {
            var clamped = Math.Clamp(value, 5, 100);
            if (!Set(ref _minProcessorState, clamped)) return;
            if (_maxProcessorState < clamped)
            {
                _maxProcessorState = clamped;
                Raise(nameof(MaxProcessorState));
            }
            NudgeToCustom();
        }
    }

    /// <summary>睿频策略：0=禁用，1=启用，2=高效。</summary>
    public int SelectedBoostMode
    {
        get => _selectedBoostMode;
        set
        {
            var clamped = Math.Clamp(value, 0, 2);
            if (!Set(ref _selectedBoostMode, clamped)) return;
            NudgeToCustom();
        }
    }

    public ObservableCollection<BoostOptionViewModel> BoostOptions { get; }

    /// <summary>当前系统状态的摘要（来自真实读取，读不到的部分显示「未读取到」）。</summary>
    public string CurrentSummary
    {
        get => _currentSummary;
        private set => Set(ref _currentSummary, value);
    }

    public string PowerPlanText
    {
        get => _powerPlanText;
        private set => Set(ref _powerPlanText, value);
    }

    /// <summary>最近一次操作的**真实**返回消息，绝不伪造成功。</summary>
    public string ApplyMessage
    {
        get => _applyMessage;
        private set
        {
            if (!Set(ref _applyMessage, value)) return;
            Raise(nameof(HasApplyMessage));
        }
    }

    /// <summary>消息色调：ok / bad / info（绑定 StateToBrush 转换器）。</summary>
    public string ApplyState
    {
        get => _applyState;
        private set => Set(ref _applyState, value);
    }

    public bool HasApplyMessage => !string.IsNullOrWhiteSpace(_applyMessage);

    /// <summary>当前是否允许调整参数（电源接口可用，且已成功读到一次状态）。</summary>
    public bool CanAdjust
    {
        get => _canAdjust;
        private set => Set(ref _canAdjust, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!Set(ref _isBusy, value)) return;
            ApplyCommand?.RaiseCanExecuteChanged();
            RestoreDefaultsCommand?.RaiseCanExecuteChanged();
        }
    }

    /// <summary>本机是否支持联想私有性能模式（GameZone WMI）。</summary>
    public bool LenovoModeSupported
    {
        get => _lenovoModeSupported;
        private set => Set(ref _lenovoModeSupported, value);
    }

    /// <summary>「使用联想私有性能模式」开关，写回 CustomProfile.UseLenovoPerformanceMode。</summary>
    public bool UseLenovoMode
    {
        get => _useLenovoMode;
        set
        {
            if (!Set(ref _useLenovoMode, value)) return;
            _app.Settings.CustomProfile.UseLenovoPerformanceMode = value;
        }
    }

    /// <summary>置灰卡片旁边展示的原因说明。</summary>
    public string LenovoModeDetail
    {
        get => _lenovoModeDetail;
        private set => Set(ref _lenovoModeDetail, value);
    }

    public RelayCommand ApplyCommand { get; }
    public RelayCommand RestoreDefaultsCommand { get; }
    public RelayCommand SelectModeCommand { get; }
    public RelayCommand RefreshCommand { get; }

    public Task ActivateAsync() => RefreshAsync();
    public Task DeactivateAsync() => Task.CompletedTask;

    /// <summary>页面激活时拉取一次真实状态。</summary>
    public async Task RefreshAsync()
    {
        SetMessage("", "info");
        try
        {
            await RefreshStateAsync();
        }
        catch (Exception ex)
        {
            _stateReady = false;
            UpdateAdjustability();
            AppLog.Instance.Error("读取性能状态失败", ex);
            CurrentSummary = "读取系统电源状态失败：" + ex.Message;
            SetMessage("读取性能状态失败：" + ex.Message, "bad");
        }

        try
        {
            LenovoModeSupported = await _app.Performance.IsLenovoModeSupportedAsync();
            LenovoModeDetail = LenovoModeSupported
                ? "本机联想的 GameZone WMI 接口可用，自定义模式下可同时尝试厂商私有性能模式。"
                : "本机联想私有性能模式（GameZone ACPI WMI）不可用：LENOVO_GAMEZONE_DATA 方法表存在，但调用返回「无效的方法参数 / 无效的对象」。"
                  + "因此四个模式全部通过 Windows 官方电源接口实现，未写入任何 EC 寄存器或 BIOS 设置。";
        }
        catch (Exception ex)
        {
            LenovoModeSupported = false;
            LenovoModeDetail = "探测联想私有性能模式失败：" + ex.Message;
            AppLog.Instance.Error("探测联想私有模式失败", ex);
        }

        // 设置里的值优先（应用重启后保持用户选择）
        UseLenovoMode = _app.Settings.CustomProfile.UseLenovoPerformanceMode;
        RebuildOptions();
        SetMessage("", "info");
    }

    private async Task RefreshStateAsync()
    {
        var state = await _app.Performance.GetStateAsync();
        ApplyStateToUi(state);
    }

    private void ApplyStateToUi(Ppc.Core.Abstractions.PerformanceState state)
    {
        _stateReady = true;

        // 模式以本应用设置为准（服务端返回的 Mode 作为参考展示在摘要里）
        SelectedMode = _app.Settings.CurrentMode;

        var profile = _app.Settings.CustomProfile;
        var max = state.MaxProcessorState is { } mx
            ? Math.Clamp(mx, 5, 100)
            : Math.Clamp(profile.MaxProcessorStatePercent ?? 100, 5, 100);
        var min = state.MinProcessorState is { } mn
            ? Math.Clamp(mn, 5, 100)
            : Math.Clamp(profile.MinProcessorStatePercent ?? 5, 5, 100);
        min = Math.Min(min, max);
        _maxProcessorState = max;
        _minProcessorState = min;
        Raise(nameof(MaxProcessorState));
        Raise(nameof(MinProcessorState));

        var boost = state.BoostMode is { } b && b is >= 0 and <= 2
            ? b
            : Math.Clamp(profile.BoostMode ?? 1, 0, 2);
        _selectedBoostMode = boost;
        Raise(nameof(SelectedBoostMode));

        PowerPlanText = string.IsNullOrWhiteSpace(state.PowerSchemeName)
            ? "电源方案：未读取到"
            : $"电源方案：{state.PowerSchemeName}" + (state.PowerSchemeGuid is { } guid ? $"（{guid:D}）" : "");

        CurrentSummary = string.IsNullOrWhiteSpace(state.Summary)
            ? ComposeSummary(state)
            : state.Summary;

        UpdateAdjustability();
        SetMessage("", "info");
    }

    private static string ComposeSummary(Ppc.Core.Abstractions.PerformanceState state)
    {
        var maxText = state.MaxProcessorState is { } mx ? mx + "%" : "未读取到";
        var minText = state.MinProcessorState is { } mn ? mn + "%" : "未读取到";
        var boostText = state.BoostMode switch
        {
            0 => "禁用",
            1 => "启用",
            2 => "高效",
            _ => "未读取到"
        };
        var modeText = state.Mode switch
        {
            PerformanceMode.Eco => "节能",
            PerformanceMode.Balanced => "均衡",
            PerformanceMode.Performance => "高性能",
            PerformanceMode.Custom => "自定义",
            _ => "未知"
        };
        var thermal = string.IsNullOrWhiteSpace(state.LenovoThermalMode) ? "未读取到" : state.LenovoThermalMode;
        return $"系统当前：{modeText} · 最大处理器状态 {maxText} · 最小处理器状态 {minText} · 睿频 {boostText} · 联想热模式 {thermal}";
    }

    private void UpdateAdjustability()
    {
        CanAdjust = _stateReady;
    }

    /// <summary>拖动滑块时，把模式自动切到「自定义」，避免用户误以为改动了当前模式。</summary>
    private void NudgeToCustom()
    {
        if (SelectedMode == PerformanceMode.Custom) return;
        SelectedMode = PerformanceMode.Custom;
        SetMessage("检测到参数改动，已切换到「自定义」模式，点「应用」后写入系统。", "info");
    }

    /// <summary>四张模式卡 / 其它入口统一走这里；参数为枚举名或中文名。</summary>
    private void SelectMode(object? parameter)
    {
        var text = parameter?.ToString();
        if (string.IsNullOrWhiteSpace(text)) return;

        PerformanceMode mode;
        switch (text)
        {
            case "节能": mode = PerformanceMode.Eco; break;
            case "均衡": mode = PerformanceMode.Balanced; break;
            case "高性能": mode = PerformanceMode.Performance; break;
            case "自定义": mode = PerformanceMode.Custom; break;
            default:
                if (!Enum.TryParse(text, true, out mode)) return;
                break;
        }

        SelectedMode = mode;
    }

    private async Task ApplyAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        SetMessage("正在写入系统电源设置…", "info");
        try
        {
            if (_app.Settings.CustomProfile is null) _app.Settings.CustomProfile = new CustomPerfProfile();
            var profile = _app.Settings.CustomProfile;
            profile.MaxProcessorStatePercent = Math.Clamp(MaxProcessorState, 5, 100);
            profile.MinProcessorStatePercent = Math.Clamp(Math.Min(MinProcessorState, MaxProcessorState), 5, 100);
            profile.BoostMode = Math.Clamp(SelectedBoostMode, 0, 2);
            profile.UseLenovoPerformanceMode = UseLenovoMode && LenovoModeSupported;

            var mode = SelectedMode;
            _app.Settings.CurrentMode = mode;

            var result = await _app.Performance.ApplyModeAsync(mode, profile);
            SetMessage(result.Message, result.Success ? "ok" : "bad");
            AppLog.Instance.Info($"应用性能模式 {mode}：{(result.Success ? "成功" : "失败")} {result.Message}");

            if (result.Success)
            {
                await _app.SaveSettingsAsync();
                try
                {
                    await RefreshStateAsync();
                }
                catch (Exception ex)
                {
                    AppLog.Instance.Error("应用后刷新状态失败", ex);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("应用性能模式异常", ex);
            SetMessage("应用失败：" + ex.Message, "bad");
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task RestoreSystemDefaultsAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        SetMessage("正在恢复系统默认电源策略…", "info");
        try
        {
            var result = await _app.Performance.RestoreAsync();
            SetMessage(result.Message, result.Success ? "ok" : "bad");
            AppLog.Instance.Info($"恢复系统默认电源策略：{(result.Success ? "成功" : "失败")} {result.Message}");

            if (result.Success)
            {
                _app.Settings.CurrentMode = PerformanceMode.Balanced;
                _app.Settings.CustomProfile = new CustomPerfProfile();
                await _app.SaveSettingsAsync();
                try
                {
                    await RefreshStateAsync();
                }
                catch (Exception ex)
                {
                    AppLog.Instance.Error("恢复后刷新状态失败", ex);
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("恢复系统默认电源策略异常", ex);
            SetMessage("恢复失败：" + ex.Message, "bad");
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>用设备能力表重建选项列表（真实支持状态，不推测）。</summary>
    private void RebuildOptions()
    {
        try
        {
            Options.Clear();

            var capabilities = _app.Device.Capabilities;
            if (capabilities.Count == 0)
            {
                var pending = new PerfOptionViewModel("硬件能力检测", "尚未完成设备检测，暂时无法判断本机支持哪些性能接口。")
                {
                    Supported = false,
                    ValueText = "未检测",
                    IsAdjustable = false,
                };
                Options.Add(pending);
            }
            else
            {
                foreach (var capability in capabilities)
                {
                    var detail = string.IsNullOrWhiteSpace(capability.Detail) ? "无详细说明" : capability.Detail;
                    Options.Add(new PerfOptionViewModel(capability.Title, detail)
                    {
                        Supported = capability.Supported,
                        ValueText = capability.Supported ? "可用" : "不可用",
                        IsAdjustable = capability.Supported,
                    });
                }
            }

            // 能力表里通常没有单独列出联想私有模式，这里补一行真实状态
            var alreadyListed = capabilities.Any(c =>
                c.Key.Contains("lenovo", StringComparison.OrdinalIgnoreCase) ||
                c.Title.Contains("联想") ||
                c.Key.Contains("gamezone", StringComparison.OrdinalIgnoreCase));

            if (!alreadyListed)
            {
                Options.Add(new PerfOptionViewModel("联想私有性能模式（GameZone WMI）", LenovoModeDetail)
                {
                    Supported = LenovoModeSupported,
                    ValueText = LenovoModeSupported ? "可用" : "不可用",
                    IsAdjustable = false,
                });
            }
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("重建性能选项列表失败", ex);
        }
    }

    private void OnProfileUpdated(object? sender, DeviceProfile profile) =>
        Ui(() =>
        {
            RebuildOptions();
            UpdateAdjustability();
        });

    private void SetMessage(string message, string state)
    {
        ApplyState = state;
        ApplyMessage = message;
    }

    private static string ModeTitle(PerformanceMode mode) => mode switch
    {
        PerformanceMode.Eco => "节能",
        PerformanceMode.Balanced => "均衡",
        PerformanceMode.Performance => "高性能",
        PerformanceMode.Custom => "自定义",
        _ => mode.ToString()
    };

    /// <summary>后台事件回到 UI 线程（页面 VM 可能被监控线程的事件触发）。</summary>
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

/// <summary>「可调参数 / 能力」列表里的一行。Supported/ValueText 来自真实探测结果。</summary>
public sealed class PerfOptionViewModel : ObservableObject
{
    private bool _supported;
    private string _valueText = "";
    private bool _isAdjustable;

    public PerfOptionViewModel(string title, string description)
    {
        Title = title;
        Description = description;
    }

    public string Title { get; }

    public string Description { get; }

    /// <summary>该能力是否真实可用。</summary>
    public bool Supported
    {
        get => _supported;
        set => Set(ref _supported, value);
    }

    /// <summary>界面展示的支持状态文本，例如「可用 / 不可用 / 未检测」。</summary>
    public string ValueText
    {
        get => _valueText;
        set => Set(ref _valueText, value);
    }

    /// <summary>是否可以在本页调整（不可用时界面置灰）。</summary>
    public bool IsAdjustable
    {
        get => _isAdjustable;
        set => Set(ref _isAdjustable, value);
    }
}

/// <summary>睿频策略下拉项。</summary>
public sealed class BoostOptionViewModel
{
    public BoostOptionViewModel(string name, int value)
    {
        Name = name;
        Value = value;
    }

    public string Name { get; }

    /// <summary>PERFBOOSTMODE 的取值：0=禁用，1=启用，2=高效。</summary>
    public int Value { get; }
}
