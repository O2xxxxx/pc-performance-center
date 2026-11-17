namespace Ppc.Core.Models;

public enum PerformanceMode { Eco = 0, Balanced = 1, Performance = 2, Custom = 3 }

public enum FanControlMode { Auto = 0, Silent = 1, Performance = 2, Manual = 3, Curve = 4 }

public sealed class CustomPerfProfile
{
    public int? MaxProcessorStatePercent { get; set; } = 100;
    public int? MinProcessorStatePercent { get; set; } = 5;
    public int? BoostMode { get; set; }
    public int? CpuPowerLimitWatts { get; set; }
    public bool UseLenovoPerformanceMode { get; set; } = true;
}

public sealed class FanSettings
{
    public FanControlMode Mode { get; set; } = FanControlMode.Auto;
    public int ManualPercent { get; set; } = 50;
    public string? ActiveCurveId { get; set; }
    public List<FanCurve> Curves { get; set; } = new() { FanCurve.Default() };
    public bool UseVendorFanTable { get; set; } = true;

    public FanCurve? ActiveCurve => Curves.FirstOrDefault(c => c.Id == ActiveCurveId) ?? Curves.FirstOrDefault();
}

public enum RuleTrigger
{
    AcConnected,
    AcDisconnected,
    BatteryBelow,
    ProcessRunning,
    TemperatureAbove,
    TemperatureBelow,
}

public sealed class AutomationRule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "新规则";
    public bool Enabled { get; set; } = true;
    public int Priority { get; set; } = 10;
    public RuleTrigger Trigger { get; set; } = RuleTrigger.AcConnected;
    public double Threshold { get; set; } = 80;
    public string? ProcessName { get; set; }
    public PerformanceMode TargetMode { get; set; } = PerformanceMode.Performance;
    public FanControlMode? TargetFanMode { get; set; }
    public bool RestorePrevious { get; set; } = true;
    public int CooldownSeconds { get; set; } = 60;
}

public sealed class AutomationSettings
{
    public bool Enabled { get; set; }
    public bool ThermalGuardEnabled { get; set; } = true;
    public int ThermalGuardCelsius { get; set; } = 95;
    public List<AutomationRule> Rules { get; set; } = new();
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public string Theme { get; set; } = "Dark";
    public string Language { get; set; } = "zh-CN";
    public int RefreshIntervalSeconds { get; set; } = 2;
    public int HistoryWindowSeconds { get; set; } = 300;
    public PerformanceMode CurrentMode { get; set; } = PerformanceMode.Balanced;
    public CustomPerfProfile CustomProfile { get; set; } = new();
    public FanSettings Fan { get; set; } = new();
    public AutomationSettings Automation { get; set; } = new();
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool RestoreOnExit { get; set; } = true;
    public bool ShowUnavailableMetrics { get; set; } = true;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();
}
