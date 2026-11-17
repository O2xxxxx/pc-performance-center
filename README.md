# PC Performance Center（电脑性能与散热控制中心）

面向 Windows 11 笔记本的现代化性能调节与硬件监控工具。C# + WPF + .NET 8，中文界面，深/浅色主题，**macOS 风格视觉语言**：左上红黄绿窗口按钮、半透明感侧栏、系统蓝胶囊选中态、大标题层级、30px 控件高度与 42×24 开关。

> 设计原则只有一条：**界面上出现的每个数字都来自真实传感器，每个按钮背后都有真实的系统操作。**
> 读不到的传感器显示「不支持」并给出原因，不支持的控制功能直接禁用并解释原因，绝不用模拟数据凑界面。

---

## 〇、界面风格（macOS）

- **窗口**：无系统标题栏，左上角红/黄/绿三个 12px 圆点（关闭=最小化到托盘、最小化、最大化），Windows 11 下通过 `DwmSetWindowAttribute(DWMWA_WINDOW_CORNER_PREFERENCE)` 启用圆角。
- **侧栏**：236px 深/浅分离的侧栏底色，导航行高 34、圆角 8，选中项是系统蓝实心胶囊 + 白字。
- **强调色**：深色 `#0A84FF`、浅色 `#007AFF`（macOS 系统蓝），状态色沿用系统色板（绿 `#32D74B`、黄 `#FFD60A`、红 `#FF453A`）。
- **字体层级**：大标题 26px SemiBold、区块标题 14px、指标数值 30px、正文 12.5px，字体栈 `Segoe UI Variable → 微软雅黑`。
- **控件**：按钮高 30 圆角 7；开关 42×24 白色圆钮；滑块 4px 细轨 + 18px 白钮带投影；下拉框/输入框高 30；滚动条 9px 细条。
- 六页截图见 `docs/screenshots/`，深色为默认，设置页可切换浅色。

## 一、这台电脑的真实支持情况（实测，非推测）

测试机型：**LENOVO XiaoXinPro 16 IRH8（小新 Pro 16 2023）**
- CPU：13th Gen Intel Core i5-13500H（12 核 16 线程）
- GPU：NVIDIA GeForce RTX 4050 Laptop GPU + Intel Iris Xe
- 内存 32GB LPDDR5，SSD 1TB（UMIS RPEYJ1T24MKN2QWY），电池 L22M4PF5
- BIOS：KZCN52WW，主板 LENOVO LNVNB161216，机型代码 83AQ
- 系统：Windows 11 Pro for Workstations 10.0.26300

| 功能 | 状态 | 说明 |
|---|---|---|
| CPU 温度（每核 / Package / Core Max） | ✅ 真实可用 | LibreHardwareMonitor，读的是 Intel MSR 温度 |
| CPU 频率 / 使用率 / Package 功耗 | ✅ 真实可用 | LHM + MSR |
| GPU 温度 / 热点 / 频率 / 功耗 / 显存 / 负载 | ✅ 真实可用 | NVIDIA NVML 通道 |
| 内存 / 磁盘温度 / 磁盘占用 | ✅ 真实可用 | LHM |
| 电池电量、是否接电源 | ✅ 真实可用 | GetSystemPowerStatus |
| **风扇转速（RPM）** | ❌ 不支持 | 本机 ACPI/WMI 未向第三方暴露风扇转速传感器，LHM 也读不到 EC 风扇通道。界面明确显示「不支持」并说明原因 |
| **风扇转速控制（手动/曲线）** | ❌ 不支持 | 检测到 `LENOVO_GAMEZONE_DATA` / `LENOVO_FAN_METHOD` 类与方法表，但所有调用都被固件拒绝（`Invoke-CimMethod` 报 0x8004102F；System.Management 报「无效的对象」「常规故障」）。软件因此禁用滑块与曲线应用，只保留监控与说明 |
| Windows 电源方案切换 | ✅ 真实可用 | `powercfg` 官方接口 |
| CPU 最大/最小处理器状态（PROCTHROTTLEMAX/MIN） | ✅ 真实可用 | `powercfg -set{ac,dc}valueindex`，改完自动 `-setactive` 生效 |
| 睿频策略（PERFBOOSTMODE） | ✅ 真实可用 | 同上，Intel 平台原生暴露该设置项 |
| 联想性能模式（Fn+Q 那一套） | ❌ 不支持 | 本机 GameZone WMI 是空壳；代码保留了适配器，在真正支持的机型上会自动启用 |
| CPU 功耗墙（PL1/PL2） | ❌ 不支持 | 需要厂商接口或 `Intel XTU` 级别驱动，Windows 没有官方 API。代码里不做未经验证的 MSR 写入 |
| GPU 性能策略 | ❌ 不支持 | 需要 NVIDIA/AMD 官方 SDK，未集成 |

