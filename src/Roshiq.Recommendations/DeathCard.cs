namespace Roshiq.Recommendations;

/// <summary>
/// 0.21: короткая карточка после смерти — «почему умер», пока ждёшь возрождения (как сводка смерти у Dota Plus).
/// Только то, что честно известно из GSI: время, место, золото при себе, ТП, выкуп. Источников урона GSI не даёт —
/// их не придумываем. Один совет — самый весомый из найденных.
/// </summary>
public static class DeathCard
{
    public const int GoldWarn = 1000;
    public const int BuybackFromSec = 1800;

    /// <summary>Строка фактов и, если есть, один совет. earlier — смерти этого матча до этой.</summary>
    public static (Insight Facts, Insight? Tip) Build(DeathEvent d, IReadOnlyList<DeathEvent> earlier, string? team)
    {
        var ru = new List<string> { GameTimers.Clock(d.Clock) };
        var en = new List<string> { GameTimers.Clock(d.Clock) };
        MapZone? zone = Zone(d, team);
        if (zone is MapZone z) { var n = MapZones.Name(z); ru.Add(n.Ru); en.Add(n.En); }
        if (d.GoldAtRisk > 0) { ru.Add($"{d.GoldAtRisk} золота при себе"); en.Add($"{d.GoldAtRisk} gold on hand"); }
        ru.Add(d.HadTeleport ? "ТП был" : "без ТП");
        en.Add(d.HadTeleport ? "had a TP" : "no TP");
        if (d.Clock >= BuybackFromSec && d.BuybackReady is bool bb)
        {
            ru.Add(bb ? "выкуп доступен" : "на выкуп не хватает");
            en.Add(bb ? "buyback ready" : "can't afford buyback");
        }
        var facts = new Insight("death_card", 0, string.Join(" · ", ru), string.Join(" · ", en));
        return (facts, Tip(d, earlier, team, zone));
    }

    private static Insight? Tip(DeathEvent d, IReadOnlyList<DeathEvent> earlier, string? team, MapZone? zone)
    {
        if (zone is MapZone z && z is not MapZone.OwnBase)
        {
            int same = earlier.Count(e => Zone(e, team) == z) + 1;
            if (same >= 3)
            {
                var n = MapZones.Name(z);
                return new Insight("death_zone", 3,
                    $"Уже {same}-я смерть здесь ({n.Ru}). Заходи туда с командой или после варда.",
                    $"Death #{same} in this area ({n.En}). Go there with your team or after a ward.");
            }
        }
        if (!d.HadTeleport && d.Clock >= 240)
            return new Insight("death_no_tp", 2,
                "Без ТП не уйти и не прийти на помощь — купи ТП сразу после возрождения.",
                "Without a TP you can't escape or join fights — buy one right after you respawn.");
        if (d.GoldAtRisk >= GoldWarn)
            return new Insight("death_gold", 2,
                $"При себе было {d.GoldAtRisk} золота — часть потеряна. Покупай до драки, а не после.",
                $"You carried {d.GoldAtRisk} gold — part of it is lost. Spend before fights, not after.");
        if (d.Clock >= BuybackFromSec && d.BuybackReady == false)
            return new Insight("death_buyback", 1,
                "Выкупа не было. После 30:00 держи запас золота на выкуп.",
                "No buyback. After 30:00 keep gold in reserve for it.");
        return null;
    }

    private static MapZone? Zone(DeathEvent d, string? team)
    {
        if (d.X is not int x || d.Y is not int y) return null;
        var (px, py) = MapZones.FromWorld(x, y);
        return MapZones.Classify(px, py, !string.Equals(team, "dire", StringComparison.OrdinalIgnoreCase));
    }
}
