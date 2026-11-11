using Roshiq.Core.Models;

namespace Roshiq.Recommendations;

/// <summary>Один момент разбора по ходу матча: когда и что произошло.</summary>
public sealed record StoryPoint(int Clock, Insight Insight);

/// <summary>Доход за отрезок матча (золото в минуту, добивания в минуту).</summary>
public sealed record FarmPhase(int From, int To, int GoldPerMin, double? LastHitsPerMin);

/// <summary>
/// Разбор по ходу матча только по записи GSI этого матча: где проседал фарм, где был простой без добиваний,
/// сколько золота лежало без покупок, где и как умирал. Работает с первой игры — сравнивать с историей не нужно.
/// Ничего не выдумывает: каждая строка — из точек графика матча (раз в 30 секунд) и записанных смертей.
/// </summary>
public static class MatchStory
{
    /// <summary>Отрезки матча для сравнения дохода.</summary>
    private static readonly int[] Bounds = { 0, 600, 1200, 1800, 2400, 3000, 3600, 4200, 4800 };
    /// <summary>Доход отрезка упал до этой доли от предыдущего — это провал (обычно доход только растёт).</summary>
    public const double DropShare = 0.75;
    /// <summary>Простой без добиваний (для кора) — от стольких секунд.</summary>
    public const int StallSec = 240;
    /// <summary>Сколько времени с большим запасом золота уже стоит упомянуть.</summary>
    public const int HoldSec = 240;
    /// <summary>Две смерти с таким промежутком — «подряд».</summary>
    public const int ChainSec = 150;

    /// <summary>
    /// Играл ли игрок кором — по фарму, а не только по записанной роли: роль с экрана драфта — это роль в поиске,
    /// а сыграть можно иначе (саппорт с Radiance и 230 добиваниями — это кор).
    /// </summary>
    public static bool PlaysCore(MatchRecord m)
    {
        double minutes = Math.Max(1, m.DurationSec / 60.0);
        int lh10 = m.LastHitsAt(600) ?? -1;
        double lhPerMin = m.LastHits / minutes;
        return m.Position switch
        {
            Position.Carry or Position.Mid or Position.Offlane => !(lh10 is >= 0 and < 15 && lhPerMin < 1.5),
            Position.SoftSupport or Position.HardSupport => lh10 >= 35 || (lhPerMin >= 4 && m.Gpm >= 480),
            _ => lh10 >= 30 || lhPerMin >= 4
        };
    }

    /// <summary>Записанная роль не совпадает с тем, как игрок фармил.</summary>
    public static bool RoleMismatch(MatchRecord m) => m.Position is Position p && PlaysCore(m) != (p is Position.Carry or Position.Mid or Position.Offlane);

    /// <summary>Доход по отрезкам 0–10, 10–20, … (только отрезки от 5 минут, где есть точки графика на концах).</summary>
    public static IReadOnlyList<FarmPhase> Phases(MatchRecord m)
    {
        var samples = m.Samples.Where(s => s.Gpm != null && s.Clock >= 0).OrderBy(s => s.Clock).ToList();
        var list = new List<FarmPhase>();
        if (samples.Count < 4) return list;
        for (int i = 0; i + 1 < Bounds.Length; i++)
        {
            int from = Bounds[i];
            if (from >= m.DurationSec) break;
            int to = Math.Min(Bounds[i + 1], m.DurationSec);
            if (to - from < 300) continue;
            var a = Nearest(samples, from);
            var b = Nearest(samples, to);
            if (Math.Abs(a.Clock - from) > 60 || Math.Abs(b.Clock - to) > 60 || b.Clock <= a.Clock) continue;
            double minutes = (b.Clock - a.Clock) / 60.0;
            int gold = (int)Math.Round((Earned(b) - Earned(a)) / minutes);
            double? lh = a.LastHits is int la && b.LastHits is int lb ? (lb - la) / minutes : null;
            list.Add(new FarmPhase(from, to, Math.Max(0, gold), lh));
        }
        return list;
    }