> 换一台机器会怎样？软件启动时自动重新检测：能力矩阵里每一项都会变成该机器真实的状态。
> 联想 Legion 系列、部分微星/华硕机型若开放了 WMI 风扇接口，风扇页会自动从"只读"切换为"可控"。

---

## 二、功能

**首页仪表盘**：CPU/GPU 温度、功耗、使用率、频率，内存，风扇，当前性能模式，供电状态，温度趋势图。

**性能调节**：节能 / 均衡 / 高性能 / 自定义四档预设。自定义可分别设置最大/最小处理器状态（5–100%）与睿频策略；应用后立刻把 `powercfg` 的真实返回结果显示出来。退出时可一键恢复出厂电源策略（首次修改前会先把原始值快照到 `power-snapshot.json`）。

**风扇控制**：自动 / 静音 / 性能 / 手动 / 自定义曲线五种模式；曲线编辑器支持拖拽节点、增删节点、多个曲线存档、回滞温度、响应延迟、平滑处理，以及"一键恢复厂商自动"。**当前机器不支持直接控制，因此这些控件会被明确禁用并解释原因，不做假按钮。**

**硬件监控**：全部传感器一览（含来源与不可用原因）、三张历史曲线（温度/使用率/功耗）、刷新间隔 1/2/5 秒可调、清空历史。

**自动化策略**：接电源/用电池、电量低于阈值、指定进程启动、温度高于/低于阈值 → 自动切换性能模式或风扇模式，支持优先级、冷却时间、条件消失后恢复原模式，另有温度保护（超过阈值强制降档）。

**系统托盘**：最小化到托盘、托盘菜单快速切模式、退出前恢复系统原始策略、可选开机启动。

---

## 三、安全设计

1. **不碰危险的东西**：不写 EC 寄存器、不改 BIOS、不绕过固件保护、不调用任何未经验证的 MSR 写入。
2. **风扇永远有下限**：曲线与手动百分比夹在 20%–100%（`FanCurve.MinSafePercent`），不会把风扇停转。
3. **控制前先探测**：所有控制接口都是"先只读探测 → 探测通过才允许写"，探测失败即降级为只读监控。
4. **可恢复**：修改电源策略前快照原始值；退出/异常/托盘退出都会尝试恢复；风扇支持控制的机型在退出时恢复厂商自动档。
5. **崩溃隔离**：所有硬件调用都有 try/catch，单次采样失败不会中断监控，UI 线程异常会被外层捕获并记录，不闪退。
6. **权限最小化**：默认以普通用户启动；需要底层访问时在设置页提供"以管理员身份重启"（UAC 由系统接管）。

---

## 四、项目结构

```
PC-Performance-Center/
├─ PCPerformanceCenter.sln
├─ build-release.ps1              # 一键打包
├─ CONTRACT.md                    # 开发契约（能力矩阵、红线、模块边界）
├─ src/
│  ├─ PPC.Core/                   # 纯模型与接口（无 UI、无硬件依赖）
│  │  ├─ Models/                  # Metric / HardwareSnapshot / FanCurve / AppSettings / DeviceProfile
│  │  ├─ Abstractions/            # IMonitorService / IPerformanceService / IFanControlService ...
│  │  └─ Services/                # AppLog / SettingsStore / HistoryService
│  ├─ PPC.Hardware/               # 硬件实现，可替换、可降级
│  │  ├─ Monitoring/              # LibreHardwareMonitor 采样服务
│  │  ├─ Detection/               # WMI / SMBIOS 设备检测与能力矩阵
│  │  ├─ Power/                   # powercfg 电源方案与处理器策略
│  │  ├─ Lenovo/                  # 联想 GameZone / FanTable 适配器（探测失败自动禁用）
│  │  └─ Automation/              # 自动化规则引擎
│  └─ PPC.App/                    # WPF 界面
│     ├─ Views/                   # 六个页面
│     ├─ ViewModels/              # 页面视图模型
│     ├─ Controls/                # 环形仪表、折线图、曲线编辑器
│     ├─ Themes/                  # 深色 / 浅色设计系统
│     └─ Infrastructure/          # 主题、托盘、开机启动、提权、MVVM 基类
└─ tools/PPC.PowerCheck/          # 电源写入真机验证程序（真的改电源策略再恢复）
└─ docs/screenshots/              # 实际运行截图（本机拍摄）
```

---

## 五、编译与打包

需要 .NET 8 SDK（`winget install Microsoft.DotNet.SDK.8`）。

