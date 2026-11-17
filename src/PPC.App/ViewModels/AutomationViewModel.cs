using System.Globalization;
using System.Windows.Data;
using Ppc.App.Infrastructure;
using Ppc.Core.Models;
using Ppc.Core.Services;

namespace Ppc.App.ViewModels;

/// <summary>
/// 自动化策略页。规则直接存放在 app.Settings.Automation.Rules 里（与 AutomationService 引用同一个列表），
/// 增删改都会写回设置文件；运行状态文本来自 AutomationService.StatusChanged 的真实事件。
/// </summary>
public sealed class AutomationViewModel : ObservableObject, IPageViewModel, IActivatablePageForRefresh
{
    private static readonly (RuleTrigger Trigger, string Text)[] TriggerEntries =
    {
        (RuleTrigger.AcConnected, "接通电源"),
        (RuleTrigger.AcDisconnected, "断开电源（用电池）"),
        (RuleTrigger.BatteryBelow, "电池低于阈值"),
        (RuleTrigger.ProcessRunning, "指定进程运行中"),
        (RuleTrigger.TemperatureAbove, "CPU 温度高于阈值"),
        (RuleTrigger.TemperatureBelow, "CPU 温度低于阈值"),
    };

    private static readonly (PerformanceMode Mode, string Text)[] ModeEntries =
    {
        (PerformanceMode.Eco, "节能"),
        (PerformanceMode.Balanced, "均衡"),
        (PerformanceMode.Performance, "高性能"),
        (PerformanceMode.Custom, "自定义"),
    };

    private static readonly (FanControlMode? Mode, string Text)[] FanModeEntries =
    {
        (null, "不改变风扇模式"),
        (FanControlMode.Auto, "自动"),
        (FanControlMode.Silent, "静音"),
        (FanControlMode.Performance, "性能"),
        (FanControlMode.Manual, "手动"),
        (FanControlMode.Curve, "曲线"),
    };

    private readonly AppServices _app;

    private bool _initializing = true;
    private bool _enabled;
    private bool _thermalGuardEnabled;
    private int _thermalGuardCelsius = 95;
    private AutomationRule? _selectedRule;
    // 行容器重建时 ListBox 会短暂把 SelectedItem 清空，这个标志用来忽略那次假变更
    private bool _suppressRuleSelection;
    private string _statusText = "正在读取自动化状态…";
    private bool _isProcessPickerOpen;
    private string? _selectedProcessExample;

    public AutomationViewModel(AppServices app)
    {
        _app = app;

        TriggerOptions = new ObservableCollection<string>(TriggerEntries.Select(e => e.Text));
        ModeOptions = new ObservableCollection<string>(ModeEntries.Select(e => e.Text));
        FanModeOptions = new ObservableCollection<string>(FanModeEntries.Select(e => e.Text));
        Rules = new ObservableCollection<AutomationRule>();
        StatusHistory = new ObservableCollection<string>();
        ProcessCandidates = new ObservableCollection<string>();
        ProcessExampleOptions = new ObservableCollection<string>
        {
            "GenshinImpact",
            "StarRail",
            "League of Legends",
            "cs2",
            "TslGame",
            "eldenring",
            "Cyberpunk2077",
            "ForzaHorizon5",
        };

        AddRuleCommand = new RelayCommand(AddRule);
        DeleteRuleCommand = new RelayCommand(() => _ = DeleteRuleAsync(), () => SelectedRule is not null);
        SaveCommand = new RelayCommand(() => _ = SaveAsync());
        OpenProcessPickerCommand = new RelayCommand(OpenProcessPicker, () => SelectedRule is not null);
        CloseProcessPickerCommand = new RelayCommand(() => IsProcessPickerOpen = false);
        PickProcessCommand = new RelayCommand(PickProcess);
        RefreshCommand = new RelayCommand(() => _ = RefreshAsync());

        _app.Automation.StatusChanged += OnAutomationStatusChanged;
        LoadFromSettings();
    }

