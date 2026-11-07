using Roshiq.Core.Models;

namespace Roshiq.Recommendations;

/// <summary>Итог матча сразу после игры: что получилось и одна главная ошибка — по своей истории, без интернета.</summary>
public sealed record PostMatchSummary(MatchRecord Match, IReadOnlyList<Insight> Good, Insight? MainMistake, int Compared)
{
    public bool HasContent => Good.Count > 0 || MainMistake != null;
}

public static class PostMatch
{
    /// <summary>Короткие матчи (ливы, ремейки) не разбираем.</summary>
    public const int MinDurationSec = 10 * 60;

    public static bool Worth(MatchRecord m) => m.FromGsi && m.DurationSec >= MinDurationSec;

    /// <summary>
    /// Сравнение со своими матчами: сначала на этом герое (от 3 игр), иначе на этой роли, иначе со всеми.
    /// Положительные пункты — в Good (до двух), самый важный отрицательный — MainMistake.
    /// </summary>
    public static PostMatchSummary Build(MatchRecord m, IEnumerable<MatchRecord> history, Func<string, string?>? itemName = null)
    {
        var others = history.Where(h => h.Id != m.Id && h.DurationSec >= MinDurationSec).ToList();
        var pool = others.Where(h => h.HeroId == m.HeroId).ToList();
        if (pool.Count < 3) pool = others.Where(h => m.Position != null && h.Position == m.Position).ToList();
        if (pool.Count < 3) pool = others;

        var good = new List<Insight>();
        var bad = new List<Insight>();
        double minutes = Math.Max(1, m.DurationSec / 60.0);

        // --- смерти
        double deathsPer10 = m.Deaths / minutes * 10;
        if (pool.Count >= 3)
        {
            double avg = pool.Average(h => h.Deaths / Math.Max(1, h.DurationSec / 60.0) * 10);
            if (deathsPer10 >= avg * 1.4 && m.Deaths >= 6)
                bad.Add(new("deaths", -3 - (int)Math.Min(2, (deathsPer10 - avg) / Math.Max(0.5, avg)),
                    $"Смертей больше обычного: {m.Deaths} (обычно ~{avg * minutes / 10:0} за такую игру).{DeathPhase(m, true)}",
                    $"More deaths than usual: {m.Deaths} (usually ~{avg * minutes / 10:0} for this length).{DeathPhase(m, false)}"));
            else if (deathsPer10 <= avg * 0.6 && minutes >= 20)
                good.Add(new("deaths", 2, $"Мало смертей: {m.Deaths} — заметно реже обычного.", $"Few deaths: {m.Deaths} — clearly fewer than usual."));
        }
        else if (m.Deaths >= 10)
            bad.Add(new("deaths", -3, $"Много смертей: {m.Deaths}.{DeathPhase(m, true)}", $"Many deaths: {m.Deaths}.{DeathPhase(m, false)}"));

        // --- добивания к 10-й минуте
        var lh10 = m.LastHitsAt(600);
        var poolLh = pool.Select(h => h.LastHitsAt(600)).Where(v => v != null).Select(v => v!.Value).ToList();
        int? target = MatchReviewBuilder.LastHitTarget(m.Position);
        if (lh10 is int lh && (m.Position is Position.Carry or Position.Mid or Position.Offlane || m.Position == null))
        {
            if (poolLh.Count >= 3)
            {
                double avg = poolLh.Average();
                if (lh >= avg * 1.2 && lh >= 30)
                    good.Add(new("lh10", 2, $"Сильная линия: {lh} добиваний к 10-й минуте (обычно ~{avg:0}).", $"Strong lane: {lh} last hits by 10:00 (usually ~{avg:0})."));
                else if (lh <= avg * 0.75)
                    bad.Add(new("lh10", -2, $"Слабая линия: {lh} добиваний к 10-й минуте при обычных ~{avg:0}.", $"Weak lane: {lh} last hits by 10:00 vs usual ~{avg:0}."));
            }
            else if (target is int t && lh < t * 0.7)
                bad.Add(new("lh10", -2, $"Мало добиваний: {lh} к 10-й минуте (ориентир — {t}).", $"Few last hits: {lh} by 10:00 (target {t})."));
        }

        // --- доход
        if (pool.Count >= 3 && m.Gpm > 0)
        {
            double avg = pool.Where(h => h.Gpm > 0).Select(h => (double)h.Gpm).DefaultIfEmpty(0).Average();
            if (avg > 0 && m.Gpm >= avg * 1.15)
                good.Add(new("gpm", 1, $"Хороший фарм: {m.Gpm} золота в минуту (обычно ~{avg:0}).", $"Good farm: {m.Gpm} GPM (usually ~{avg:0})."));
            else if (avg > 0 && m.Gpm <= avg * 0.8 && m.Position is Position.Carry or Position.Mid)
                bad.Add(new("gpm", -1, $"Фарм ниже обычного: {m.Gpm} золота в минуту (обычно ~{avg:0}).", $"Farm below usual: {m.Gpm} GPM (usually ~{avg:0})."));
        }

        // --- участие (KDA)
        double kda = (m.Kills + m.Assists) / Math.Max(1.0, m.Deaths);
        if (pool.Count >= 3)
        {
            double avg = pool.Average(h => (h.Kills + h.Assists) / Math.Max(1.0, h.Deaths));
            if (kda >= avg * 1.4 && kda >= 3)
                good.Add(new("kda", 2, $"Отличное участие: {m.Kills}/{m.Deaths}/{m.Assists} — лучше вашего обычного.", $"Great impact: {m.Kills}/{m.Deaths}/{m.Assists} — above your usual."));
        }
        else if (kda >= 4)
            good.Add(new("kda", 1, $"Хорошее участие: {m.Kills}/{m.Deaths}/{m.Assists}.", $"Good impact: {m.Kills}/{m.Deaths}/{m.Assists}."));

        // --- ключевые предметы быстрее/медленнее своего обычного
        var timing = ItemTimingDelta(m, pool, itemName);
        if (timing is { } td)
        {
            if (td.Delta <= -120)
                good.Add(new("item", 1, $"{td.Name} на {-td.Delta / 60} мин раньше обычного ({Clock(td.Yours)}).", $"{td.Name} {-td.Delta / 60} min earlier than usual ({Clock(td.Yours)})."));
            else if (td.Delta >= 240)
                bad.Add(new("item", -2, $"{td.Name} на {td.Delta / 60} мин позже обычного ({Clock(td.Yours)}) — посмотрите, где терялось время.", $"{td.Name} {td.Delta / 60} min later than usual ({Clock(td.Yours)}) — check where time was lost."));
        }

        // --- смерти без телепорта и с большим золотом
        int noTp = m.DeathEvents.Count(d => !d.HadTeleport && d.Clock > 300);
        if (noTp >= 3)
            bad.Add(new("no_tp", -2, $"{noTp} смертей без телепорта — с ТП часть из них можно было избежать или успеть на помощь.", $"{noTp} deaths without a teleport — with a TP some could be avoided."));
        int richDeaths = m.DeathEvents.Count(d => d.GoldAtRisk >= 1500);
        if (richDeaths >= 2)
            bad.Add(new("gold_risk", -2, $"{richDeaths} раза умерли с большим запасом золота — тратьте его до драки.", $"Died {richDeaths} times holding lots of gold — spend it before fights."));

        // --- проигнорированные подсказки
        if (m.AlertsIgnored.Count > 0)
        {
            var (key, count) = m.AlertsIgnored.OrderByDescending(kv => kv.Value).First();
            if (count >= 3)
                bad.Add(new("ignored", -1, $"Подсказка «{AlertName(key, true)}» висела без реакции {count} раз.", $"The “{AlertName(key, false)}” tip was ignored {count} times."));
        }

        if (good.Count == 0 && m.Result == MatchResult.Win)
            good.Add(new("win", 1, "Победа — отличная работа.", "A win — well played."));

        var main = bad.OrderBy(b => b.Severity).FirstOrDefault();
        return new PostMatchSummary(m, good.OrderByDescending(g => g.Severity).Take(2).ToList(), main, pool.Count);
    }

