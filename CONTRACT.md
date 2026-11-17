# PC Performance Center —— 开发契约（所有子 agent 必读，v2）

## 0. 本次实测的硬件事实（不要再猜、不要推翻）

- 机器：LENOVO XiaoXinPro 16 IRH8（小新 Pro 16 2023），Model 83AQ，序列号 PF4MJTGM
- BIOS：KZCN52WW（2026-04-29），主板 LENOVO LNVNB161216
- CPU：13th Gen Intel Core i5-13500H（12 核 16 线程）
- GPU：NVIDIA GeForce RTX 4050 Laptop GPU（独显）+ Intel Iris Xe（核显）
- 内存 32GB，SSD UMIS RPEYJ1T24MKN2QWY 1TB，电池 L22M4PF5
- OS：Windows 11 Pro for Workstations 10.0.26300

实测结论（决定性，写代码必须遵守）：

1. **LibreHardwareMonitor 0.9.4 + System.Management 9.0.0 可用**，内核驱动能加载（HVCI 未开启）。
   实测可读：CPU 每核温度 / Core Max / Core Average / CPU Package 温度、CPU 每核频率、CPU Package 功耗（W）、
   CPU 负载、内存已用/可用/占比、NVIDIA 温度/热点/功耗/频率/显存/负载、Intel 核显 D3D 传感器、
   SSD 温度与占用。**风扇 RPM 读不到**（LHM 无 Fan 传感器、无 EmbeddedController 硬件节点）。
2. **联想 GameZone ACPI WMI 是空壳**：root\\WMI 里 LENOVO_GAMEZONE_DATA（实例 ACPI\\PNP0C14\\GMZN_0）
   方法表存在（GetFanCount/GetFan1Speed/GetSmartFanMode/SetSmartFanMode/SetFanCooling ...），
   但任何调用都失败：Invoke-CimMethod 报 0x8004102F「无效的方法参数」；C# System.Management 调
   GetMethodParameters 返回 null、InvokeMethod 报「无效的对象」/「常规故障」。**因此本机无法通过 WMI 控制风扇或性能模式。**
   LENOVO_FAN_METHOD 只有 Fan_Get_Table / Fan_Set_Table，同样调不通。
3. 电源计划只有「平衡」381b4222-f694-41f0-9685-ff5bb260df2e。
4. MSAcpi_ThermalZoneTemperature：TZ00=27.9°C 可用，TZ01 无效（-273.2）。

结论：
- 监控：CPU/GPU/内存/SSD 全部真实可读；风扇 RPM 必须显示「不支持」并给出原因，不得编造。
- 性能调节：走 Windows 官方接口（powercfg 电源方案 + PROCTHROTTLEMAX 最大处理器状态 + PERFBOOSTMODE 睿频策略），真实可写；联想私有模式在本机不可用，代码里做适配器，运行时探测失败就禁用并显示原因。
- 风扇控制：本机不支持。代码必须实现「检测 → 不支持时明确说明 + 保留监控 → 判定为无效的滑块不得出现」，同时保留可扩展适配器（联想 WMI / 通用 EC 之外的官方接口），在别的机器上能真正工作。

## 1. 红线（违反即返工）

1. 禁止任何模拟/随机/占位数据。读不到就是「不支持」，写不出就是「失败 + 真实原因」。
2. 禁止伪造成功：OpResult.Ok 只能在系统调用真的返回成功后使用。
3. 禁止写 EC 寄存器、禁止改 BIOS、禁止绕过固件保护、禁止把风扇设到 0%。
4. 硬件控制一律通过：Windows 官方 API（powercfg / 电源覆盖 / 注册表电源设置）、System.Management(WMI)、
   LibreHardwareMonitorLib。除此之外不得引入新的第三方原生组件。
5. 不支持的接口要 catch 到并降级为只读监控，绝不能让程序崩溃或白屏。
6. 不修改不属于你负责的文件（见第 4 节所有权表）。不要改 PPC.Core（除非契约明确要求，且只能加不改）。
7. 全部面向对象/方法注释、UI 文案用中文；日志走 Ppc.Core.Services.AppLog.Instance。

## 2. 技术栈与结构

