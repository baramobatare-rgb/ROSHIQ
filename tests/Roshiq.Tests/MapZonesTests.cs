using Roshiq.Recommendations;
using Xunit;

namespace Roshiq.Tests;

/// <summary>0.17.1: зоны карты смертей.</summary>
public class MapZonesTests
{
    [Theory]
    [InlineData(0.05, 0.05, true, MapZone.OwnBase)]
    [InlineData(0.05, 0.05, false, MapZone.EnemyBase)]
    [InlineData(0.95, 0.95, true, MapZone.EnemyBase)]
    [InlineData(0.05, 0.60, true, MapZone.TopLane)]
    [InlineData(0.60, 0.95, true, MapZone.TopLane)]
    [InlineData(0.60, 0.05, true, MapZone.BotLane)]
    [InlineData(0.50, 0.52, true, MapZone.MidLane)]
    [InlineData(0.30, 0.70, true, MapZone.River)]
    [InlineData(0.35, 0.25, true, MapZone.OwnJungle)]
    [InlineData(0.35, 0.25, false, MapZone.EnemyJungle)]
    [InlineData(0.70, 0.80, true, MapZone.EnemyJungle)]
    public void Classify(double x, double y, bool radiant, MapZone expected) => Assert.Equal(expected, MapZones.Classify(x, y, radiant));

    [Fact]
    public void Summary_CountsZonesPhasesAndTeleports()
    {
        var radiant = new Roshiq.Recommendations.MatchRecord { Id = "r", Team = "radiant" };
        var dire = new Roshiq.Recommendations.MatchRecord { Id = "d", Team = "dire" };
        // мировые координаты: (−7400,−7400) ≈ угол Radiant, (2000,4500) ≈ половина Dire
        var deaths = new[]
        {
            (radiant, new DeathEvent { X = 2000, Y = 4500, Clock = 300, HadTeleport = false, GoldAtRisk = 200 }),
            (radiant, new DeathEvent { X = 2200, Y = 4300, Clock = 900, HadTeleport = true, GoldAtRisk = 400 }),
            (dire, new DeathEvent { X = 2000, Y = 4500, Clock = 2000, HadTeleport = false, GoldAtRisk = 600 }),
            (dire, new DeathEvent { Clock = 100 }),                        // без координат — не считается
        };
        var s = MapZones.Summarize(deaths);
        Assert.Equal(3, s.Total);
        Assert.Equal(2, s.Matches);
        Assert.Equal(MapZone.EnemyJungle, s.Zones[0].Zone);            // у Radiant это вражеский лес
        Assert.Equal(2, s.Zones[0].Count);
        Assert.Contains(s.Zones, z => z.Zone == MapZone.OwnJungle && z.Count == 1);   // у Dire — свой
        Assert.Equal(1, s.Early);
        Assert.Equal(1, s.Mid);
        Assert.Equal(1, s.Late);
        Assert.Equal(2, s.WithoutTeleport);
        Assert.Equal(400, s.AvgGoldAtRisk);
    }
}
