using Roshiq.Core.Models;
using Roshiq.Data;

namespace Roshiq.Recommendations;

/// <summary>Эпизод хода матча: время, вид (death/tower/rax/fort/roshan/swing), знак: −1 плохо для вас, 0 нейтрально, +1 хорошо.</summary>
public sealed record TimelineEvent(int Time, string Kind, int Severity, string Ru, string En)
{
    public string Text(Language lang) => lang == Language.English ? En : Ru;
}

/// <summary>
/// 0.22.0: ход матча по разобранной записи (OpenDota): ваши смерти с разменом или без, вышки, бараки, Рошан
/// и резкие повороты по золоту. Только факты из записи, без догадок о том, кто «слил».
/// </summary>
public static class MatchTimeline
{
    /// <summary>Окно «размена»: команда убила врага за 20 секунд до или после вашей смерти…</summary>
    public const int TradeWindowSec = 20;
    /// <summary>…или взяла объект (вышку, барак, Рошана) за 30 секунд — смерть на сплите ради вышки тоже размен.</summary>
    public const int TradeObjectiveWindowSec = 30;
    /// <summary>Сколько эпизодов показывать сразу; остальное — под «Показать всё».</summary>
    public const int KeyEvents = 12;
    /// <summary>Резкий поворот: перевес по золоту изменился хотя бы на столько за 2 минуты.</summary>
    public const int SwingGold = 3000;
    public const int MaxEvents = 40;

    public static List<TimelineEvent> Build(MatchDetails d, Func<int, (string Ru, string En)> heroName)
    {
        var list = new List<TimelineEvent>();

        foreach (var death in d.DeathsBy)
        {
            int kills = d.HeroKills.Count(k => k.ByYourTeam && !k.VictimYourTeam && Math.Abs(k.Time - death.Time) <= TradeWindowSec);
            var objective = d.Objectives.FirstOrDefault(o => TakenByYou(o) && Math.Abs(o.Time - death.Time) <= TradeObjectiveWindowSec);
            var (ru, en) = death.KillerHeroId > 0 ? heroName(death.KillerHeroId) : ("врага", "an enemy");
            if (kills > 0)
                list.Add(new TimelineEvent(death.Time, "death", 0, $"Смерть от {ru} — размен: {Kills(kills)}", $"Killed by {en} — traded for {kills} kill{(kills == 1 ? "" : "s")}"));
            else if (objective != null)
            {
                var (oRu, oEn) = objective.Kind == "roshan" ? ("Рошана", "Roshan") : Building(objective);
                list.Add(new TimelineEvent(death.Time, "death", 0, $"Смерть от {ru} — размен: взяли {oRu}", $"Killed by {en} — traded for {oEn}"));
            }
            else
                list.Add(new TimelineEvent(death.Time, "death", -1, $"Смерть от {ru} — без размена", $"Killed by {en} — no trade"));
        }

        foreach (var o in d.Objectives)
        {
            var (whatRu, whatEn) = Building(o);
            if (o.Kind == "roshan")
                list.Add(o.Ours
                    ? new TimelineEvent(o.Time, "roshan", 1, "Ваша команда убила Рошана", "Your team killed Roshan")
                    : new TimelineEvent(o.Time, "roshan", -1, "Враги убили Рошана", "The enemy killed Roshan"));
            else
                list.Add(o.Ours
                    ? new TimelineEvent(o.Time, o.Kind, -1, $"Потеряли {whatRu}", $"Lost {whatEn}")
                    : new TimelineEvent(o.Time, o.Kind, 1, $"Снесли {whatRu}", $"Destroyed {whatEn}"));
        }

        // резкие повороты по золоту: окно 2 минуты, не чаще одного раза в 3 минуты; с причинами из того же окна
        var gold = d.TeamGoldAdv;
        int last = -10;
        var deathsSoFar = list.Where(e => e.Kind == "death").ToList();
        for (int i = 2; i < gold.Count; i++)
        {
            int delta = gold[i] - gold[i - 2];
            if (Math.Abs(delta) < SwingGold || i - last < 3) continue;
            last = i;
            int from = (i - 2) * 60, to = i * 60;
            string n = Math.Abs(delta).ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("ru-RU"));
            string nEn = Math.Abs(delta).ToString("N0", System.Globalization.CultureInfo.InvariantCulture);
            string span = $"{Clock(from)}–{Clock(to)}";
            var whyRu = new List<string>();
            var whyEn = new List<string>();
            if (delta < 0)
            {
                int untraded = deathsSoFar.Count(e => e.Severity < 0 && e.Time >= from && e.Time < to);
                if (untraded > 0) { whyRu.Add($"ваших смертей без размена: {untraded}"); whyEn.Add($"{untraded} untraded death{(untraded == 1 ? "" : "s")} of yours"); }
            }
            foreach (var o in d.Objectives.Where(o => o.Time >= from && o.Time < to && o.Kind != "roshan" && o.Ours == (delta < 0)))
            {
                var (bRu, bEn) = Building(o);
                whyRu.Add((o.Ours ? "потеряли " : "снесли ") + bRu);
                whyEn.Add((o.Ours ? "lost " : "destroyed ") + bEn);
            }
            if (d.Objectives.Any(o => o.Kind == "roshan" && o.Time >= from && o.Time < to && o.Ours == (delta > 0)))
            { whyRu.Add(delta > 0 ? "взяли Рошана" : "враги взяли Рошана"); whyEn.Add(delta > 0 ? "took Roshan" : "the enemy took Roshan"); }
            string tailRu = whyRu.Count > 0 ? " · " + string.Join(", ", whyRu) : "";
            string tailEn = whyEn.Count > 0 ? " · " + string.Join(", ", whyEn) : "";
            list.Add(delta > 0
                ? new TimelineEvent(from, "swing", 1, $"{span}: +{n} золота к перевесу{tailRu}", $"{span}: +{nEn} gold lead{tailEn}")
                : new TimelineEvent(from, "swing", -1, $"{span}: −{n} золота{tailRu}", $"{span}: −{nEn} gold{tailEn}"));
        }

