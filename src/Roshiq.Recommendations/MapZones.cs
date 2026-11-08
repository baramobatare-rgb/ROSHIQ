using Roshiq.Core.Models;

namespace Roshiq.Recommendations;

/// <summary>Зона карты, где случилась смерть (с точки зрения игрока: «своя» / «вражеская» половина).</summary>
public enum MapZone { OwnBase, EnemyBase, TopLane, MidLane, BotLane, River, OwnJungle, EnemyJungle }

public sealed record ZoneShare(MapZone Zone, int Count, double Share);

/// <summary>Сводка смертей по карте: зоны, фазы, сколько без телепорта и сколько золота под риском.</summary>
public sealed record DeathMapSummary(int Total, int Matches, IReadOnlyList<ZoneShare> Zones, int Early, int Mid, int Late,
    int WithoutTeleport, int AvgGoldAtRisk);

/// <summary>
/// Грубая разметка карты Dota по долям 0…1 ((0,0) — угол базы Radiant, (1,1) — угол базы Dire).
/// Только геометрия схемы, без игровых файлов: базы в углах, линии по краям и по диагонали, река — обратная диагональ.
/// </summary>
public static class MapZones
{
    public const double BaseSize = 0.2, LaneWidth = 0.12, MidWidth = 0.07, RiverWidth = 0.07;

    public static MapZone Classify(double x, double y, bool radiant)
    {
        bool inRadiantBase = x < BaseSize && y < BaseSize;
        bool inDireBase = x > 1 - BaseSize && y > 1 - BaseSize;
        if (inRadiantBase) return radiant ? MapZone.OwnBase : MapZone.EnemyBase;
        if (inDireBase) return radiant ? MapZone.EnemyBase : MapZone.OwnBase;
        if (x < LaneWidth || y > 1 - LaneWidth) return MapZone.TopLane;
        if (y < LaneWidth || x > 1 - LaneWidth) return MapZone.BotLane;
        if (Math.Abs(x - y) < MidWidth) return MapZone.MidLane;
        if (Math.Abs(x + y - 1) < RiverWidth) return MapZone.River;
        bool radiantSide = x + y < 1;
        return radiantSide == radiant ? MapZone.OwnJungle : MapZone.EnemyJungle;
    }

    public static (string Ru, string En) Name(MapZone z) => z switch
    {
        MapZone.OwnBase => ("Своя база", "Own base"),
        MapZone.EnemyBase => ("Вражеская база", "Enemy base"),
        MapZone.TopLane => ("Верхняя линия", "Top lane"),
        MapZone.MidLane => ("Центр", "Mid lane"),
        MapZone.BotLane => ("Нижняя линия", "Bottom lane"),
        MapZone.River => ("Река", "River"),
        MapZone.OwnJungle => ("Свой лес", "Own jungle"),
        _ => ("Вражеский лес", "Enemy jungle"),
    };

    /// <summary>Мировые координаты GSI (примерно −8200…8200) → доли карты.</summary>
    public static (double X, double Y) FromWorld(int x, int y) =>
        (Math.Clamp((x + 8200) / 16400.0, 0, 1), Math.Clamp((y + 8200) / 16400.0, 0, 1));

    /// <summary>Сводка по смертям с координатами (только матчи через GSI; сторона берётся из матча, по умолчанию Radiant).</summary>
    public static DeathMapSummary Summarize(IEnumerable<(MatchRecord Match, DeathEvent Death)> deaths)
    {
        var list = deaths.Where(x => x.Death.X != null && x.Death.Y != null).ToList();
        var zones = list.Select(x =>
            {
                var (px, py) = FromWorld(x.Death.X!.Value, x.Death.Y!.Value);
                return Classify(px, py, !string.Equals(x.Match.Team, "dire", StringComparison.OrdinalIgnoreCase));
            })
            .GroupBy(z => z).Select(g => new ZoneShare(g.Key, g.Count(), list.Count == 0 ? 0 : (double)g.Count() / list.Count))
            .OrderByDescending(z => z.Count).ToList();
        return new DeathMapSummary(list.Count, list.Select(x => x.Match.Id).Distinct().Count(), zones,
            list.Count(x => GamePhases.Of(x.Death.Clock) == GamePhase.Lane), list.Count(x => GamePhases.Of(x.Death.Clock) == GamePhase.Mid),
            list.Count(x => GamePhases.Of(x.Death.Clock) == GamePhase.Late),
            list.Count(x => !x.Death.HadTeleport),
            list.Count == 0 ? 0 : (int)Math.Round(list.Average(x => x.Death.GoldAtRisk)));
    }
}
