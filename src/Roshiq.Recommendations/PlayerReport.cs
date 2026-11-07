using Roshiq.Core.Models;

namespace Roshiq.Recommendations;

/// <summary>Сводка за период: результаты, KDA, серия, лучший и худший герой.</summary>
public sealed record HeroScore(int HeroId, int Games, int Wins)
{
    public double WinRate => Games == 0 ? 0 : (double)Wins / Games;
}

public sealed record WeekSummary(
    int Days, int Games, int Wins, int Losses, double? WinRate, double? PrevWinRate,
    double AvgKills, double AvgDeaths, double AvgAssists, int Streak, HeroScore? Best, HeroScore? Worst)
{
    /// <summary>(убийства + помощь) / смерти, в среднем за период.</summary>
    public double Kda => (AvgKills + AvgAssists) / Math.Max(1.0, AvgDeaths);
}

/// <summary>Ошибка, которая повторяется из матча в матч.</summary>
public sealed record MistakeStat(string Kind, string Ru, string En, int Count, int Of, IReadOnlyList<MatchRecord> Examples,
    double? RecentRate, double? PreviousRate)
{
    public double Rate => Of == 0 ? 0 : (double)Count / Of;
    public string Text(Language lang) => lang == Language.English ? En : Ru;

    /// <summary>-1 — стало лучше (реже), +1 — хуже, 0 — так же или мало данных.</summary>
    public int Trend => RecentRate is double r && PreviousRate is double p
        ? (r <= p - 0.1 ? -1 : r >= p + 0.1 ? 1 : 0) : 0;
}

/// <summary>Отрезок матча: смерти, потерянное золото и добивания / золото в минуту к концу отрезка против обычного.</summary>
public sealed record PhaseRow(string Key, string Ru, string En, int From, int To, int Deaths, int GoldLost,
    int? LastHits, int? LastHitsUsual, int? Gpm, int? GpmUsual)
{
    public string Text(Language lang) => lang == Language.English ? En : Ru;
}

public sealed record LateItem(string ItemKey, int Yours, int Usual, int Matches, bool SameHero)
{
    public int Delta => Yours - Usual;
}

public sealed record MatchBreakdown(IReadOnlyList<PhaseRow> Phases, IReadOnlyList<LateItem> LateItems, int Compared);

/// <summary>Сравнение с другими игроками: средний процентиль по показателю (0…1, доля игроков ниже вас).</summary>
public sealed record PercentileRow(string Key, string Ru, string En, double AvgPct, int Matches)
{
    public string Text(Language lang) => lang == Language.English ? En : Ru;
}

/// <summary>
/// Статистика игрока поверх локальной истории: неделя, повторяющиеся ошибки, разбор матча по фазам и сравнение с другими.
/// Без выдуманных норм: сравнение — с вашими же прошлыми играми или с процентилями OpenDota; пороги ошибок названы явно.
/// </summary>
public static class PlayerReport
{
    // ------------------------------------------------------------------ неделя

    public static WeekSummary Week(IEnumerable<MatchRecord> all, DateTime nowUtc, int days = 7)
    {
        var known = all.Where(m => m.Result != MatchResult.Unknown && m.StartedUtc > DateTime.MinValue)
            .OrderByDescending(m => m.StartedUtc).ToList();
        var from = nowUtc - TimeSpan.FromDays(days);
        var week = known.Where(m => m.StartedUtc >= from && m.StartedUtc <= nowUtc).ToList();
        var prev = known.Where(m => m.StartedUtc >= from - TimeSpan.FromDays(days) && m.StartedUtc < from).ToList();

        int wins = week.Count(m => m.Result == MatchResult.Win);
        int losses = week.Count - wins;
        double? wr = week.Count > 0 ? (double)wins / week.Count : null;
        double? prevWr = prev.Count >= 3 ? (double)prev.Count(m => m.Result == MatchResult.Win) / prev.Count : null;

        // серия — по всей истории, от последней игры
        int streak = 0;
        if (known.Count > 0)
        {
            var first = known[0].Result;
            foreach (var m in known) { if (m.Result != first) break; streak++; }
            if (first == MatchResult.Loss) streak = -streak;
        }

        var heroes = week.GroupBy(m => m.HeroId).Where(g => g.Key > 0 && g.Count() >= 2)
            .Select(g => new HeroScore(g.Key, g.Count(), g.Count(m => m.Result == MatchResult.Win))).ToList();
        var best = heroes.OrderByDescending(h => h.WinRate).ThenByDescending(h => h.Games).FirstOrDefault();
        var worst = heroes.Where(h => h.HeroId != best?.HeroId && h.WinRate < 0.5)
            .OrderBy(h => h.WinRate).ThenByDescending(h => h.Games).FirstOrDefault();
        if (best != null && best.WinRate < 0.5) best = null;   // «лучший» с винрейтом ниже половины — не лучший

        return new WeekSummary(days, week.Count, wins, losses, wr, prevWr,
            week.Count > 0 ? week.Average(m => m.Kills) : 0, week.Count > 0 ? week.Average(m => m.Deaths) : 0,
            week.Count > 0 ? week.Average(m => m.Assists) : 0, streak, best, worst);
    }