- .NET 8（net8.0-windows）、C# latest、nullable enable。
- 三个项目：src/PPC.Core（纯模型与接口，**已完成，不要动**）、src/PPC.Hardware（硬件实现）、src/PPC.App（WPF UI）。
- 命名空间：
  - `Ppc.Core.Models` / `Ppc.Core.Abstractions` / `Ppc.Core.Services`
  - `Ppc.Hardware.Monitoring` / `Ppc.Hardware.Detection` / `Ppc.Hardware.Power` / `Ppc.Hardware.Lenovo` / `Ppc.Hardware.Automation`
  - `Ppc.App` / `Ppc.App.ViewModels` / `Ppc.App.Views` / `Ppc.App.Controls` / `Ppc.App.Infrastructure`

## 3. 已经存在、可直接调用的 API

### Ppc.Core.Abstractions（接口，签名固定）
```csharp
public interface IMonitorService : IDisposable {
    bool IsRunning { get; }
    IReadOnlyList<string> ActiveBackends { get; }
    event EventHandler<HardwareSnapshot>? SnapshotReady;
    Task StartAsync(int intervalMilliseconds);
    Task StopAsync();
    Task<HardwareSnapshot> RefreshOnceAsync();
}
public interface IDeviceDetectionService { Task<DeviceProfile> DetectAsync(CancellationToken ct = default); }
public interface IPerformanceService {
    Task<PerformanceState> GetStateAsync();
    Task<OpResult> ApplyModeAsync(PerformanceMode mode, CustomPerfProfile custom);
    Task<OpResult> RestoreAsync();
    Task<bool> IsLenovoModeSupportedAsync();
}
public interface IFanControlService {
    bool SupportsDirectControl { get; }
    string SupportDetail { get; }
    Task<FanState> GetStateAsync();
    Task<OpResult> SetManualAsync(int percent);
    Task<OpResult> SetModeAsync(FanControlMode mode, FanCurve? curve, int manualPercent);
    Task<OpResult> ApplyCurveAsync(FanCurve curve, double currentTemperature);
    Task<OpResult> RestoreAutoAsync();
    event EventHandler<FanState>? StateChanged;
}
public interface IHistoryService {
    void Append(HardwareSnapshot snapshot);
    IReadOnlyList<(DateTimeOffset Stamp, double? Value)> GetSeries(Metric metric);
    void Clear();
}
```
含 `PerformanceState`（Mode/PowerSchemeName/PowerSchemeGuid/MaxProcessorState/MinProcessorState/BoostMode/WindowsPowerMode/LenovoThermalMode/Summary）、
`FanChannelState`（Index/Name/Rpm/MaxRpm/Percent）、`FanState`（Channels/Mode/ControlSource/CanControl/ControlUnavailableReason/ActiveCurve/AppliedPercent）。

### Ppc.Core.Models
- `Metric` 枚举：CpuTemperature, CpuUsage, CpuPower, CpuClock, CpuPackagePowerLimit, GpuTemperature, GpuUsage, GpuPower, GpuClock, GpuMemoryUsedPercent, MemoryUsedPercent/Bytes/Total/Available, DiskTemperature, DiskUsagePercent, Fan1Rpm..Fan4Rpm, BatteryPercent, BatteryRateWatts, AcOnline
- `MetricInfo`：Unit(Metric)/IsFan/IsTemperature/DisplayName
- `MetricReading(double? Value, string Source, string? UnavailableReason)`，HasValue，静态 Ok(value, source) / Unavailable(reason, source)
- `HardwareSnapshot`：Set(metric, value?, source) / SetReading / SetUnavailable / 索引器 this[Metric] / Get / Values / Fans(润余，可不用) / Timestamp
- `HistoryBuffer(capacity)`
- `OpResult`：静态 Ok(msg)、Fail(msg)、Unsupported(msg)，含 Success/Message
- `DeviceProfile`（Manufacturer/Model/SystemFamily/Sku/SerialNumber/BiosVersion/BiosReleaseDate/BoardVendor/BoardProduct/CpuName/CpuCores/CpuThreads/GpuDiscrete/GpuIntegrated/OsCaption/OsBuild/TotalMemoryBytes/Platform/LenovoGameZonePresent/LenovoFanTablePresent/IsElevated/Capabilities/DisplayName/TotalMemoryGb/Supports(key)）
- `CapabilityItem(string Key, string Title, bool Supported, string Detail)`
- `FanCurve`（Id/Name/Points(List<FanCurvePoint{Temperature,Percent}>)/HysteresisCelsius/ResponseDelaySeconds/Smoothing/SensorMetric；常量 MinSafePercent=20/MaxPercent=100/MinTemperature=30/MaxTemperature=100；Default()/Clone()/Normalized()/Evaluate(temp)→int）
- `AppSettings`（Theme/Language/RefreshIntervalSeconds/HistoryWindowSeconds/CurrentMode/CustomProfile/Fan/Automation/StartWithWindows/MinimizeToTray/RestoreOnExit/ShowUnavailableMetrics）、`PerformanceMode`{Eco,Balanced,Performance,Custom}、`FanControlMode`{Auto,Silent,Performance,Manual,Curve}、`CustomPerfProfile`、`FanSettings`、`AutomationRule`、`AutomationSettings`、`RuleTrigger`{AcConnected,AcDisconnected,BatteryBelow,ProcessRunning,TemperatureAbove,TemperatureBelow}

