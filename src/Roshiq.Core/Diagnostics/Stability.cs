using System.Globalization;
using System.Text;

namespace Roshiq.Core.Diagnostics;

/// <summary>Состояние запуска: сколько предыдущих запусков подряд закончились сбоем и нужен ли безопасный режим.</summary>
public sealed record StartupState(int FailedStarts, bool SafeMode);

/// <summary>
/// Защита от «падает при каждом запуске». Перед запуском счётчик увеличивается, после минуты нормальной работы
/// или при обычном выходе — обнуляется. Если два запуска подряд не дожили до этого — третий идёт в безопасном режиме.
/// </summary>
public static class CrashGuard
{
    public const int SafeModeAfter = 2;

    public static StartupState Begin(string path, bool forceSafe = false)
    {
        int failed = Read(path);
        Write(path, failed + 1);
        return new StartupState(failed, forceSafe || failed >= SafeModeAfter);
    }

    /// <summary>Программа работает нормально (минута без сбоя или обычный выход).</summary>
    public static void MarkStable(string path) => Write(path, 0);

    public static int Read(string path)
    {
        try
        {
            return File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
                ? Math.Clamp(n, 0, 100) : 0;
        }
        catch { return 0; }
    }

    private static void Write(string path, int value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, value.ToString(CultureInfo.InvariantCulture));
        }
        catch { }
    }
}

/// <summary>Отчёт о сбое: понятный текстовый файл, который можно скопировать и отправить разработчику.</summary>
public static class CrashReport
{
    public const string Prefix = "crash-";
    /// <summary>Некритичные ошибки интерфейса (программа продолжила работу).</summary>
    public const string ErrorPrefix = "error-";
    private const string SeenFile = "crash-seen.txt";

    public static string Build(Exception ex, string appVersion, string where, IEnumerable<string> recentLog, DateTime now)
    {
        var sb = new StringBuilder();
        sb.AppendLine("ROSHIQ — отчёт о сбое");
        sb.AppendLine($"Версия: {appVersion}");
        sb.AppendLine($"Время: {now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Windows: {Environment.OSVersion.VersionString}; .NET {Environment.Version}; {(Environment.Is64BitProcess ? "x64" : "x86")}");
        sb.AppendLine($"Где: {where}");
        sb.AppendLine();
        sb.AppendLine("Ошибка:");
        for (var e = ex; e != null; e = e.InnerException)
        {
            sb.AppendLine($"{e.GetType().FullName}: {e.Message}");
            if (!string.IsNullOrWhiteSpace(e.StackTrace)) sb.AppendLine(e.StackTrace);
            if (e.InnerException != null) sb.AppendLine("--- внутренняя ошибка ---");
        }
        var lines = recentLog.TakeLast(40).ToList();
        if (lines.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Последние строки журнала:");
            foreach (var l in lines) sb.AppendLine(l);
        }
        return sb.ToString();
    }