    // ------------------------------------------------------------------ повторяющиеся ошибки

    private static bool IsSupport(Position? p) => p is Position.SoftSupport or Position.HardSupport;

    /// <summary>Порог «много смертей»: саппорты умирают чаще — 9, остальные — 7.</summary>
    public static int DeathLimit(Position? p) => IsSupport(p) ? 9 : 7;

    private sealed record Check(string Kind, string Ru, string En, Func<MatchRecord, IReadOnlyList<MatchRecord>, bool?> Test);

    private static readonly Check[] Checks =
    {
        new("deaths", "Много смертей (7+, у саппортов 9+)", "Too many deaths (7+, supports 9+)",
            (m, _) => m.Deaths >= DeathLimit(m.Position)),
        new("early_deaths", "3+ смерти до 10-й минуты", "3+ deaths before 10:00",
            (m, _) => m.FromGsi ? m.DeathEvents.Count(d => d.Clock < 600) >= 3 : null),
        new("no_tp", "Смерти без телепорта (2+ за игру)", "Deaths without a teleport (2+ per game)",
            (m, _) => m.FromGsi && m.DeathEvents.Count > 0 ? m.DeathEvents.Count(d => !d.HadTeleport) >= 2 : null),
        new("lh10", "Мало добиваний к 10:00 (меньше 75% планки роли)", "Few last hits by 10:00 (under 75% of the role bar)",
            (m, _) => MatchReviewBuilder.LastHitTarget(m.Position) is int target && m.LastHitsAt(600) is int lh ? lh < target * 0.75 : null),
        new("ignored", "Подсказки остались без ответа (3+ за игру)", "Hints left unanswered (3+ per game)",
            (m, _) => m.FromGsi ? m.AlertsIgnored.Values.Sum() >= 3 : null),
        new("low_gpm", "Золото в минуту на 20% ниже вашего обычного на этой роли", "GPM 20% below your usual on this role",
            (m, all) =>
            {
                if (m.Gpm <= 0 || m.Position == null) return null;
                var same = all.Where(x => x.Id != m.Id && x.Position == m.Position && x.Gpm > 0).Select(x => x.Gpm).OrderBy(x => x).ToList();
                if (same.Count < 5) return null;
                return m.Gpm < same[same.Count / 2] * 0.8;
            }),
    };

    /// <summary>
    /// Ошибки, которые повторяются за последние <paramref name="last"/> игр: доля игр с ошибкой, примеры и сравнение
    /// новой половины выборки со старой. Показываем ошибку, если она была хотя бы в 2 играх и в четверти подходящих.
    /// </summary>
    public static IReadOnlyList<MistakeStat> Recurring(IEnumerable<MatchRecord> all, int last = 20, int top = 3)
    {
        var history = all.Where(m => m.DurationSec >= PostMatch.MinDurationSec).OrderByDescending(m => m.StartedUtc).ToList();
        var recent = history.Take(last).ToList();
        var result = new List<MistakeStat>();
        foreach (var c in Checks)
        {
            var applicable = recent.Select(m => (m, hit: c.Test(m, history))).Where(x => x.hit != null).ToList();
            if (applicable.Count < 3) continue;
            var hits = applicable.Where(x => x.hit == true).Select(x => x.m).ToList();
            if (hits.Count < 2 || (double)hits.Count / applicable.Count < 0.25) continue;
            int half = applicable.Count / 2;
            double? recentRate = half >= 2 ? (double)applicable.Take(half).Count(x => x.hit == true) / half : null;
            double? prevRate = half >= 2 ? (double)applicable.Skip(half).Count(x => x.hit == true) / (applicable.Count - half) : null;
            result.Add(new MistakeStat(c.Kind, c.Ru, c.En, hits.Count, applicable.Count, hits.Take(3).ToList(), recentRate, prevRate));
        }
        return result.OrderByDescending(r => r.Rate).ThenByDescending(r => r.Count).Take(top).ToList();
    }

