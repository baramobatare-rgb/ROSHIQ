using System.Globalization;
using System.Text;
using Roshiq.Core.Diagnostics;

namespace Roshiq.Recommendations;

/// <summary>
/// Журнал подсказок: каждая подсказка и её исход (показана, истекла, снята, заменена) — строкой в файл матча.
/// Хранится только на этом компьютере (папка logs\tips) и нужен, чтобы по фактам видеть, где помощник шумит, а где молчит.
/// Файлов не больше <see cref="KeepFiles"/>: старые удаляются.
/// </summary>
public sealed class TipJournal
{
    public const int KeepFiles = 30;
    public const string Prefix = "tips-";
    private readonly object _sync = new();
    private bool _warned;

    public TipJournal(string dir) { Dir = dir; }

    public string Dir { get; }

    /// <summary>Файл матча (tips-&lt;номер&gt;.log) или служебный файл дня, если матча нет.</summary>
    public static string FileName(string? matchId, DateTime localNow) =>
        matchId is { Length: > 0 } id && id.All(char.IsAsciiLetterOrDigit)
            ? $"{Prefix}{id}.log"
            : $"{Prefix}service-{localNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log";

    public static string OutcomeText(TipOutcome outcome) => outcome switch
    {
        TipOutcome.Shown => "показана",
        TipOutcome.Dropped => "не показана (оверлей скрыт или уведомления выключены)",
        TipOutcome.Expired => "истекла, не дождавшись показа",
        TipOutcome.Invalidated => "снята — уже неактуальна",
        TipOutcome.Merged => "заменена более свежей",
        _ => outcome.ToString()
    };

    /// <summary>«21:40:12 | 10:05 | показана | alert_no_tp | Нет телепорта — купи ТП» (время игры — «—», если матча нет).</summary>
    public static string FormatLine(DateTime localNow, int? clock, string kind, TipOutcome outcome, string header, string text)
    {
        string game = clock is int c ? (c < 0 ? "-" : "") + $"{Math.Abs(c) / 60}:{Math.Abs(c) % 60:00}" : "—";
        var body = (header.Length > 0 ? header + ": " : "") + text;
        body = body.Replace("\r", " ").Replace("\n", " ").Replace("|", "/").Trim();
        if (body.Length > 240) body = body[..240] + "…";
        return $"{localNow.ToString("HH:mm:ss", CultureInfo.InvariantCulture)} | {game} | {OutcomeText(outcome)} | {kind} | {body}";
    }

    /// <summary>Записать исход подсказки. Никогда не бросает исключений: журнал — вспомогательный.</summary>
    public void Add(string? matchId, int? clock, Tip tip, TipOutcome outcome, DateTime localNow)
    {
        try
        {
            lock (_sync)
            {
                Directory.CreateDirectory(Dir);
                var path = Path.Combine(Dir, FileName(matchId, localNow));
                bool created = !File.Exists(path);
                File.AppendAllText(path, FormatLine(localNow, clock, tip.Kind, outcome, tip.Header, tip.Text) + Environment.NewLine, new UTF8Encoding(false));
                if (created) Cleanup();
            }
        }
        catch (Exception ex)
        {
            if (!_warned) { _warned = true; Log.Warn("Журнал подсказок: не удалось записать — " + ex.Message); }
        }
    }

    /// <summary>Сколько файлов в журнале (для проверки состояния).</summary>
    public int Count()
    {
        try { return Directory.Exists(Dir) ? Directory.GetFiles(Dir, Prefix + "*.log").Length : 0; }
        catch { return 0; }
    }

    private void Cleanup()
    {
        foreach (var old in Directory.GetFiles(Dir, Prefix + "*.log").OrderByDescending(File.GetLastWriteTimeUtc).Skip(KeepFiles))
        {
            try { File.Delete(old); } catch { }
        }
    }
}
