using Ppc.Core.Models;

namespace Ppc.Core.Abstractions;

public interface IMonitorService : IDisposable
{
    bool IsRunning { get; }
    IReadOnlyList<string> ActiveBackends { get; }
    event EventHandler<HardwareSnapshot>? SnapshotReady;
    Task StartAsync(int intervalMilliseconds);
    Task StopAsync();
    Task<HardwareSnapshot> RefreshOnceAsync();
}

public interface IDeviceDetectionService
{
    Task<DeviceProfile> DetectAsync(CancellationToken cancellationToken = default);
}

public sealed class PerformanceState
{
    public PerformanceMode Mode { get; set; } = PerformanceMode.Balanced;
    public string PowerSchemeName { get; set; } = "";
    public Guid? PowerSchemeGuid { get; set; }
    public int? MaxProcessorState { get; set; }
    public int? MinProcessorState { get; set; }
    public int? BoostMode { get; set; }
    public string WindowsPowerMode { get; set; } = "";
    public string LenovoThermalMode { get; set; } = "";
    public string Summary { get; set; } = "";
}

public interface IPerformanceService
{
    Task<PerformanceState> GetStateAsync();
    Task<OpResult> ApplyModeAsync(PerformanceMode mode, CustomPerfProfile custom);
    Task<OpResult> RestoreAsync();
    Task<bool> IsLenovoModeSupportedAsync();
}

public sealed class FanChannelState
{
    public int Index { get; set; }
    public string Name { get; set; } = "";
    public int? Rpm { get; set; }
    public int? MaxRpm { get; set; }
    public int? Percent { get; set; }
}

public sealed class FanState
{
    public List<FanChannelState> Channels { get; set; } = new();
    public FanControlMode Mode { get; set; } = FanControlMode.Auto;
    public string ControlSource { get; set; } = "厂商自动控制";
    public bool CanControl { get; set; }
    public string? ControlUnavailableReason { get; set; }
    public FanCurve? ActiveCurve { get; set; }
    public int? AppliedPercent { get; set; }
}

public interface IFanControlService
{
    bool SupportsDirectControl { get; }
    string SupportDetail { get; }
    Task<FanState> GetStateAsync();
    Task<OpResult> SetManualAsync(int percent);
    Task<OpResult> SetModeAsync(FanControlMode mode, FanCurve? curve, int manualPercent);
    Task<OpResult> ApplyCurveAsync(FanCurve curve, double currentTemperature);
    Task<OpResult> RestoreAutoAsync();
    event EventHandler<FanState>? StateChanged;
}

public interface IHistoryService
{
    void Append(HardwareSnapshot snapshot);
    IReadOnlyList<(DateTimeOffset Stamp, double? Value)> GetSeries(Metric metric);
    void Clear();
}