    /// <summary>Моменты матча: сначала по времени. Отрицательные — что пошло не так, положительные — что получилось.</summary>
    public static IReadOnlyList<StoryPoint> Build(MatchRecord m)
    {
        var points = new List<StoryPoint>();
        if (!m.FromGsi || m.Samples.Count < 4) return points;
        bool core = PlaysCore(m);
        var samples = m.Samples.Where(s => s.Clock >= 0).OrderBy(s => s.Clock).ToList();

        // --- провал дохода: отрезок заметно хуже предыдущего (обычно доход растёт с каждым отрезком)
        var phases = Phases(m);
        var drops = phases.Zip(phases.Skip(1)).Where(p => p.Second.From >= 600 && p.First.GoldPerMin > 0
            && p.Second.GoldPerMin <= p.First.GoldPerMin * DropShare && m.Covers(p.Second.From, p.Second.To)).ToList();
        if (drops.Count > 0)
        {
            var (prev, cur) = drops.OrderBy(p => (double)p.Second.GoldPerMin / p.First.GoldPerMin).First();
            points.Add(new(cur.From, new Insight("farm_drop", core ? -2 : -1,
                $"Доход упал на {C(cur.From)}–{C(cur.To)}: ~{cur.GoldPerMin} золота в минуту после ~{prev.GoldPerMin} на {C(prev.From)}–{C(prev.To)}.{DeathsIn(m, cur.From, cur.To, true)}",
                $"Income dropped at {C(cur.From)}–{C(cur.To)}: ~{cur.GoldPerMin} gold per minute after ~{prev.GoldPerMin} at {C(prev.From)}–{C(prev.To)}.{DeathsIn(m, cur.From, cur.To, false)}")));
        }
        else if (core && phases.Count >= 3 && phases[^1].GoldPerMin > phases[0].GoldPerMin)
            points.Add(new(phases[^1].From, new Insight("farm_grow", 1,
                $"Доход рос без провалов: ~{phases[0].GoldPerMin} → ~{phases[^1].GoldPerMin} золота в минуту к концу игры.",
                $"Income grew without dips: ~{phases[0].GoldPerMin} → ~{phases[^1].GoldPerMin} gold per minute by the end.")));

        // --- простой без добиваний (только кор): самый длинный отрезок после 8:00 без новых добиваний
        if (core && Stall(samples) is { } stall && stall.To - stall.From >= StallSec)
        {
            int deaths = m.DeathEvents.Count(d => d.Clock >= stall.From && d.Clock <= stall.To);
            string dRu = deaths > 0 ? $" За это время смертей: {deaths}." : "";
            string dEn = deaths > 0 ? $" Deaths in that time: {deaths}." : "";
            points.Add(new(stall.From, new Insight("stall", -2,
                $"С {C(stall.From)} по {C(stall.To)} ни одного добивания — {(stall.To - stall.From) / 60} мин без фарма.{dRu}",
                $"No last hits from {C(stall.From)} to {C(stall.To)} — {(stall.To - stall.From) / 60} min without farm.{dEn}")));
        }

        // --- золото без покупок: до 35-й минуты (позже золото часто копят на выкуп), не в последние 2 минуты
        var hold = samples.Where(s => s.Gold is int g && s.Clock >= 600 && s.Clock < 2100 && s.Clock < m.DurationSec - 120
            && g >= (s.Clock < 1200 ? 2000 : 3500)).ToList();
        if (hold.Count * 30 >= HoldSec)
        {
            var peak = hold.OrderByDescending(s => s.Gold).First();
            int min = hold.Count * 30 / 60;
            points.Add(new(hold[0].Clock, new Insight("gold_hold", core ? -2 : -1,
                $"~{min} мин при себе было много золота без покупок (больше всего — {peak.Gold} на {C(peak.Clock)}). Это несобранные предметы и риск потерять золото при смерти.",
                $"~{min} min carrying lots of unspent gold (peak {peak.Gold} at {C(peak.Clock)}). That is unbuilt items and gold at risk on death.")));
        }

        // --- где умирал: одна зона забирает заметную долю смертей
        bool radiant = !string.Equals(m.Team, "dire", StringComparison.OrdinalIgnoreCase);
        var located = m.DeathEvents.Where(d => d.X != null && d.Y != null)
            .Select(d => (Death: d, Zone: Zone(d, radiant))).ToList();
        if (located.Count >= 3)
        {
            var top = located.GroupBy(x => x.Zone).OrderByDescending(g => g.Count()).First();
            int n = top.Count();
            if (n >= 3 && n * 10 >= located.Count * 4)
            {
                var (ru, en) = Where(top.Key);
                int first = top.Min(x => x.Death.Clock);
                points.Add(new(first, new Insight("death_zone", top.Key == MapZone.EnemyJungle ? -2 : -1,
                    $"{n} из {located.Count} смертей — {ru} ({string.Join(", ", top.Select(x => C(x.Death.Clock)).Take(4))}).{ZoneHint(top.Key, true)}",
                    $"{n} of {located.Count} deaths {en} ({string.Join(", ", top.Select(x => C(x.Death.Clock)).Take(4))}).{ZoneHint(top.Key, false)}")));
            }
        }

        // --- смерти подряд
        var clocks = m.DeathEvents.Select(d => d.Clock).OrderBy(c => c).ToList();
        var chains = clocks.Zip(clocks.Skip(1)).Where(p => p.Second - p.First <= ChainSec).ToList();
        if (chains.Count > 0)
        {
            var (a, b) = chains[0];
            string moreRu = chains.Count > 1 ? $" Так было ещё {chains.Count - 1} раз." : "";
            string moreEn = chains.Count > 1 ? $" It happened {chains.Count - 1} more time(s)." : "";
            points.Add(new(a, new Insight("death_chain", chains.Count >= 3 ? -2 : -1,
                $"Две смерти подряд: {C(a)} и {C(b)} — после смерти стоит переждать и вернуться с командой.{moreRu}",
                $"Two deaths in a row: {C(a)} and {C(b)} — after dying, regroup before going back in.{moreEn}")));
        }

        // --- поздняя игра без выкупа
        var noBuyback = m.DeathEvents.Where(d => d.Clock >= 1800 && d.BuybackReady == false).ToList();
        if (noBuyback.Count >= 2)
            points.Add(new(noBuyback[0].Clock, new Insight("no_buyback", -2,
                $"После 30:00 — {Deaths(noBuyback.Count)} без выкупа ({string.Join(", ", noBuyback.Select(d => C(d.Clock)).Take(4))}): не хватало золота или выкуп был на перезарядке.",
                $"After 30:00 — {noBuyback.Count} deaths without buyback ({string.Join(", ", noBuyback.Select(d => C(d.Clock)).Take(4))}): not enough gold or it was on cooldown.")));

        // --- чистая линия
        if (m.DurationSec >= 1200 && m.Covers(0, 600) && !m.DeathEvents.Any(d => d.Clock < 600) && m.UnlocatedDeaths == 0)
            points.Add(new(600, new Insight("clean_lane", 1, "Первые 10 минут без смертей.", "No deaths in the first 10 minutes.")));

        return points.OrderBy(p => p.Clock).ToList();
    }