```powershell
# 编译（Release）
dotnet build PCPerformanceCenter.sln -c Release

# 直接运行
dotnet run --project src/PPC.App/PPC.App.csproj -c Release

# 打包成独立 EXE（自包含单文件，目标机器无需安装 .NET）
powershell -ExecutionPolicy Bypass -File .\build-release.ps1

# 依赖框架的轻量包（目标机器已装 .NET 8 桌面运行时）
powershell -ExecutionPolicy Bypass -File .\build-release.ps1 -FrameworkDependent
```

产物：`artifacts\PCPerformanceCenter\PCPerformanceCenter.exe` 与 `artifacts\PCPerformanceCenter-win-x64.zip`（本机实测 67 MB，双击即用）。已构建好的成品在 `artifacts\publish\PCPerformanceCenter.exe` 与 `artifacts\PCPerformanceCenter-win-x64.zip`。

开发期自检：`PCPerformanceCenter.exe --capture out\` 会把每个页面渲染成 PNG，用来验证界面没崩。

---

## 六、常见问题

**Q：CPU/GPU 数据全是「不支持」？**
A：先确认以管理员身份运行（LibreHardwareMonitor 需要加载内核驱动）。若仍为空，看设置页的"能力矩阵"和日志（`%LOCALAPPDATA%\PCPerformanceCenter\logs`）。

**Q：为什么风扇页没有滑块？**
A：因为这台机器的固件不允许第三方控制风扇。软件宁可禁用，也不会给一个按下去没反应的滑块。原因在风扇页顶部写得很清楚。

**Q：切了性能模式，为什么温度还是很高？**
A：Windows 电源策略只影响处理器的调度与频率上限，不改变厂商的散热策略和功耗墙。真正的功耗墙（PL1/PL2）在本机没有官方接口，软件不会去写。

**Q：会不会影响保修 / 伤硬件？**
A：所有调节都是 Windows 官方提供的电源策略，不涉及固件与 EC 写入；退出时可恢复原始设置。

---

## 七、明确标注为「未验证」的部分

- 联想 GameZone WMI 控制路径：代码完整实现并做了只读探测，但**本机调用必失败**，因此该路径**未在真实可用的机型上验证过**。
- 风扇曲线回滞/延迟逻辑：逻辑已实现并做了边界夹紧，但本机无法驱动风扇，**闭环效果未验证**。
- 非联想品牌机型的适配：能力矩阵会自动检测，但未在其它机型上实测。

---

## 八、实测验收记录（本机真实运行，2026-11-15）

**编译**：`dotnet build PCPerformanceCenter.sln -c Release` → 0 错误。单文件自包含发布 → `artifacts\publish\PCPerformanceCenter.exe`（67 MB），双击直接运行，目标机不需要装 .NET。

**界面**：六个页面全部真实渲染成功，截图见 `docs/screenshots/`。首页真实负载读数：CPU 60.0 ℃ / 11.7 W / 38.5% / 1494 MHz，GPU 52.0 ℃ / 10.2 W / 37.0% / 225 MHz，内存 63.2%，SSD 32 ℃。风扇四路显示「不支持 / 本机未提供风扇转速传感器」。

**电源写入实测**（`tools/PPC.PowerCheck` 真机执行，不是模拟）：

| 步骤 | 返回 | 回读值 |
|---|---|---|
| 初始状态 | — | Max=100 / Min=5 / 睿频=未设置（等效 2）/ Windows 覆盖=最佳能效 |
| 应用「高性能」 | Success=True | 睿频=2、Windows 覆盖=最佳性能 |
| 应用「节能」 | Success=True | 电源方案=节能、Max=60、睿频=0 |
| 应用「均衡」 | Success=True | Max=100、睿频=1 |
| 恢复默认 | Success=True | 睿频=2（本机该方案的固件默认值） |

> 恢复时踩到一个真问题并修掉：未提权时删不掉注册表里的睿频覆盖值（Requested registry access is not allowed.），会让睿频卡在被改状态。现在改成「删不掉就按 PowerSettings DefaultPowerSchemeValues 里的方案默认值写回」，重测恢复成功。

**能力矩阵**：监控启动后按实时传感器重算 → 8 / 11 项可用（CPU / GPU / 内存 / 硬盘监控、Windows 电源方案、处理器性能状态、睿频策略等）。风扇转速监控、风扇控制、GPU 性能策略三项明确判为不可用并给出原因，对应滑块与曲线应用按钮为禁用状态。

**原生保护没动过**：全程没有写 EC、没有改 BIOS、没有碰任何固件参数；提速降速全部走 Windows powercfg 官方设置项。