        return list.OrderBy(e => e.Time).ThenBy(e => e.Severity).Take(MaxEvents).ToList();
    }

    /// <summary>
    /// Ключевые эпизоды для показа сразу: повороты, смерти без размена, бараки, трон и Рошан, потерянные вышки — по времени.
    /// Размененные смерти и снесённые вами вышки — под «Показать всё».
    /// </summary>
    public static IReadOnlyList<TimelineEvent> Key(IReadOnlyList<TimelineEvent> all)
    {
        static int Rank(TimelineEvent e) => e.Kind switch
        {
            "swing" => 0,
            "fort" or "rax_melee" or "rax_range" or "roshan" => 1,
            "death" when e.Severity < 0 => 2,
            "tower" when e.Severity < 0 => 3,
            _ => 9
        };
        return all.Where(e => Rank(e) < 9).OrderBy(Rank).ThenBy(e => e.Time).Take(KeyEvents).OrderBy(e => e.Time).ToList();
    }

    private static bool TakenByYou(MatchObjective o) => o.Kind == "roshan" ? o.Ours : !o.Ours;

    private static string Kills(int n) => n switch { 1 => "1 убийство", 2 or 3 or 4 => $"{n} убийства", _ => $"{n} убийств" };

    private static string Clock(int sec) => $"{sec / 60}:{sec % 60:00}";

    private static (string Ru, string En) Building(MatchObjective o)
    {
        var (laneRu, laneEn) = o.Lane switch
        {
            "top" => (" (верх)", " (top)"),
            "mid" => (" (мид)", " (mid)"),
            "bot" => (" (низ)", " (bottom)"),
            _ => ("", "")
        };
        return o.Kind switch
        {
            "tower" => ($"вышку T{o.Tier}{laneRu}", $"a T{o.Tier} tower{laneEn}"),
            "rax_melee" => ($"барак ближнего боя{laneRu}", $"melee barracks{laneEn}"),
            "rax_range" => ($"барак дальнего боя{laneRu}", $"ranged barracks{laneEn}"),
            "fort" => ("трон", "the Ancient"),
            _ => ("", "")
        };
    }

    /// <summary>Сводка для выводов разбора: сколько смертей без размена.</summary>
    public static int UntradedDeaths(IEnumerable<TimelineEvent> events) => events.Count(e => e.Kind == "death" && e.Severity < 0);
}
