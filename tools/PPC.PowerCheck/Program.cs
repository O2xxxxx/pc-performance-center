using Ppc.Core.Models;
using Ppc.Core.Services;
using Ppc.Hardware.Power;

// 真实写入验证：切换性能模式 -> 回读 -> 切节能 -> 回读 -> 恢复原始快照 -> 回读。
// 只动当前电源方案的处理器设置，测试结束调用 RestoreAsync 还原。
var dir = Path.Combine(Path.GetTempPath(), "ppc-powercheck-" + Guid.NewGuid().ToString("N")[..6]);
var store = new SettingsStore(dir);
var perf = new PerformanceService(store);

async Task Dump(string tag)
{
    var s = await perf.GetStateAsync();
    Console.WriteLine($"[{tag}] Mode={s.Mode} Scheme={s.PowerSchemeName} Max={s.MaxProcessorState?.ToString() ?? "-"} Min={s.MinProcessorState?.ToString() ?? "-"} Boost={s.BoostMode?.ToString() ?? "-"} Win={s.WindowsPowerMode}");
}

await Dump("initial");

var r1 = await perf.ApplyModeAsync(PerformanceMode.Performance, new CustomPerfProfile());
Console.WriteLine($"apply Performance => Success={r1.Success} | {r1.Message}");
await Dump("after-performance");

var r2 = await perf.ApplyModeAsync(PerformanceMode.Eco, new CustomPerfProfile());
Console.WriteLine($"apply PowerSaving => Success={r2.Success} | {r2.Message}");
await Dump("after-powersaving");

var r3 = await perf.ApplyModeAsync(PerformanceMode.Balanced, new CustomPerfProfile());
Console.WriteLine($"apply Balanced => Success={r3.Success} | {r3.Message}");
await Dump("after-balanced");

// --force-default-boost：把快照里的睿频值改成「未设置」，强制走「无法删除覆盖值 → 写回方案默认值」的兜底分支
if (args.Contains("--force-default-boost"))
{
    var snapPath = Path.Combine(dir, "power-snapshot.json");
    var json = File.ReadAllText(snapPath);
    Console.WriteLine("snapshot(before): " + json);
    json = System.Text.RegularExpressions.Regex.Replace(json, @"\s*""(boostModeAc|boostModeDc)""\s*:\s*-?\d+,?", "",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    json = System.Text.RegularExpressions.Regex.Replace(json, @",\s*}", "\n}");
    File.WriteAllText(snapPath, json);
    Console.WriteLine("snapshot(after): " + json);
}

var r4 = await perf.RestoreAsync();
Console.WriteLine($"restore => Success={r4.Success} | {r4.Message}");
await Dump("after-restore");