    // ------------------------------------------------------------------ разбор матча по фазам

    private static int? Median(IEnumerable<int> values)
    {
        var list = values.ToList();
        return list.Count < 3 || MatchStats.IsNoisy(list) ? null : (int)Math.Round(MatchStats.Median(list));
    }

    /// <summary>
    /// Матч по отрезкам (лайнинг 0–10, середина 10–25, поздняя игра 25+): смерти и золото под риском,
    /// добивания и GPM к концу отрезка против медианы ваших игр на этом герое (или на этой роли).
    /// Плюс предметы, собранные на 3+ минуты позже обычного. Только для матчей, записанных через GSI.
    /// </summary>
    public static MatchBreakdown? Breakdown(MatchRecord m, IEnumerable<MatchRecord> all)
    {
        if (!m.FromGsi || m.Samples.Count < 4 || m.DurationSec < PostMatch.MinDurationSec) return null;
        var others = all.Where(x => x.Id != m.Id && x.FromGsi && x.Samples.Count >= 4).ToList();
        // минимум 3 сопоставимые игры: сначала на этом герое, потом на этой роли; меньше — «обычно» не показываем
        var pool = others.Where(x => x.HeroId == m.HeroId).ToList();
        if (pool.Count < 3 && m.Position != null) pool = others.Where(x => x.Position == m.Position).ToList();
        if (pool.Count < 3) pool = new List<MatchRecord>();

        var phases = new List<PhaseRow>();
        void Add(string key, string ru, string en, int from, int to)
        {
            if (m.DurationSec <= from) return;
            int end = Math.Min(to, m.DurationSec);
            var deaths = m.DeathEvents.Where(d => d.Clock >= from && d.Clock < end).ToList();
            int? usualLh = Median(pool.Where(x => x.DurationSec >= end).Select(x => x.LastHitsAt(end)).OfType<int>());
            int? usualGpm = Median(pool.Where(x => x.DurationSec >= end).Select(x => x.GpmAt(end)).OfType<int>());
            phases.Add(new PhaseRow(key, ru, en, from, end, deaths.Count, deaths.Sum(d => d.GoldAtRisk),
                m.LastHitsAt(end), usualLh, m.GpmAt(end), usualGpm));
        }
        Add("lane", "Линия (0–10)", "Laning (0–10)", 0, 600);
        Add("mid", "Середина (10–25)", "Mid game (10–25)", 600, 1500);
        Add("late", "Поздняя игра (25+)", "Late game (25+)", 1500, int.MaxValue);

        var late = new List<LateItem>();
        foreach (var (key, at) in m.ItemTimings)
        {
            if (at < 600 || MatchRecorder.IgnoredItems.Contains(key) || MatchRecorder.IsRecipe(key)) continue;
            if (MatchStats.Benchmark(all, m, key) is { Matches: >= 3 } b && at - b.AvgSec >= 180)
                late.Add(new LateItem(key, at, b.AvgSec, b.Matches, b.SameHero));
        }
        return new MatchBreakdown(phases, late.OrderByDescending(x => x.Delta).Take(4).ToList(), pool.Count);
    }

    // ------------------------------------------------------------------ сравнение с другими игроками

    /// <summary>
    /// Средний процентиль по каждому показателю OpenDota за последние разборы (по герою, если задан).
    /// Порядок — как в <see cref="MatchReviewBuilder.BenchmarkNames"/>.
    /// </summary>
    public static IReadOnlyList<PercentileRow> Percentiles(IEnumerable<MatchReview> reviews, int? heroId = null)
    {
        var list = reviews.Where(r => heroId == null || r.HeroId == heroId).ToList();
        var rows = new List<PercentileRow>();
        foreach (var (key, names) in MatchReviewBuilder.BenchmarkNames)
        {
            var values = list.SelectMany(r => r.Benchmarks).Where(b => b.Key == key).Select(b => b.Pct).ToList();
            if (values.Count == 0) continue;
            rows.Add(new PercentileRow(key, names.Ru, names.En, values.Average(), values.Count));
        }
        return rows;
    }
}
