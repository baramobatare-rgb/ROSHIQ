using Roshiq.Data;

namespace Roshiq.Recommendations;

/// <summary>Одна строка сравнения: показатель, значение в матче и доля игроков на герое, которых вы обошли (0…100).</summary>
public sealed record ComparisonRow(string Metric, string LabelRu, string LabelEn, string Value, int BetterThan)
{
    public string Text(Roshiq.Core.Models.Language lang) => lang == Roshiq.Core.Models.Language.English
        ? $"{LabelEn} {Value} — better than {BetterThan}% of players on this hero"
        : $"{LabelRu} {Value} — лучше, чем у {BetterThan}% игроков на этом герое";
}

/// <summary>
/// 0.21.1: сравнение матча с другими игроками на том же герое по перцентилям OpenDota (как у Dota Plus и Dota Coach).
/// Честно: это все публичные матчи на герое, не только ваш ранг. Смерти — чем меньше, тем лучше.
/// </summary>
public static class HeroComparison
{
    public const int MinDurationSec = 600;

    public static IReadOnlyList<ComparisonRow> Build(MatchRecord m, HeroBenchmarks? b)
    {
        var rows = new List<ComparisonRow>();
        if (b == null || m.DurationSec < MinDurationSec) return rows;
        double minutes = m.DurationSec / 60.0;
        void Add(string metric, string ru, string en, double value, string shown, bool lowerIsBetter = false)
        {
            if (b.Percentile(metric, value) is not double p) return;
            int better = (int)Math.Round(Math.Clamp(lowerIsBetter ? 100 - p : p, 0, 100));
            rows.Add(new ComparisonRow(metric, ru, en, shown, better));
        }
        if (m.Gpm > 0) Add("gold_per_min", "GPM", "GPM", m.Gpm, m.Gpm.ToString());
        if (m.Xpm > 0) Add("xp_per_min", "XPM", "XPM", m.Xpm, m.Xpm.ToString());
        if (m.LastHits > 0)
        {
            double lpm = m.LastHits / minutes;
            Add("last_hits_per_min", "Добивания в минуту", "Last hits per minute", lpm, lpm.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture));
        }
        if (m.Ended == true || m.Deaths > 0)
        {
            double dpm = m.Deaths / minutes;
            Add("deaths_per_min", "Смертей за 10 минут", "Deaths per 10 minutes", dpm,
                (dpm * 10).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture), lowerIsBetter: true);
        }
        return rows;
    }

    /// <summary>В формате полосок «Лучше, чем у N%» из разбора OpenDota.</summary>
    public static List<Benchmark> ToBenchmarks(IReadOnlyList<ComparisonRow> rows) =>
        rows.Select(r => new Benchmark(r.Metric, double.TryParse(r.Value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0, r.BetterThan / 100.0)).ToList();

    /// <summary>Вес показателя в оценке матча: золото и смерти решают больше, чем опыт и добивания.</summary>
    public static double Weight(string metric) => metric switch
    {
        "gold_per_min" => 0.3,
        "deaths_per_min" => 0.3,
        "xp_per_min" => 0.2,
        "last_hits_per_min" => 0.2,
        _ => 0
    };

    /// <summary>
    /// 0.21.2: оценка матча 0…100 — взвешенная доля игроков на герое, которых вы обошли.
    /// null, если показателей меньше трёх (короткий матч, нет данных OpenDota).
    /// </summary>
    public static int? Score(IReadOnlyList<ComparisonRow> rows)
    {
        var used = rows.Where(r => Weight(r.Metric) > 0).ToList();
        if (used.Count < 3) return null;
        double w = used.Sum(r => Weight(r.Metric));
        return (int)Math.Round(used.Sum(r => r.BetterThan * Weight(r.Metric)) / w);
    }

    /// <summary>Словесная оценка: ключ строки для 0…100.</summary>
    public static string ScoreVerdictKey(int score) => score >= 70 ? "score_strong" : score >= 40 ? "score_mid" : "score_weak";

    /// <summary>Самый слабый показатель матча (меньше всех обошли), если он ниже трети игроков.</summary>
    public static ComparisonRow? Weakest(IReadOnlyList<ComparisonRow> rows) =>
        rows.Where(r => r.BetterThan < 33).OrderBy(r => r.BetterThan).FirstOrDefault();
}
