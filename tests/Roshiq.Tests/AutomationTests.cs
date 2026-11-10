using Roshiq.Core.Models;
using Roshiq.GSI;
using Roshiq.Recommendations;
using Xunit;

namespace Roshiq.Tests;

public class AutomationTests
{
    private static Hero H(params Position[] positions) =>
        new(9001, "test_hero", "Test Hero", "Тест", PrimaryAttribute.Agility, positions, Array.Empty<string>());

    private static LaneRoleDetector Walk(int x, int y, int lastHits)
    {
        var d = new LaneRoleDetector();
        for (int clock = 0; clock <= 150; clock += 5) d.Observe(clock, x, y, clock == 150 ? lastHits : 0);
        return d;
    }

    [Theory]
    [InlineData(0, 0, Lane.Mid)]
    [InlineData(-1500, -1300, Lane.Mid)]
    [InlineData(-6200, 1800, Lane.Top)]
    [InlineData(-3000, 6000, Lane.Top)]
    [InlineData(4900, -6100, Lane.Bottom)]
    [InlineData(6300, 1000, Lane.Bottom)]
    [InlineData(-6800, -6600, Lane.Unknown)] // фонтан Radiant
    [InlineData(6500, 6000, Lane.Unknown)]   // база Dire
    [InlineData(300, -3500, Lane.Unknown)]   // лес
    public void Lanes_FromCoordinates(int x, int y, Lane expected) => Assert.Equal(expected, MapLanes.Classify(x, y));

    [Fact]
    public void SafeLane_CoreWithLastHits_IsCarry()
    {
        var d = Walk(5200, -6200, lastHits: 14);
        var g = d.TryDecide(150, H(Position.Carry, Position.Mid), "radiant", () => Array.Empty<MatchRecord>(), Position.HardSupport);
        Assert.NotNull(g);
        Assert.Equal(Position.Carry, g!.Position);
        Assert.Equal(Lane.Bottom, g.Lane);
        Assert.True(d.Decided);
        Assert.Null(d.TryDecide(160, H(Position.Carry), "radiant", () => Array.Empty<MatchRecord>(), Position.Carry)); // один раз
    }

    [Fact]
    public void SafeLane_SupportHero_NoLastHits_IsHardSupport()
    {
        var g = Walk(5200, -6200, lastHits: 1).TryDecide(150, H(Position.HardSupport, Position.SoftSupport), "radiant",
            () => Array.Empty<MatchRecord>(), Position.Carry);
        Assert.Equal(Position.HardSupport, g!.Position);
    }

    [Fact]
    public void DireTopIsSafe_RadiantTopIsOff()
    {
        var hero = H(Position.Offlane, Position.Carry);
        var dire = Walk(-3000, 6200, lastHits: 12).TryDecide(150, hero, "dire", () => Array.Empty<MatchRecord>(), Position.Mid);
        Assert.Equal(Position.Carry, dire!.Position); // у Dire верх — лёгкая линия: керри или пятёрка
        var radiant = Walk(-3000, 6200, lastHits: 12).TryDecide(150, hero, "radiant", () => Array.Empty<MatchRecord>(), Position.Mid);
        Assert.Equal(Position.Offlane, radiant!.Position);
    }

    [Fact]
    public void Mid_IsMid()
    {
        var g = Walk(-800, -600, lastHits: 20).TryDecide(150, H(Position.Carry), "radiant", () => Array.Empty<MatchRecord>(), Position.Carry);
        Assert.Equal(Position.Mid, g!.Position);
    }

    [Fact]
    public void History_TipsTheBalance()
    {
        var hero = H(Position.SoftSupport, Position.Offlane);
        var history = Enumerable.Range(0, 5).Select(i => new MatchRecord { Id = "h" + i, Source = "GSI", HeroId = hero.Id, Position = Position.Offlane }).ToList();
        // на сложной линии, добиваний средне — решает история игрока
        var g = Walk(-6200, 1500, lastHits: 6).TryDecide(150, hero, "radiant", () => history, Position.SoftSupport);
        Assert.Equal(Position.Offlane, g!.Position);
    }

    [Fact]
    public void Roaming_GivesUpWithoutGuess()
    {
        var d = new LaneRoleDetector();
        for (int clock = 0; clock <= 240; clock += 5) d.Observe(clock, 300, -3500, 2); // всё время в лесу
        Assert.Null(d.TryDecide(150, H(Position.Carry), "radiant", () => Array.Empty<MatchRecord>(), Position.Carry));
        Assert.Null(d.TryDecide(240, H(Position.Carry), "radiant", () => Array.Empty<MatchRecord>(), Position.Carry));
        Assert.True(d.Decided);
    }