    public string Key => "automation";
    public string Title => "自动化策略";
    public string Icon => "M3,7 L21,7 L21,9 L3,9 Z M3,15 L21,15 L21,17 L3,17 Z M8,5 L10,5 L10,11 L8,11 Z M14,13 L16,13 L16,19 L14,19 Z";
    public string Subtitle => "按电源、进程与温度自动切换模式";

    /// <summary>自动化总开关。</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!Set(ref _enabled, value)) return;
            Raise(nameof(OverviewText));
            if (_initializing) return;

            try
            {
                _app.Automation.IsEnabled = value;
                _app.Settings.Automation.Enabled = value;
                AddHistory(value ? "自动化已开启：规则将按优先级评估并执行" : "自动化已关闭：规则暂停执行");
            }
            catch (Exception ex)
            {
                AppLog.Instance.Error("切换自动化总开关失败", ex);
                StatusText = "切换自动化失败：" + ex.Message;
            }

            _ = PersistAsync(value ? "已开启自动化" : "已关闭自动化");
        }
    }

    public bool ThermalGuardEnabled
    {
        get => _thermalGuardEnabled;
        set
        {
            if (!Set(ref _thermalGuardEnabled, value)) return;
            Raise(nameof(OverviewText));
            _app.Settings.Automation.ThermalGuardEnabled = value;
            if (_initializing) return;
            AddHistory(value ? $"温度保护已开启（阈值 {ThermalGuardCelsius}℃）" : "温度保护已关闭");
            _ = PersistAsync(value ? "已开启温度保护" : "已关闭温度保护");
        }
    }

    /// <summary>温度保护阈值（AutomationService 会夹紧到 60..110℃）。</summary>
    public int ThermalGuardCelsius
    {
        get => _thermalGuardCelsius;
        set
        {
            var clamped = Math.Clamp(value, 60, 110);
            if (!Set(ref _thermalGuardCelsius, clamped)) return;
            Raise(nameof(OverviewText));
            _app.Settings.Automation.ThermalGuardCelsius = clamped;
        }
    }

    /// <summary>规则集合（与 app.Settings.Automation.Rules 是同一批对象）。</summary>
    public ObservableCollection<AutomationRule> Rules { get; }

    public AutomationRule? SelectedRule
    {
        get => _selectedRule;
        set
        {
            if (_suppressRuleSelection) return;
            if (!Set(ref _selectedRule, value)) return;
            Raise(nameof(IsRuleSelected));
            Raise(nameof(HasSelectedRule));
            DeleteRuleCommand?.RaiseCanExecuteChanged();
            OpenProcessPickerCommand?.RaiseCanExecuteChanged();
            if (value is null) IsProcessPickerOpen = false;
            RefreshRuleBindings();
        }
    }

    public bool IsRuleSelected => _selectedRule is not null;

    public bool HasSelectedRule => _selectedRule is not null;

    public bool HasNoRules => Rules.Count == 0;

    public string RuleCountText => $"共 {Rules.Count} 条规则";

    /// <summary>来自 AutomationService.StatusChanged 的真实状态文本。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    /// <summary>最近 30 条自动化事件（含规则触发、温度保护介入）。</summary>
    public ObservableCollection<string> StatusHistory { get; }

    /// <summary>触发类型中文下拉。</summary>
    public ObservableCollection<string> TriggerOptions { get; }

    /// <summary>目标性能模式中文下拉。</summary>
    public ObservableCollection<string> ModeOptions { get; }

    /// <summary>目标风扇模式中文下拉（含「不改变」）。</summary>
    public ObservableCollection<string> FanModeOptions { get; }

    public string OverviewText
    {
        get
        {
            var automation = _app.Settings.Automation;
            var total = Rules.Count;
            var enabled = Rules.Count(r => r.Enabled);
            var guard = automation.ThermalGuardEnabled ? $"温度保护 {ThermalGuardCelsius}℃" : "温度保护关闭";
            return $"总开关 {(_enabled ? "开启" : "关闭")} · 规则 {enabled}/{total} 条启用 · {guard}";
        }
    }

    // ---------- 选中规则的编辑绑定（AutomationRule 无 INPC，因此由 VM 代理并主动通知） ----------

    public string RuleName
    {
        get => _selectedRule?.Name ?? string.Empty;
        set
        {
            var rule = _selectedRule;
            if (rule is null) return;
            var text = (value ?? string.Empty).Trim();
            if (rule.Name == text) return;
            rule.Name = text.Length == 0 ? "未命名规则" : text;
            Raise();
            Raise(nameof(RuleCountText));
            RefreshRuleRow(rule);
        }
    }

    public bool RuleEnabled
    {
        get => _selectedRule?.Enabled ?? false;
        set
        {
            var rule = _selectedRule;
            if (rule is null || rule.Enabled == value) return;
            rule.Enabled = value;
            Raise();
            Raise(nameof(OverviewText));
            RefreshRuleRow(rule);
        }
    }

    /// <summary>选中规则的阈值（按触发类型夹紧到合理范围）。</summary>
    public double Threshold
    {
        get => _selectedRule?.Threshold ?? 0;
        set
        {
            var rule = _selectedRule;
            if (rule is null) return;
            var clamped = rule.Trigger switch
            {
                RuleTrigger.BatteryBelow => Math.Clamp(value, 0, 100),
                RuleTrigger.TemperatureAbove => Math.Clamp(value, 60, 110),
                RuleTrigger.TemperatureBelow => Math.Clamp(value, 30, 90),
                _ => Math.Clamp(value, 0, 120),
            };
            if (Math.Abs(rule.Threshold - clamped) < 0.0001) return;
            rule.Threshold = clamped;
            Raise();
            Raise(nameof(RuleDetailText));
            RefreshRuleRow(rule);
        }
    }

    /// <summary>选中规则的冷却秒数（0–3600）。</summary>
    public int CooldownSeconds
    {
        get => _selectedRule?.CooldownSeconds ?? 0;
        set
        {
            var rule = _selectedRule;
            if (rule is null) return;
            var clamped = Math.Clamp(value, 0, 3600);
            if (rule.CooldownSeconds == clamped) return;
            rule.CooldownSeconds = clamped;
            Raise();
            Raise(nameof(RuleDetailText));
            RefreshRuleRow(rule);
        }
    }

    /// <summary>选中规则的优先级（0–100，越大越先判断）。</summary>
    public int Priority
    {
        get => _selectedRule?.Priority ?? 0;
        set
        {
            var rule = _selectedRule;
            if (rule is null) return;
            var clamped = Math.Clamp(value, 0, 100);
            if (rule.Priority == clamped) return;
            rule.Priority = clamped;
            Raise();
            Raise(nameof(RuleDetailText));
            RefreshRuleRow(rule);
        }
    }

    /// <summary>规则解除后是否恢复先前的性能/风扇模式。</summary>
    public bool RestorePrevious
    {
        get => _selectedRule?.RestorePrevious ?? false;
        set
        {
            var rule = _selectedRule;
            if (rule is null || rule.RestorePrevious == value) return;
            rule.RestorePrevious = value;
            Raise();
            Raise(nameof(RuleDetailText));
            RefreshRuleRow(rule);
        }
    }

    public int SelectedTriggerIndex
    {
        get
        {
            var rule = _selectedRule;
            if (rule is null) return -1;
            for (var i = 0; i < TriggerEntries.Length; i++)
                if (TriggerEntries[i].Trigger == rule.Trigger) return i;
            return -1;
        }
        set
        {
            var rule = _selectedRule;
            if (rule is null || value < 0 || value >= TriggerEntries.Length) return;
            rule.Trigger = TriggerEntries[value].Trigger;
            Raise();
            RefreshRuleBindings();
            RefreshRuleRow(rule);
        }
    }

    public int SelectedModeIndex
    {
        get
        {
            var rule = _selectedRule;
            if (rule is null) return -1;
            for (var i = 0; i < ModeEntries.Length; i++)
                if (ModeEntries[i].Mode == rule.TargetMode) return i;
            return -1;
        }
        set
        {
            var rule = _selectedRule;
            if (rule is null || value < 0 || value >= ModeEntries.Length) return;
            rule.TargetMode = ModeEntries[value].Mode;
            Raise();
            RefreshRuleRow(rule);
        }
    }

    public int SelectedFanModeIndex
    {
        get
        {
            var rule = _selectedRule;
            if (rule is null) return -1;
            for (var i = 0; i < FanModeEntries.Length; i++)
                if (FanModeEntries[i].Mode == rule.TargetFanMode) return i;
            if (rule.TargetFanMode is null) return 0;
            return -1;
        }
        set
        {
            var rule = _selectedRule;
            if (rule is null || value < 0 || value >= FanModeEntries.Length) return;
            rule.TargetFanMode = FanModeEntries[value].Mode;
            Raise();
            RefreshRuleRow(rule);
        }
    }

    public string ThresholdUnit => _selectedRule?.Trigger switch
    {
        RuleTrigger.BatteryBelow => "%",
        RuleTrigger.TemperatureAbove => "℃",
        RuleTrigger.TemperatureBelow => "℃",
        _ => "—"
    };

    public string ThresholdHint => _selectedRule?.Trigger switch
    {
        RuleTrigger.BatteryBelow => "电池电量低于该百分比时触发（0–100）。",
        RuleTrigger.TemperatureAbove => "CPU 温度高于该温度时触发（60–110℃）。",
        RuleTrigger.TemperatureBelow => "CPU 温度低于该温度时触发（30–90℃）。",
        RuleTrigger.ProcessRunning => "进程触发不使用阈值，请填写进程名。",
        _ => "电源状态触发不使用阈值。"
    };

    public bool IsThresholdRelevant => _selectedRule?.Trigger is RuleTrigger.BatteryBelow or RuleTrigger.TemperatureAbove or RuleTrigger.TemperatureBelow;

    public bool IsProcessRelevant => _selectedRule?.Trigger == RuleTrigger.ProcessRunning;

    /// <summary>选中规则的进程名（进程触发时使用）。</summary>
    public string ProcessName
    {
        get => _selectedRule?.ProcessName ?? string.Empty;
        set
        {
            var rule = _selectedRule;
            if (rule is null) return;
            var text = (value ?? string.Empty).Trim();
            if ((rule.ProcessName ?? string.Empty) == text) return;
            rule.ProcessName = text.Length == 0 ? null : text;
            Raise();
            RefreshRuleRow(rule);
        }
    }

    /// <summary>常见游戏示例下拉；选中后直接填入进程名输入框。</summary>
    public string? SelectedProcessExample
    {
        get => _selectedProcessExample;
        set
        {
            if (!Set(ref _selectedProcessExample, value)) return;
            if (string.IsNullOrWhiteSpace(value) || _selectedRule is null) return;
            ProcessName = value;
            StatusText = $"已把进程名填成「{value}」，记得点「保存」。";
        }
    }

    public ObservableCollection<string> ProcessExampleOptions { get; }

    public ObservableCollection<string> ProcessCandidates { get; }

    public bool IsProcessPickerOpen
    {
        get => _isProcessPickerOpen;
        private set
        {
            if (!Set(ref _isProcessPickerOpen, value)) return;
            Raise(nameof(HasProcessCandidates));
        }
    }

    public bool HasProcessCandidates => _isProcessPickerOpen && ProcessCandidates.Count > 0;

    public string ProcessPickerText => ProcessCandidates.Count == 0
        ? "没有读取到进程列表。"
        : $"当前运行中的进程：{ProcessCandidates.Count} 个，点一个填入。";

    public RelayCommand AddRuleCommand { get; }
    public RelayCommand DeleteRuleCommand { get; }
    public RelayCommand SaveCommand { get; }
    public RelayCommand OpenProcessPickerCommand { get; }
    public RelayCommand CloseProcessPickerCommand { get; }
    public RelayCommand PickProcessCommand { get; }
    public RelayCommand RefreshCommand { get; }

    public Task ActivateAsync() => RefreshAsync();
    public Task DeactivateAsync() => Task.CompletedTask;

    /// <summary>页面激活时把设置里的真实状态读进界面（不覆盖未保存的编辑当且仅当没有规则差异）。</summary>
    public Task RefreshAsync()
    {
        LoadFromSettings();
        return Task.CompletedTask;
    }

    private void LoadFromSettings()
    {
        try
        {
            _initializing = true;
            var automation = _app.Settings.Automation;

            Rules.Clear();
            foreach (var rule in automation.Rules) Rules.Add(rule);

            _enabled = automation.Enabled;
            Raise(nameof(Enabled));
            _thermalGuardEnabled = automation.ThermalGuardEnabled;
            Raise(nameof(ThermalGuardEnabled));
            _thermalGuardCelsius = Math.Clamp(automation.ThermalGuardCelsius, 60, 110);
            Raise(nameof(ThermalGuardCelsius));
            Raise(nameof(OverviewText));
            Raise(nameof(RuleCountText));
            Raise(nameof(HasNoRules));

            SelectedRule = Rules.FirstOrDefault();
            IsProcessPickerOpen = false;

            StatusText = automation.Enabled
                ? $"自动化已开启，共 {Rules.Count} 条规则；等待首次触发事件…"
                : "自动化已关闭，规则不会执行。";
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("读取自动化设置失败", ex);
            StatusText = "读取自动化设置失败：" + ex.Message;
        }
        finally
        {
            _initializing = false;
        }
    }

    private void AddRule()
    {
        try
        {
            var index = Rules.Count + 1;
            var name = $"规则 {index}";
            while (Rules.Any(r => r.Name == name)) name = $"规则 {++index}";

            var rule = new AutomationRule
            {
                Name = name,
                Enabled = true,
                Priority = 10,
                Trigger = RuleTrigger.AcConnected,
                Threshold = 80,
                TargetMode = PerformanceMode.Performance,
                TargetFanMode = null,
                RestorePrevious = true,
                CooldownSeconds = 60,
            };

            _app.Settings.Automation.Rules.Add(rule);
            Rules.Add(rule);
            SelectedRule = rule;
            Raise(nameof(RuleCountText));
            Raise(nameof(HasNoRules));
            Raise(nameof(OverviewText));
            StatusText = $"已添加规则「{name}」，正在保存…";
            _ = PersistAsync($"已添加规则「{name}」");
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("添加自动化规则失败", ex);
            StatusText = "添加规则失败：" + ex.Message;
        }
    }

    private async Task DeleteRuleAsync()
    {
        var rule = SelectedRule;
        if (rule is null)
        {
            StatusText = "没有选中的规则。";
            return;
        }

        try
        {
            var name = rule.Name;
            _app.Settings.Automation.Rules.Remove(rule);
            Rules.Remove(rule);
            SelectedRule = Rules.FirstOrDefault();
            Raise(nameof(RuleCountText));
            Raise(nameof(HasNoRules));
            Raise(nameof(OverviewText));
            await PersistAsync($"已删除规则「{name}」");
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("删除自动化规则失败", ex);
            StatusText = "删除规则失败：" + ex.Message;
        }
    }

    private async Task SaveAsync()
    {
        ClampRules();
        await PersistAsync("已保存规则设置");
        RefreshRuleBindings();
    }

    /// <summary>只写 flags 与磁盘，不动 Settings.Automation.Rules（避免与后台评估线程同时改 List）。</summary>
    private async Task PersistAsync(string note)
    {
        try
        {
            var automation = _app.Settings.Automation;
            automation.Enabled = _enabled;
            automation.ThermalGuardEnabled = _thermalGuardEnabled;
            automation.ThermalGuardCelsius = Math.Clamp(_thermalGuardCelsius, 60, 110);

            await _app.SaveSettingsAsync();
            StatusText = $"[{DateTime.Now:HH:mm:ss}] {note}（{Rules.Count} 条规则）→ {_app.Store.FilePath}";
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("保存自动化设置失败", ex);
            StatusText = "保存失败：" + ex.Message;
        }
    }

    private void ClampRules()
    {
        foreach (var rule in Rules)
        {
            rule.Threshold = Math.Clamp(rule.Threshold, 0, 120);
            rule.Priority = Math.Clamp(rule.Priority, 0, 100);
            rule.CooldownSeconds = Math.Clamp(rule.CooldownSeconds, 0, 3600);
            if (string.IsNullOrWhiteSpace(rule.Name)) rule.Name = "未命名规则";
        }
    }

    private void OpenProcessPicker()
    {
        try
        {
            ProcessCandidates.Clear();
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var process in System.Diagnostics.Process.GetProcesses())
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(process.ProcessName)) names.Add(process.ProcessName);
                }
                catch (Exception ex)
                {
                    // 受保护进程读不到名字，跳过
                    AppLog.Instance.Warn("读取进程名失败：" + ex.Message);
                }
                finally
                {
                    process.Dispose();
                }
            }

            foreach (var name in names) ProcessCandidates.Add(name);
            IsProcessPickerOpen = true;
            Raise(nameof(ProcessPickerText));
            Raise(nameof(HasProcessCandidates));
            StatusText = ProcessCandidates.Count == 0
                ? "没有读取到进程列表。"
                : $"已列出 {ProcessCandidates.Count} 个运行中的进程，点一个填入。";
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("枚举进程列表失败", ex);
            IsProcessPickerOpen = false;
            StatusText = "读取进程列表失败：" + ex.Message;
        }
    }

    private void PickProcess(object? parameter)
    {
        var name = parameter?.ToString();
        if (string.IsNullOrWhiteSpace(name)) return;
        ProcessName = name;
        IsProcessPickerOpen = false;
        StatusText = $"已选择进程「{name}」，记得点「保存」。";
    }

    /// <summary>
    /// AutomationRule 是 PPC.Core 里的纯数据模型（没有 INotifyPropertyChanged），
    /// 编辑后列表行不会自动刷新；这里把该行从镜像集合里取出再放回，
    /// 让 ListBox 重建这一个容器重新读取显示值，选中项由 _selectedRule 保持。
    /// </summary>
    private void RefreshRuleRow(AutomationRule rule)
    {
        var index = Rules.IndexOf(rule);
        if (index < 0) return;
        _suppressRuleSelection = true;
        try
        {
            Rules.RemoveAt(index);
            Rules.Insert(index, rule);
        }
        finally
        {
            _suppressRuleSelection = false;
        }
        if (ReferenceEquals(_selectedRule, rule)) Raise(nameof(SelectedRule));
    }

    private void RefreshRuleBindings()
    {
        Raise(nameof(RuleName));
        Raise(nameof(RuleEnabled));
        Raise(nameof(SelectedTriggerIndex));
        Raise(nameof(SelectedModeIndex));
        Raise(nameof(SelectedFanModeIndex));
        Raise(nameof(ProcessName));
        Raise(nameof(ThresholdUnit));
        Raise(nameof(ThresholdHint));
        Raise(nameof(IsThresholdRelevant));
        Raise(nameof(IsProcessRelevant));
        Raise(nameof(RuleDetailText));
    }

    /// <summary>选中规则的摘要（列表/详情共用，真实反映当前设置）。</summary>
    public string RuleDetailText
    {
        get
        {
            var rule = _selectedRule;
            if (rule is null) return "尚未选择规则。";
            var trigger = TriggerName(rule.Trigger);
            var threshold = rule.Trigger switch
            {
                RuleTrigger.BatteryBelow => $"阈值 {rule.Threshold:0}%",
                RuleTrigger.TemperatureAbove => $"阈值 {rule.Threshold:0}℃",
                RuleTrigger.TemperatureBelow => $"阈值 {rule.Threshold:0}℃",
                RuleTrigger.ProcessRunning => $"进程 {(string.IsNullOrWhiteSpace(rule.ProcessName) ? "（未填写）" : rule.ProcessName)}",
                _ => "无阈值"
            };
            var fan = rule.TargetFanMode is { } fanMode ? "风扇 " + FanModeText(fanMode) : "风扇不改变";
            return $"{trigger} · {threshold} · 目标 {ModeText(rule.TargetMode)} · {fan} · 冷却 {rule.CooldownSeconds} 秒"
                   + (rule.RestorePrevious ? " · 条件解除后恢复" : " · 条件解除后不恢复");
        }
    }

    private void OnAutomationStatusChanged(object? sender, string message) =>
        Ui(() =>
        {
            StatusText = $"[{DateTime.Now:HH:mm:ss}] {message}";
            AddHistory(message);
        });

    private void AddHistory(string message)
    {
        try
        {
            StatusHistory.Insert(0, $"{DateTime.Now:HH:mm:ss}  {message}");
            while (StatusHistory.Count > 30) StatusHistory.RemoveAt(StatusHistory.Count - 1);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("写入自动化事件列表失败", ex);
        }
    }

    private static string TriggerName(RuleTrigger trigger) => trigger switch
    {
        RuleTrigger.AcConnected => "接通电源",
        RuleTrigger.AcDisconnected => "断开电源（用电池）",
        RuleTrigger.BatteryBelow => "电池低于阈值",
        RuleTrigger.ProcessRunning => "指定进程运行中",
        RuleTrigger.TemperatureAbove => "CPU 温度高于阈值",
        RuleTrigger.TemperatureBelow => "CPU 温度低于阈值",
        _ => trigger.ToString()
    };

    private static string ModeText(PerformanceMode mode) => mode switch
    {
        PerformanceMode.Eco => "节能",
        PerformanceMode.Balanced => "均衡",
        PerformanceMode.Performance => "高性能",
        PerformanceMode.Custom => "自定义",
        _ => mode.ToString()
    };

    private static string FanModeText(FanControlMode mode) => mode switch
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