    /// <summary>Самый заметный ключевой предмет: разница с вашим средним временем на этом герое (сек).</summary>
    private static (string Name, int Yours, int Delta)? ItemTimingDelta(MatchRecord m, IReadOnlyList<MatchRecord> pool, Func<string, string?>? itemName)
    {
        var sameHero = pool.Where(h => h.HeroId == m.HeroId).ToList();
        if (sameHero.Count < 3) return null;
        (string, int, int)? best = null;
        foreach (var (key, yours) in m.ItemTimings)
        {
            if (MatchRecorder.IgnoredItems.Contains(key) || yours < 600) continue;
            var times = sameHero.Where(h => h.ItemTimings.ContainsKey(key)).Select(h => h.ItemTimings[key]).ToList();
            if (times.Count < 3) continue;
            int delta = yours - (int)times.Average();
            if (best == null || Math.Abs(delta) > Math.Abs(best.Value.Item3))
                best = (itemName?.Invoke(key) is { Length: > 0 } n ? n : Pretty(key), yours, delta);
        }
        return best;
    }

    private static string DeathPhase(MatchRecord m, bool ru)
    {
        if (m.DeathEvents.Count < 4) return "";
        var groups = m.DeathEvents.GroupBy(d => d.Clock < 600 ? 0 : d.Clock < 1500 ? 1 : 2).OrderByDescending(g => g.Count()).First();
        if (groups.Count() * 2 < m.DeathEvents.Count) return "";
        string phase = groups.Key switch
        {
            0 => ru ? "на линии (до 10 мин)" : "in lane (before 10:00)",
            1 => ru ? "в середине игры (10–25 мин)" : "mid game (10–25 min)",
            _ => ru ? "в поздней игре (после 25 мин)" : "late game (after 25 min)"
        };
        return ru ? $" Больше всего — {phase}: {groups.Count()} из {m.DeathEvents.Count}." : $" Most of them {phase}: {groups.Count()} of {m.DeathEvents.Count}.";
    }

    private static string AlertName(string key, bool ru) => key switch
    {
        MatchAlerts.NoTeleport => ru ? "нет телепорта" : "no teleport",
        MatchAlerts.NoBuyback => ru ? "не хватает на выкуп" : "can't buy back",
        MatchAlerts.ItemsInStash => ru ? "предметы в тайнике" : "items in stash",
        MatchAlerts.WardsInStash => ru ? "варды в тайнике" : "wards in stash",
        MatchAlerts.UnspentGold => ru ? "много золота" : "unspent gold",
        _ => key
    };

    private static string Pretty(string itemKey)
    {
        var s = itemKey.StartsWith("item_", StringComparison.Ordinal) ? itemKey[5..] : itemKey;
        return string.Join(' ', s.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }

    private static string Clock(int s) => $"{s / 60}:{s % 60:00}";
}
