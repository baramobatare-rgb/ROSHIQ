using Roshiq.Core.Models;

namespace Roshiq.Recommendations;

/// <summary>Итог матча сразу после игры: что получилось и одна главная ошибка — по своей истории, без интернета.</summary>
/// <param name="Basis">Основание сравнения (ключ строки: bd_basis_hero_role / bd_basis_role / bd_basis_hero); null — сравнения нет.</param>
public sealed record PostMatchSummary(MatchRecord Match, IReadOnlyList<Insight> Good, Insight? MainMistake, int Compared, string? Basis = null)
{
    public bool HasContent => Good.Count > 0 || MainMistake != null || PostMatch.Feedback(Match).Any;
}

/// <summary>
/// Как сработали подсказки в матче: что помогло (условие исчезло после подсказки), что пропущено
/// (условие осталось) и что было отложено до разбора.
/// </summary>
public sealed record TipFeedback(string? HelpedKey, int HelpedCount, string? MissedKey, int MissedCount,
    IReadOnlyList<DeferredMatchTip> Deferred, int DeferredMore)
{
    public bool HasFeedback => HelpedKey != null || MissedKey != null;
    public bool Any => HasFeedback || Deferred.Count > 0;
}

public static class PostMatch
{
    /// <summary>
    /// Самая частая сработавшая и самая частая пропущенная подсказка, плюс отложенные советы по времени игры.
    /// <paramref name="maxDeferred"/> — сколько отложенных показать сразу; остальные считаются в DeferredMore.
    /// </summary>
    public static TipFeedback Feedback(MatchRecord m, int maxDeferred = int.MaxValue)
    {
        static (string? Key, int Count) Top(Dictionary<string, int> d)
        {
            // при равенстве — по ключу, чтобы итог не зависел от порядка словаря
            var best = d.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).FirstOrDefault();
            return best.Value > 0 ? (best.Key, best.Value) : (null, 0);
        }
        var helped = Top(m.AlertsFollowed);
        var missed = Top(m.AlertsIgnored);
        var deferred = m.DeferredTips.Where(t => !string.IsNullOrWhiteSpace(t.Text)).OrderBy(t => t.Clock).ToList();
        int take = Math.Max(0, Math.Min(maxDeferred, deferred.Count));
        return new TipFeedback(helped.Key, helped.Count, missed.Key, missed.Count, deferred.Take(take).ToList(), deferred.Count - take);
    }

    /// <summary>Время совета в формате игровых часов.</summary>
    public static string DeferredLine(DeferredMatchTip t) => $"{Clock(Math.Max(0, t.Clock))} · {t.Text}";

    /// <summary>Короткие матчи (ливы, ремейки) не разбираем.</summary>
    public const int MinDurationSec = 10 * 60;

    public static bool Worth(MatchRecord m) => m.FromGsi && m.DurationSec >= MinDurationSec;

    /// <summary>Минимум надёжных значений показателя в выборке, чтобы сравнивать с «обычным».</summary>
    public const int MinValues = 3;

    /// <summary>
    /// Сравнение со своими матчами той же роли: сначала этот герой в этой роли (от 3 игр), иначе эта роль; другие роли — никогда.
    /// Роль неизвестна — только этот герой с неизвестной ролью. Каждый показатель сравнивается, только если у него
    /// не меньше 3 надёжных значений; иначе — только абсолютные пороги. Положительные пункты — в Good (до двух),
    /// самый важный отрицательный — MainMistake.
    /// </summary>
    public static PostMatchSummary Build(MatchRecord m, IEnumerable<MatchRecord> history, Func<string, string?>? itemName = null)
    {
        // обрыв записи (не видели конец матча) — итоговые счётчики неполные, такие игры не берём в «обычное»
        var others = history.Where(h => h.Id != m.Id && h.DurationSec >= MinDurationSec && h.Ended != false).ToList();
        var pool = others.Where(h => h.HeroId == m.HeroId && h.Position == m.Position).ToList();
        string? basis = m.Position == null ? "bd_basis_hero" : "bd_basis_hero_role";
        if (pool.Count < MinValues && m.Position != null)
        {
            pool = others.Where(h => h.Position == m.Position).ToList();
            basis = "bd_basis_role";
        }
        if (pool.Count < MinValues) { pool.Clear(); basis = null; }

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
            else if (deathsPer10 <= avg * 0.6 && minutes >= 20 && !m.Partial)   // неполную запись за «мало смертей» не хвалим
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
            if (poolLh.Count >= MinValues)
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
        var poolGpm = pool.Where(h => h.Gpm > 0).Select(h => (double)h.Gpm).ToList();
        if (poolGpm.Count >= MinValues && m.Gpm > 0)
        {
            double avg = poolGpm.Average();
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
        return new PostMatchSummary(m, good.OrderByDescending(g => g.Severity).Take(2).ToList(), main, pool.Count, basis);
    }

    /// <summary>One concrete next-game action tied to the observed main issue.</summary>
    public static Insight NextGoal(PostMatchSummary summary)
    {
        var (ru, en) = summary.MainMistake?.Kind switch
        {
            "deaths" => ("Перед выходом на опасную линию проверьте мини-карту и доступность пути отхода.", "Before entering a dangerous lane, check the minimap and your escape route."),
            "lh10" => ("На следующей линии следите за добиваниями к 10-й минуте; сравните результат с этим матчем.", "Track last hits at 10:00 next game and compare with this match."),
            "gpm" => ("После каждой драки выбирайте ближайший безопасный источник фарма, чтобы сократить простой.", "After each fight, choose a safe farming route to reduce idle time."),
            "item" => ("Заранее выберите ключевой предмет и после игры сравните время его покупки с этим матчем.", "Choose a key item in advance and compare its purchase time after the next game."),
            "no_tp" => ("Перед выходом с базы проверяйте наличие свитка телепорта.", "Check that you have a teleport scroll before leaving base."),
            "gold_risk" => ("Перед дракой проверяйте запас золота и покупайте запланированные компоненты.", "Before fighting, check your gold and buy planned components."),
            "ignored" => ("При появлении подсказки проверьте ситуацию и решите, какое действие сейчас нужно.", "When a tip appears, check the situation and choose the appropriate action."),
            _ => MissedGoal(Feedback(summary.Match).MissedKey)
        };
        return new Insight("next_goal", 0, ru, en);
    }

    /// <summary>Главной ошибки нет — цель берём из подсказки, которую чаще всего пропускали.</summary>
    private static (string Ru, string En) MissedGoal(string? alertKey) => alertKey switch
    {
        MatchAlerts.NoTeleport => ("Перед выходом с базы проверяйте наличие свитка телепорта.", "Check that you have a teleport scroll before leaving base."),
        MatchAlerts.NoBuyback => ("После 30-й минуты держите золото на выкуп перед важными драками.", "After minute 30, keep buyback gold before important fights."),
        MatchAlerts.ItemsInStash or MatchAlerts.WardsInStash => ("После каждой покупки сразу отправляйте курьера.", "Send the courier right after every purchase."),
        MatchAlerts.UnspentGold => ("Перед дракой тратьте накопленное золото на запланированные компоненты.", "Before a fight, spend saved gold on planned components."),
        _ => ("Выберите одну цель перед игрой и после матча проверьте, удалось ли её выполнить.", "Choose one goal before the game and check it after the match.")
    };

    /// <summary>
    /// Самый заметный ключевой предмет: разница с вашим медианным временем на этом герое в этой роли (сек).
    /// Только точные тайминги — увиденные после пропуска данных не сравниваются.
    /// </summary>
    private static (string Name, int Yours, int Delta)? ItemTimingDelta(MatchRecord m, IReadOnlyList<MatchRecord> pool, Func<string, string?>? itemName)
    {
        (string, int, int)? best = null;
        foreach (var (key, yours) in m.ExactItemTimings)
        {
            if (MatchRecorder.IgnoredItems.Contains(key) || MatchRecorder.IsRecipe(key) || yours < 600) continue;
            if (MatchStats.Benchmark(pool, m, key) is not { SameHero: true } bench) continue;
            int delta = yours - bench.AvgSec;
            if (best == null || Math.Abs(delta) > Math.Abs(best.Value.Item3))
                best = (itemName?.Invoke(key) is { Length: > 0 } n ? n : Pretty(key), yours, delta);
        }
        return best;
    }

    private static string DeathPhase(MatchRecord m, bool ru)
    {
        if (m.DeathEvents.Count < 4) return "";
        var groups = m.DeathEvents.GroupBy(d => (int)GamePhases.Of(d.Clock)).OrderByDescending(g => g.Count()).First();
        if (groups.Count() * 2 < m.DeathEvents.Count) return "";
        string phase = groups.Key switch
        {
            0 => ru ? "на линии (до 10 мин)" : "in lane (before 10:00)",
            1 => ru ? "в середине игры (10–25 мин)" : "mid game (10–25 min)",
            _ => ru ? "в поздней игре (после 25 мин)" : "late game (after 25 min)"
        };
        return ru ? $" Больше всего — {phase}: {groups.Count()} из {m.DeathEvents.Count}." : $" Most of them {phase}: {groups.Count()} of {m.DeathEvents.Count}.";
    }

    public static string AlertName(string key, bool ru) => key switch
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
