using Roshiq.Core.Settings;

namespace Roshiq.Core.Diagnostics;

/// <summary>Простой потокобезопасный файловый лог. Никогда не бросает исключений.</summary>
public static class Log
{
    private static readonly object Sync = new();
    private static readonly List<string> Recent = new();
    public static bool FileEnabled { get; set; } = true;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    public static IReadOnlyList<string> RecentLines
    {
        get { lock (Sync) return Recent.ToArray(); }
    }

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
        lock (Sync)
        {
            Recent.Add(line);
            if (Recent.Count > 200) Recent.RemoveAt(0);
            if (!FileEnabled) return;
            try
            {
                Directory.CreateDirectory(AppPaths.Logs);
                File.AppendAllText(Path.Combine(AppPaths.Logs, $"roshiq-{DateTime.Now:yyyyMMdd}.log"), line + Environment.NewLine);
            }
            catch
            {
                // лог не должен ломать приложение
            }
        }
    }
}

/// <summary>Журнал всех сетевых обращений (требование "все сетевые обращения документированы").</summary>
public static class NetworkLog
{
    private static readonly object Sync = new();
    private static readonly List<string> Entries = new();

    public static void Record(string method, string url, string result)
    {
        var line = $"{DateTime.Now:HH:mm:ss} {method} {url} -> {result}";
        lock (Sync)
        {
            Entries.Add(line);
            if (Entries.Count > 100) Entries.RemoveAt(0);
        }
        Log.Info("NET " + line);
    }

    public static IReadOnlyList<string> All
    {
        get { lock (Sync) return Entries.ToArray(); }
    }
}

/// <summary>Абстракция времени для тестируемости.</summary>
public interface IClock { DateTime UtcNow { get; } }

public sealed class SystemClock : IClock
{
    public static readonly SystemClock Instance = new();
    public DateTime UtcNow => DateTime.UtcNow;
}
