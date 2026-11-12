using Roshiq.Core.Models;

namespace Roshiq.Draft;

/// <summary>Строка оценки составов: Severity 1 — сильная сторона, 0 — план на игру, -1 — риск.</summary>
public sealed record CompositionLine(string Key, int Severity, string Ru, string En)
{
    public string Text(Language lang) => lang == Language.English ? En : Ru;
}

/// <summary>
/// 0.21.3: оценка драфта целиком — перекос по типу урона, план на игру и чего ждать от врагов.
/// Только по качественным признакам героев (heroes.txt), без процентов побед. Строки короткие и называют героев,
/// из-за которых сделан вывод (по отзыву «пользователя»: без имён совет выглядит шаблоном).
/// </summary>
public static class DraftComposition
{
    /// <summary>С какого числа известных героев команды делать выводы о составе.</summary>
    public const int MinHeroes = 4;
    public const int MaxLines = 5;

    private static List<Hero> With(IReadOnlyList<Hero> team, params string[] traits) => team.Where(h => traits.Any(h.Has)).ToList();
    private static int Count(IReadOnlyList<Hero> team, params string[] traits) => With(team, traits).Count;
    private static int Physical(IReadOnlyList<Hero> t) => Count(t, "right_click", "physical_burst");
    private static int Magic(IReadOnlyList<Hero> t) => Count(t, "magic_burst");
    private static string Names(IEnumerable<Hero> heroes) => string.Join(", ", heroes.Take(3).Select(h => h.Name));

    public static IReadOnlyList<CompositionLine> Assess(IReadOnlyList<Hero> allies, IReadOnlyList<Hero> enemies)
    {
        var lines = new List<CompositionLine>();
        bool ours = allies.Count >= MinHeroes, theirs = enemies.Count >= MinHeroes;
        if (ours)
        {
            if (Magic(allies) >= 4 && Physical(allies) <= 1)
                lines.Add(new("our_magic", -1, "Почти весь урон магический — против BKB будет тяжело, нужен физический урон.",
                    "Almost all your damage is magical — BKB will hurt, you need physical damage."));
            else if (Physical(allies) >= 4 && Magic(allies) <= 1)
                lines.Add(new("our_physical", -1, "Почти весь урон физический — броня врагов его срежет, нужен магический урон.",
                    "Almost all your damage is physical — enemy armor will cut it, you need magic damage."));
            var init = With(allies, "initiator");
            if (init.Count >= 1 && Count(allies, "aoe") >= 2)
                lines.Add(new("our_teamfight", 1, $"Инициация ({Names(init)}) и урон по площади — вам выгодны драки 5 на 5.",
                    $"Initiation ({Names(init)}) and area damage — 5v5 fights favour you."));
            if (Count(allies, "push") >= 3)
                lines.Add(new("our_push", 1, "Много пуша — после выигранной драки сразу идите на вышки.",
                    "Lots of push — after a won fight, go for towers right away."));
        }

        // план на игру — одной строкой: ранний темп своих и поздняя сила врагов говорят об одном и том же
        int ourEarly = Count(allies, "early_game"), ourLate = Count(allies, "late_game");
        int theirEarly = Count(enemies, "early_game"), theirLate = Count(enemies, "late_game");
        bool goEarly = ours && ourEarly >= 2 && ourLate <= 1 || theirs && theirLate >= 2 && theirEarly == 0;
        bool goLate = ours && ourLate >= 2 && ourEarly == 0 || theirs && theirEarly >= 2 && theirLate <= 1;
        if (goEarly && !goLate)
            lines.Add(new("plan_early", 0, "План: играйте от ранней игры — давите и берите вышки до 25-й минуты.",
                "Plan: play for the early game — push and take towers before minute 25."));
        else if (goLate && !goEarly)
            lines.Add(new("plan_late", 0, "План: переживите первые 20 минут без лишних смертей — дальше время на вашей стороне.",
                "Plan: survive the first 20 minutes without needless deaths — time is on your side after that."));

        if (theirs)
        {
            var magic = With(enemies, "magic_burst");
            var physical = With(enemies, "right_click", "physical_burst");
            if (magic.Count >= 3 && physical.Count <= 1)
                lines.Add(new("enemy_magic", -1, $"Магический урон у врагов ({Names(magic)}) — Pipe of Insight, Glimmer Cape, Black King Bar, Eternal Shroud.",
                    $"Enemy magic damage ({Names(magic)}) — Pipe of Insight, Glimmer Cape, Black King Bar, Eternal Shroud."));
            else if (physical.Count >= 3 && magic.Count <= 1)
                lines.Add(new("enemy_physical", -1, $"Физический урон у врагов ({Names(physical)}) — Blade Mail, Solar Crest, Crimson Guard, Assault Cuirass.",
                    $"Enemy physical damage ({Names(physical)}) — Blade Mail, Solar Crest, Crimson Guard, Assault Cuirass."));
            var heal = With(enemies, "heal", "lifesteal");
            if (heal.Count >= 2)
                lines.Add(new("enemy_heal", -1, $"Много лечения у врагов ({Names(heal)}) — Spirit Vessel, Shiva's Guard, Eye of Skadi.",
                    $"Lots of enemy healing ({Names(heal)}) — Spirit Vessel, Shiva's Guard, Eye of Skadi."));
            var units = With(enemies, "illusions", "summons");
            if (units.Count >= 2)
                lines.Add(new("enemy_units", -1, $"Иллюзии и призывы у врагов ({Names(units)}) — нужен урон по площади: Mjollnir, Battle Fury, Radiance.",
                    $"Enemy illusions and summons ({Names(units)}) — you need area damage: Mjollnir, Battle Fury, Radiance."));
            if (Count(enemies, "initiator") == 0)
                lines.Add(new("enemy_no_init", 0, "У врагов нет явной инициации — будут ловить по одному, держитесь вместе.",
                    "The enemy has no clear initiator — they'll pick people off one by one, stay together."));
        }
        // риски первыми, затем план, затем сильные стороны
        return lines.OrderBy(l => l.Severity < 0 ? 0 : l.Severity == 0 ? 1 : 2).Take(MaxLines).ToList();
    }
}