    private static MatchSample Nearest(List<MatchSample> samples, int clock) =>
        samples.OrderBy(s => Math.Abs(s.Clock - clock)).First();

    /// <summary>Сколько золота заработано к моменту (GSI даёт среднее золото в минуту с начала игры).</summary>
    private static double Earned(MatchSample s) => (s.Gpm ?? 0) * Math.Max(0, s.Clock) / 60.0;

    private static (int From, int To)? Stall(List<MatchSample> samples)
    {
        var lh = samples.Where(s => s.LastHits != null && s.Clock >= 480).ToList();
        (int, int)? best = null;
        int start = 0;
        for (int j = 1; j < lh.Count; j++)
        {
            // пропуск в записи — не простой, а отсутствие данных
            if (lh[j].Clock - lh[j - 1].Clock > 90) { start = j; continue; }
            if (lh[j].LastHits > lh[start].LastHits) { start = j; continue; }
            var w = (lh[start].Clock, lh[j].Clock);
            if (best == null || w.Item2 - w.Item1 > best.Value.Item2 - best.Value.Item1) best = w;
        }
        return best;
    }

    private static MapZone Zone(DeathEvent d, bool radiant)
    {
        var (x, y) = MapZones.FromWorld(d.X!.Value, d.Y!.Value);
        return MapZones.Classify(x, y, radiant);
    }

    private static (string Ru, string En) Where(MapZone z) => z switch
    {
        MapZone.OwnBase => ("на своей базе", "at your own base"),
        MapZone.EnemyBase => ("у вражеской базы", "at the enemy base"),
        MapZone.TopLane => ("на верхней линии", "in the top lane"),
        MapZone.MidLane => ("в центре", "in the mid lane"),
        MapZone.BotLane => ("на нижней линии", "in the bottom lane"),
        MapZone.River => ("у реки", "around the river"),
        MapZone.OwnJungle => ("в своём лесу", "in your own jungle"),
        _ => ("во вражеском лесу", "in the enemy jungle"),
    };

    private static string ZoneHint(MapZone z, bool ru) => z switch
    {
        MapZone.EnemyJungle => ru ? " Туда стоит заходить только с видимостью и командой." : " Go there only with vision and your team.",
        MapZone.River => ru ? " Река — место встреч: смотрите на мини-карту перед переходом." : " The river is where fights start: check the minimap before crossing.",
        MapZone.EnemyBase => ru ? " Это смерти при штурме — проверьте, хватало ли сил на заход." : " These are siege deaths — check whether the push was strong enough.",
        _ => ""
    };

    private static string DeathsIn(MatchRecord m, int from, int to, bool ru)
    {
        int n = m.DeathEvents.Count(d => d.Clock >= from && d.Clock < to);
        if (n == 0) return "";
        return ru ? $" На этом отрезке — {Deaths(n)}." : $" {n} death(s) in that stretch.";
    }

    private static string Deaths(int n)
    {
        int d = n % 10, h = n % 100;
        string w = d == 1 && h != 11 ? "смерть" : d is >= 2 and <= 4 && h is < 12 or > 14 ? "смерти" : "смертей";
        return $"{n} {w}";
    }

    private static string C(int s) => $"{s / 60}:{Math.Abs(s % 60):00}";
}