### Ppc.Core.Services
- `AppLog.Instance`：Info/Warn/Error(msg, ex?)/Tail(lines)/LogFilePath
- `SettingsStore`：ctor(string? directory = null)、Load()、Save(AppSettings)、ResetToDefaults()、Directory/FilePath/BackupPath
- `HistoryService : IHistoryService`：ctor(int capacity)、SetCapacity(int)
- `Guard.Safe(Func<string>, fallback)`

### Ppc.App.Infrastructure（已写好，直接用）
- `ObservableObject`：`protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)`、`protected void Raise(string name)`、`OnPropertyChanged`、`PropertyChanged`
- `RelayCommand`（`new RelayCommand(Action, Func<bool>? can = null)`，`RaiseCanExecuteChanged()`）、`AsyncRelayCommand`（`new AsyncRelayCommand(Func<Task>, Func<bool>? can = null)`）
- `IPageViewModel`：`string Key { get; } string Title { get; } string Icon { get; } string Subtitle { get; } Task ActivateAsync(); Task DeactivateAsync();`
- `MetricTile : ObservableObject`：`Metric Metric { get; init; }` `string Title` `string Value` `string Unit` `string Detail` `bool HasValue` `double Ratio`(0..1) `string State`("ok"/"warn"/"bad"/"na") `void Update(MetricReading)` — 无值时会显示「不支持」+原因
- `AppServices`（见第 5 节）

### Ppc.App.Controls（由 ui-theme 负责实现，页面直接用）
- `GaugeRing : FrameworkElement`，DP：`Value(double)` `Minimum` `Maximum` `Unit(string)` `Label(string)` `Accent(Brush)` `Track(Brush)` `Thickness(double)` `ValueText(string)`（空则自动格式化）
- `LevelBar : FrameworkElement`，DP：`Value(0..1 double)` `Fill(Brush)` `Track(Brush)` `Radius(double)`
- `ChartSeries : ObservableObject`：`string Label` `Brush Stroke` `IReadOnlyList<double?> Values` `IReadOnlyList<DateTimeOffset> Timestamps` `double? Min/Max` `void Invalidate()`
- `LineChart : FrameworkElement`，DP：`Series(IEnumerable<ChartSeries>)` `Unit(string)` `FixedMin/FixedMax(double?)` `ShowGrid(bool)` `ShowLegend(bool)` `AutoScale(bool)`；空数据显示「暂无历史数据」

## 4. 文件所有权（只写你名下的文件，覆盖同名存根）

