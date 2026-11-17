using System.Text.Json;
using System.Text.Json.Serialization;
using Ppc.Core.Models;

namespace Ppc.Core.Services;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public SettingsStore(string? directory = null)
    {
        Directory = directory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCPerformanceCenter");
        FilePath = Path.Combine(Directory, "settings.json");
        BackupPath = Path.Combine(Directory, "settings.backup.json");
    }

    public string Directory { get; }
    public string FilePath { get; }
    public string BackupPath { get; }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return CreateDefault();

            var json = File.ReadAllText(FilePath);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, Options);
            if (settings is null) return CreateDefault();
            ApplyDefaults(settings);
            return settings;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("读取设置失败，使用默认设置", ex);
            return CreateDefault();
        }
    }

    public bool Save(AppSettings settings)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            var json = JsonSerializer.Serialize(settings, Options);
            if (File.Exists(FilePath)) File.Copy(FilePath, BackupPath, true);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, FilePath, true);
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("保存设置失败", ex);
            return false;
        }
    }

    public void ResetToDefaults()
    {
        try
        {
            if (File.Exists(FilePath)) File.Copy(FilePath, BackupPath, true);
            File.Delete(FilePath);
        }
        catch (Exception ex)
        {
            AppLog.Instance.Error("重置设置失败", ex);
        }
    }

    private static AppSettings CreateDefault() => new();

    private static void ApplyDefaults(AppSettings settings)
    {
        settings.CustomProfile ??= new CustomPerfProfile();
        settings.Fan ??= new FanSettings();
        settings.Automation ??= new AutomationSettings();
        if (settings.Fan.Curves.Count == 0) settings.Fan.Curves.Add(FanCurve.Default());
        settings.RefreshIntervalSeconds = Math.Clamp(settings.RefreshIntervalSeconds, 1, 10);
        settings.HistoryWindowSeconds = Math.Clamp(settings.HistoryWindowSeconds, 60, 3600);
    }
}