/// <summary>规则列表：触发条件 → 中文（单向显示）。</summary>
public sealed class RuleTriggerTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        AutomationRule rule => RuleText(rule.Trigger),
        RuleTrigger trigger => RuleText(trigger),
        _ => "—",
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;

    private static string RuleText(RuleTrigger trigger) => trigger switch
    {
        RuleTrigger.AcConnected => "接通电源",
        RuleTrigger.AcDisconnected => "断开电源",
        RuleTrigger.BatteryBelow => "电池电量低于",
        RuleTrigger.ProcessRunning => "进程运行中",
        RuleTrigger.TemperatureAbove => "CPU 温度高于",
        RuleTrigger.TemperatureBelow => "CPU 温度低于",
        _ => trigger.ToString()
    };
}

/// <summary>规则列表：阈值 → 中文（按触发类型带单位）。</summary>
public sealed class RuleThresholdTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not AutomationRule rule) return "—";
        return rule.Trigger switch
        {
            RuleTrigger.BatteryBelow => $"低于 {rule.Threshold:0}%",
            RuleTrigger.TemperatureAbove => $"高于 {rule.Threshold:0}℃",
            RuleTrigger.TemperatureBelow => $"低于 {rule.Threshold:0}℃",
            RuleTrigger.ProcessRunning => string.IsNullOrWhiteSpace(rule.ProcessName) ? "进程名未填" : rule.ProcessName!,
            _ => "—"
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>规则列表：目标性能模式 → 中文。</summary>
public sealed class TargetModeTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var mode = value switch
        {
            AutomationRule rule => rule.TargetMode,
            PerformanceMode direct => direct,
            _ => (PerformanceMode?)null
        };
        return mode switch
        {
            PerformanceMode.Eco => "节能",
            PerformanceMode.Balanced => "均衡",
            PerformanceMode.Performance => "高性能",
            PerformanceMode.Custom => "自定义",
            _ => "—"
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>规则列表：目标风扇模式 → 中文（null 表示不改变）。</summary>
public sealed class TargetFanModeTextConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        FanControlMode? mode = value switch
        {
            AutomationRule rule => rule.TargetFanMode,
            FanControlMode direct => direct,
            _ => null
        };
        return mode switch
        {
            FanControlMode.Auto => "自动",
            FanControlMode.Silent => "静音",
            FanControlMode.Performance => "性能",
            FanControlMode.Manual => "手动",
            FanControlMode.Curve => "曲线",
            _ => "不改变"
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