| agent | 允许写的路径 |
|---|---|
| hardware-monitor | src/PPC.Hardware/Monitoring/** |
| detection | src/PPC.Hardware/Detection/** |
| power-control | src/PPC.Hardware/Power/**、src/PPC.Hardware/Automation/** |
| lenovo-fan | src/PPC.Hardware/Lenovo/** |
| ui-theme | src/PPC.App/Themes/*.xaml、src/PPC.App/Controls/** |
| ui-pages-a | src/PPC.App/Views/DashboardView.*、MonitoringView.*、SettingsView.*、src/PPC.App/ViewModels/DashboardViewModel.cs、MonitoringViewModel.cs、SettingsViewModel.cs |
| ui-pages-b | src/PPC.App/Views/PerformanceView.*、FanControlView.*、AutomationView.*、src/PPC.App/ViewModels/PerformanceViewModel.cs、FanControlViewModel.cs、AutomationViewModel.cs、src/PPC.App/Controls/FanCurveEditor.xaml(.cs) |
| app-infra | src/PPC.App/Infrastructure/**（除已存在的 ObservableObject.cs/RelayCommand.cs/IPageViewModel.cs/MetricTile.cs） |

MainWindow / App.xaml / AppServices / csproj / README 由 lead 负责，**不要改**。

## 5. AppServices（lead 已实现，页面 VM 统一 ctor 注入）

```csharp
namespace Ppc.App;
public sealed class AppServices : ObservableObject {
    public static AppServices Current { get; }
    public SettingsStore Store { get; }
    public AppSettings Settings { get; }
    public IMonitorService Monitor { get; }
    public IDeviceDetectionService Detection { get; }
    public IPerformanceService Performance { get; }
    public IFanControlService Fan { get; }
    public IHistoryService History { get; }
    public AutomationService Automation { get; }   // 见下
    public DeviceProfile Device { get; }
    public HardwareSnapshot Latest { get; }
    public bool IsElevated { get; }
    public string AppVersion { get; }
    public string StatusText { get; }
    public event EventHandler<HardwareSnapshot>? SnapshotUpdated;
    public event EventHandler<DeviceProfile>? ProfileUpdated;
    public Task SaveSettingsAsync();
    public Task RefreshOnceAsync();
    public void SetStatus(string text);
}
```
power-control 需要提供：
```csharp
namespace Ppc.Hardware.Power;
public sealed class PerformanceService : IPerformanceService {
    public PerformanceService(SettingsStore store);   // 自存 power-snapshot.json 在 store.Directory
}
namespace Ppc.Hardware.Automation;
public sealed class AutomationService : IDisposable {
    public AutomationService(AppSettings settings, SettingsStore store, IPerformanceService perf, IFanControlService fan, IMonitorService monitor);
    public bool IsEnabled { get; set; }
    public event EventHandler<string>? StatusChanged;   // 人类可读的触发说明
    public void Start(); public void Stop();
    public Task EvaluateAsync(HardwareSnapshot snapshot);
    public IReadOnlyList<AutomationRule> Rules { get; }
}
```
其它硬件实现 ctor：
- `Ppc.Hardware.Monitoring.LhmMonitorService()`
- `Ppc.Hardware.Detection.DeviceDetectionService(IMonitorService monitor)`
- `Ppc.Hardware.Lenovo.LenovoFanControlService(IMonitorService monitor)`（内部封装 GameZone/FanTable 适配器，探测失败则 SupportsDirectControl=false）

## 6. UI 设计系统（ui-theme 提供，页面必须用这些键）

颜色/画刷（SolidColorBrush 键，深浅主题各一份）：
`Bg.Window` `Bg.Surface` `Bg.SurfaceAlt` `Bg.Hover` `Bg.Selected` `Border.Subtle` `Border.Strong`
`Text.Primary` `Text.Secondary` `Text.Tertiary`
`Accent.Primary` `Accent.Soft` `Accent.GradientStart` `Accent.GradientEnd`
`State.Good` `State.Warn` `State.Bad` `State.Info` `State.Muted`

文本样式：`PageTitleText` `SectionTitleText` `CardTitleText` `MetricValueText` `MetricUnitText` `MetricLabelText` `BodyText` `HintText` `NavItemText`
容器/控件样式：`CardStyle`(Border) `CardHoverStyle` `NavItemStyle`(RadioButton) `PrimaryButton` `SecondaryButton` `GhostButton` `IconButton` `ModernSlider` `ModernToggle`(CheckBox) `ModernComboBox` `ModernTextBox` `ChipBorder` `ChipText`
转换器键：`BoolToVisibility` `InverseBoolToVisibility` `EnumToBool`(ConverterParameter=枚举名) `NullToVisibility` `InverseBool`

所有页面：根节点 `<UserControl>`，最外层 `ScrollViewer VerticalScrollBarVisibility="Auto"` + `Grid`，卡片用 `Style="{DynamicResource CardStyle}"`。
页面不得写死颜色（一律 DynamicResource），不得用表格布局堆参数。

## 7. 各页面 VM 精确契约（属性名必须一字不差，XAML 直接绑）

### DashboardViewModel（ui-pages-a）
```
MetricTile CpuTemp, CpuUsage, CpuPower, CpuClock
MetricTile GpuTemp, GpuUsage, GpuPower, GpuClock
MetricTile MemoryTile, DiskTile
ObservableCollection<MetricTile> FanTiles
ObservableCollection<ChartSeries> TemperatureSeries     // CPU 温度 + GPU 温度两条
string DeviceTitle, DeviceSubtitle, PlatformText        // 例 "LENOVO 小新 Pro 16 2023"
string ModeTitle, ModeDescription                       // 当前性能模式
string PowerSourceText, BatteryText
string MonitoringIssue                                  // 监控不可用原因，空串=正常
bool HasMonitoringIssue
RelayCommand RefreshCommand
```
### PerformanceViewModel（ui-pages-b）
```
PerformanceMode SelectedMode                                // 绑定四张模式卡
ObservableCollection<PerfOptionViewModel> Options           // 每项：Title/Description/Supported/ValueText/IsAdjustable
int MaxProcessorState, MinProcessorState                    // 滑块 5..100
int SelectedBoostMode                                       // 0=禁用 1=启用 2=高效(厂商定义)
ObservableCollection<BoostOptionViewModel> BoostOptions     // Name/Value
string CurrentSummary, PowerPlanText, ApplyMessage
bool CanAdjust, IsBusy
RelayCommand ApplyCommand, RestoreDefaultsCommand, SelectModeCommand(参数=模式名), RefreshCommand
```
（PerfOptionViewModel / BoostOptionViewModel 也放 PerformanceViewModel.cs 里，public）

### FanControlViewModel（ui-pages-b）
```
ObservableCollection<MetricTile> FanTiles
bool SupportsControl
string SupportDetail                                       // 不支持时给用户看的原因
ObservableCollection<FanModeOption> ModeOptions            // Mode/Name/Description/Enabled
FanControlMode SelectedMode
int ManualPercent                                          // 0..100，真控制时按硬件范围夹紧
ObservableCollection<FanCurve> Curves
FanCurve? ActiveCurve
int HysteresisCelsius, ResponseDelaySeconds
bool Smoothing
string ApplyMessage, ControlSourceText
int? AppliedPercent
bool IsBusy
RelayCommand ApplyModeCommand, RestoreAutoCommand, ApplyCurveCommand, NewCurveCommand, DeleteCurveCommand, SaveCurveCommand
```
FanModeOption：`FanControlMode Mode; string Name; string Description; bool Enabled; string DisabledReason;`
### MonitoringViewModel（ui-pages-a）
```
ObservableCollection<MetricTile> AllMetrics                // 全部 Metric 一行一个
ObservableCollection<ChartSeries> CpuSeries, GpuSeries     // 温度+使用率+功耗
string SamplingText, BackendsText
int RefreshIntervalSeconds                                 // 1/2/5 三档下拉
RelayCommand RefreshCommand, ClearHistoryCommand
```
### AutomationViewModel（ui-pages-b）
```
bool Enabled, ThermalGuardEnabled
int ThermalGuardCelsius
ObservableCollection<AutomationRule> Rules
AutomationRule? SelectedRule
string StatusText
ObservableCollection<string> TriggerOptions, ModeOptions   // 中文名
RelayCommand AddRuleCommand, DeleteRuleCommand, SaveCommand, OpenProcessPickerCommand
```
### SettingsViewModel（ui-pages-a）
```
string Theme, Language, AppVersion, DataDirectory, LogPath, DeviceSummary, BiosText, CapabilitiesText
bool StartWithWindows, MinimizeToTray, RestoreOnExit, ShowUnavailableMetrics
int HistoryWindowSeconds
ObservableCollection<CapabilityItem> Capabilities
string SaveMessage
RelayCommand SaveCommand, ResetSettingsCommand, OpenLogCommand, RestartElevatedCommand, RestoreDefaultsCommand
```

## 8. 编译自检

- 硬件类：`dotnet build src/PPC.Hardware/PPC.Hardware.csproj -c Release`，必须 0 error 才能交付。
- UI 类：`dotnet build src/PPC.App/PPC.App.csproj -c Release`；因并行开发可能因**别人的文件缺失**报错。
  只修你自己文件里的错误：其他文件报错不要改，报告中列出即可。
- 禁止把 `TODO`/`NotImplementedException` 留在交付代码里。
- 结束后报告：写了哪些文件、哪些真实 API、哪些功能在本机不支持及原因、未验证项。
