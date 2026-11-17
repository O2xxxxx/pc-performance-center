using System.Text;

namespace Ppc.Core.Services;

/// <summary>Minimal rolling file logger. Never throws: logging must not take the app down.</summary>
public sealed class AppLog
{
    private static readonly Lazy<AppLog> _instance = new(() => new AppLog());
    public static AppLog Instance => _instance.Value;

    private readonly object _gate = new();
    private readonly string _path;

    private AppLog()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PCPerformanceCenter", "logs");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, $"app-{DateTime.Now:yyyyMMdd}.log");
    }

    public string LogFilePath => _path;

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);

    public void Error(string message, Exception? ex = null)
        => Write("ERROR", ex is null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    private void Write(string level, string message)
    {
        try
        {
            var line = $"{DateTime.Now:HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
            lock (_gate)
            {
                File.AppendAllText(_path, line, Encoding.UTF8);
            }
        }
        catch
        {
            // ignored on purpose
        }
    }

    public string Tail(int lines = 200)
    {
        try
        {
            if (!File.Exists(_path)) return "（暂无日志）";
            var all = File.ReadAllLines(_path);
            return string.Join(Environment.NewLine, all.Skip(Math.Max(0, all.Length - lines)));
        }
        catch (Exception ex)
        {
            return "读取日志失败: " + ex.Message;
        }
    }
}