    [Fact]
    public void LowConfidenceRole_RetriesWhenLastHitsClarify()
    {
        var d = new LaneRoleDetector();
        for (int clock = 20, sample = 0; clock <= 150; clock += 5, sample++)
            d.Observe(clock, sample < 16 ? 5200 : 300, sample < 16 ? -6200 : -3500, null);

        var hero = H();
        var first = d.TryDecide(150, hero, "radiant", () => Array.Empty<MatchRecord>(), Position.Mid);
        Assert.NotNull(first);
        Assert.True(first!.Confidence < LaneRoleDetector.MinConfidence);
        Assert.False(d.Decided);
        Assert.Null(d.TryDecide(151, hero, "radiant", () => Array.Empty<MatchRecord>(), Position.Mid));

        d.Observe(155, 5200, -6200, 14);
        var second = d.TryDecide(155, hero, "radiant", () => Array.Empty<MatchRecord>(), Position.Mid);
        Assert.Equal(Position.Carry, second!.Position);
        Assert.True(second.Confidence >= LaneRoleDetector.MinConfidence);
        Assert.True(d.Decided);
    }

    [Theory]
    [InlineData("dota_lane_selection_safelane", Position.Carry)]
    [InlineData("dota_lane_selection_midlane", Position.Mid)]
    [InlineData("dota_lane_selection_offlane", Position.Offlane)]
    [InlineData("dota_lane_selection_soft_support", Position.SoftSupport)]
    [InlineData("dota_lane_selection_hard_support", Position.HardSupport)]
    public void RankedRoleQueue_OneCheckedRoleIsKnown(string key, Position expected)
    {
        var config = "\"dota_ranked_role_queue\" \"true\"\n\"" + key + "\" \"true\"";
        Assert.Equal(expected, RankedRoleSelection.ParseSingle(config));
    }

    [Fact]
    public void RankedRoleQueue_MultipleOrClassicRolesAreUnknown()
    {
        const string roles = "\"dota_lane_selection_offlane\" \"true\"\n\"dota_lane_selection_hard_support\" \"true\"";
        Assert.Null(RankedRoleSelection.ParseSingle("\"dota_ranked_role_queue\" \"true\"\n" + roles));
        Assert.Null(RankedRoleSelection.ParseSingle("\"dota_ranked_role_queue\" \"false\"\n" + roles));
        Assert.Null(RankedRoleSelection.ParseSingle("\"dota_ranked_role_queue\" \"true\""));
        Assert.Null(RankedRoleSelection.ParseSingle("\"dota_ranked_role_queue\" \"true\"\n\"dota_ranked_role_queue_automatically_select_all\" \"true\"\n\"dota_lane_selection_safelane\" \"true\""));
    }

    [Fact]
    public void RankedRoleQueue_ReadsOnlyTheSpecifiedAccount()
    {
        var root = Path.Combine(Path.GetTempPath(), "roshiq-roles-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(root, "userdata", "42", "570", "local", "cfg", "machine_convars.vcfg");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "\"dota_ranked_role_queue\" \"true\"\n\"dota_lane_selection_offlane\" \"true\"");
            Assert.Equal(Position.Offlane, RankedRoleSelection.ReadSingle(root, 42));
            Assert.Null(RankedRoleSelection.ReadSingle(root, 43));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void GameWatcher_IgnoresInitialState_ThenReportsChanges()
    {
        var w = new GameLaunchWatcher(onAfter: 3, offAfter: 5);
        for (int i = 0; i < 5; i++) Assert.Null(w.Observe(true)); // игра уже была открыта при запуске
        Assert.True(w.Running);
        for (int i = 0; i < 4; i++) Assert.Null(w.Observe(false));
        Assert.False(w.Observe(false));
        Assert.Null(w.Observe(true));
        Assert.Null(w.Observe(false)); // мигание не считается
        Assert.Null(w.Observe(true));
        Assert.Null(w.Observe(true));
        Assert.True(w.Observe(true));
    }

    [Fact]
    public void Gsi_ParsesPositionAndRespawn()
    {
        const string json = """
        { "hero": { "id": 1, "name": "npc_dota_hero_antimage", "alive": false, "respawn_seconds": 23, "xpos": -5120, "ypos": 1834 } }
        """;
        var s = GsiParser.Parse(json, null);
        Assert.Equal(-5120, s.HeroX);
        Assert.Equal(1834, s.HeroY);
        Assert.Equal(23, s.RespawnSeconds);
        Assert.False(s.HeroAlive);
    }
}