    public static string? Save(string dir, string text, DateTime now, string prefix = Prefix)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"{prefix}{now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(path, text);
            Cleanup(dir, prefix, keep: 10);
            return path;
        }
        catch { return null; }
    }

    /// <summary>Самый свежий отчёт, который пользователь ещё не видел, иначе null.</summary>
    public static string? Pending(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            var latest = Directory.GetFiles(dir, Prefix + "*.txt")
                .Where(f => !Path.GetFileName(f).Equals(SeenFile, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).FirstOrDefault();
            if (latest == null) return null;
            var seenPath = Path.Combine(dir, SeenFile);
            var seen = File.Exists(seenPath) ? File.ReadAllText(seenPath).Trim() : "";
            return seen == Path.GetFileName(latest) ? null : latest;
        }
        catch { return null; }
    }

    public static void Acknowledge(string reportPath)
    {
        try { File.WriteAllText(Path.Combine(Path.GetDirectoryName(reportPath)!, SeenFile), Path.GetFileName(reportPath)); }
        catch { }
    }

    /// <summary>Версия программы, записанная в отчёте («Версия: 0.17.9»); null — строки нет или файл не прочитать.</summary>
    public static string? VersionOf(string reportPath)
    {
        try
        {
            foreach (var line in File.ReadLines(reportPath).Take(6))
                if (line.StartsWith("Версия:", StringComparison.Ordinal)) return line["Версия:".Length..].Trim();
        }
        catch { }
        return null;
    }

    /// <summary>
    /// Самый свежий отчёт (сбой или ошибка интерфейса) именно этой версии программы, иначе null.
    /// Отчёты прошлых версий к текущей не относятся: после обновления они не должны выглядеть как «недавние ошибки».
    /// </summary>
    public static string? LatestForVersion(string dir, string version)
    {
        try
        {
            if (!Directory.Exists(dir)) return null;
            return Directory.GetFiles(dir, "*.txt")
                .Where(f => Path.GetFileName(f) is string n && (n.StartsWith(Prefix, StringComparison.Ordinal) || n.StartsWith(ErrorPrefix, StringComparison.Ordinal))
                            && !n.Equals(SeenFile, StringComparison.OrdinalIgnoreCase))
                .Where(f => VersionOf(f) == version)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
        }
        catch { return null; }
    }

    /// <summary>Самый свежий отчёт с данным префиксом (crash- или error-), иначе null.</summary>
    public static string? Latest(string dir, string prefix = Prefix)
    {
        try
        {
            return !Directory.Exists(dir) ? null : Directory.GetFiles(dir, prefix + "*.txt")
                .Where(f => !Path.GetFileName(f).Equals(SeenFile, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).FirstOrDefault();
        }
        catch { return null; }
    }

    private static void Cleanup(string dir, string prefix, int keep)
    {
        foreach (var old in Directory.GetFiles(dir, prefix + "*.txt")
                     .Where(f => !Path.GetFileName(f).Equals(SeenFile, StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(f => Path.GetFileName(f), StringComparer.Ordinal).Skip(keep))
        {
            try { File.Delete(old); } catch { }
        }
    }
}

public enum GsiProblem { None, NoData, BadToken }

/// <summary>
/// Сторож связи с игрой: Dota запущена, а данных GSI нет. Dota шлёт данные даже в меню (раз в 30 секунд),
/// поэтому полторы минуты тишины после запуска — почти наверняка проблема. Предупреждает один раз за запуск игры.
/// </summary>
public sealed class GsiWatchdog
{
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(90);

    private DateTime? _gameSinceUtc;

    /// <summary>Когда замечен запуск Dota (UTC); null — игра не запущена.</summary>
    public DateTime? GameSinceUtc => _gameSinceUtc;
    private long _rejectedAtStart;
    private bool _warned;

    public GsiProblem Current { get; private set; }

    /// <summary>Возвращает проблему один раз, когда её пора показать; иначе None.</summary>
    public GsiProblem Tick(bool gameFound, bool gsiEnabled, DateTime? lastPacketUtc, long rejectedPackets, DateTime nowUtc)
    {
        if (!gameFound)
        {
            _gameSinceUtc = null;
            _warned = false;
            Current = GsiProblem.None;
            return GsiProblem.None;
        }
        if (_gameSinceUtc == null)
        {
            _gameSinceUtc = nowUtc;
            _rejectedAtStart = rejectedPackets;
        }
        bool gotData = lastPacketUtc != null && lastPacketUtc >= _gameSinceUtc - TimeSpan.FromSeconds(5);
        if (gotData || nowUtc - _gameSinceUtc < Grace)
        {
            Current = GsiProblem.None;
            return GsiProblem.None;
        }
        Current = gsiEnabled && rejectedPackets > _rejectedAtStart ? GsiProblem.BadToken : GsiProblem.NoData;
        if (_warned) return GsiProblem.None;
        _warned = true;
        return Current;
    }
}

/// <summary>Проверка горячих клавиш: повторы и пересечения со стандартными клавишами Dota 2.</summary>
public static class HotkeyRules
{
    /// <summary>Клавиши Dota 2 по умолчанию (способности, предметы, приказы, камера). Без модификатора они заняты игрой.</summary>
    public static readonly IReadOnlySet<string> DotaKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Q", "W", "E", "R", "D", "F", "T", "Z", "X", "C", "V", "B", "N", "A", "S", "H", "M", "P", "G", "Y",
        "D1", "D2", "D3", "D4", "D5", "D6", "F1", "F2", "F3", "F4", "F5", "Space", "Tab", "Escape", "Enter", "Return", "Oem3", "OemTilde"
    };

    /// <summary>"shift+ctrl+f8" → "Ctrl+Shift+F8". Пустая строка, если клавиши нет.</summary>
    public static string Normalize(string? gesture)
    {
        if (string.IsNullOrWhiteSpace(gesture)) return "";
        bool ctrl = false, shift = false, alt = false;
        string key = "";
        foreach (var raw in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl": case "control": ctrl = true; break;
                case "shift": shift = true; break;
                case "alt": alt = true; break;
                default: key = raw.Length == 1 ? raw.ToUpperInvariant() : char.ToUpperInvariant(raw[0]) + raw[1..]; break;
            }
        }
        if (key.Length == 0) return "";
        return (ctrl ? "Ctrl+" : "") + (shift ? "Shift+" : "") + (alt ? "Alt+" : "") + key;
    }

    public static bool HasModifier(string gesture) => Normalize(gesture).Contains('+');

    public static string KeyOf(string gesture)
    {
        var n = Normalize(gesture);
        int i = n.LastIndexOf('+');
        return i < 0 ? n : n[(i + 1)..];
    }

    /// <summary>Клавиша без Ctrl/Shift/Alt, которую Dota использует по умолчанию.</summary>
    public static bool ClashesWithDota(string gesture) => !HasModifier(gesture) && DotaKeys.Contains(KeyOf(gesture));

    /// <summary>Группы одинаковых сочетаний: имя действия → сочетание; возвращает сочетания, назначенные двум и более действиям.</summary>
    public static IReadOnlyList<string> Duplicates(IReadOnlyDictionary<string, string> actions) =>
        actions.Values.Select(Normalize).Where(g => g.Length > 0).GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1).Select(g => g.Key).ToList();
}
