using Roshiq.Core.Models;
using Roshiq.Core.Settings;
using Roshiq.GSI;
using Roshiq.Recommendations;
using Xunit;

namespace Roshiq.Tests;

public class CoachingTests
{
    private static MatchRecord Gsi(string id, int heroId = 2, Position pos = Position.Offlane, int? lh10 = null, int gpm = 500,
        int deaths = 0, DateTime? when = null, MatchResult result = MatchResult.Unknown)
    {
        var m = new MatchRecord
        {
            Id = "gsi-" + id, MatchId = id, Source = "GSI", HeroId = heroId, Position = pos, Gpm = gpm, Deaths = deaths,
            StartedUtc = when ?? DateTime.UtcNow, Result = result, DurationSec = 2400, Ended = true
        };
        m.Coverage.Add(new CoverageSpan { From = 0, To = 2400 });     // полная запись (неполные цели не засчитывают)
        if (lh10 != null) m.Samples.Add(new MatchSample { Clock = 600, LastHits = lh10 });
        return m;
    }

    private static MatchTick Tick(int clock, int deaths, int gold, int? unreliable, bool tp) =>
        new("900", "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS", clock, "radiant", null, 2, "axe", 12, 1, deaths, 3, 80, 4, 450, 500, gold,
            new Dictionary<string, string> { ["slot0"] = "item_blink", ["teleport0"] = tp ? "item_tpscroll" : "empty" },
            Position.Offlane, DateTime.UtcNow, unreliable, 1500, 0);

    [Fact]
    public void Recorder_RecordsDeathFromPreviousSnapshot()
    {
        var r = new MatchRecorder();
        r.Observe(Tick(600, 0, 1800, 1500, tp: false));
        r.Observe(Tick(610, 1, 300, 0, tp: false));
        var d = Assert.Single(r.Current!.DeathEvents);
        Assert.Equal(610, d.Clock);
        Assert.Equal(1500, d.GoldAtRisk);
        Assert.False(d.HadTeleport);
        Assert.True(d.BuybackReady); // 1800 золота ≥ 1500, перезарядка 0
        Assert.Equal(12, d.Level);
    }

    [Fact]
    public void DeathSummary_CountsPhasesAndMistakes()
    {
        var m = Gsi("1");
        m.DeathEvents.AddRange(new[]
        {
            new DeathEvent { Clock = 300, GoldAtRisk = 200, HadTeleport = true },
            new DeathEvent { Clock = 900, GoldAtRisk = 1500, HadTeleport = false },
            new DeathEvent { Clock = 2000, GoldAtRisk = 2500, HadTeleport = true, BuybackReady = false },
        });
        var s = DeathAnalysis.Analyze(m)!;
        Assert.Equal(3, s.Total);
        Assert.Equal(1, s.Early);
        Assert.Equal(1, s.Mid);
        Assert.Equal(1, s.Late);
        Assert.Equal(2, s.WithLotsOfGold);
        Assert.Equal(1, s.WithoutTeleport);
        Assert.Equal(1, s.LateWithoutBuyback);
        Assert.Equal(2500, s.MaxGoldAtRisk);
        Assert.Null(DeathAnalysis.Analyze(new MatchRecord { Id = "od-1", Source = "OpenDota" }));
    }

    [Fact]
    public void Pace_UsesSameHeroThenRole()
    {
        var history = new[] { Gsi("1", lh10: 50), Gsi("2", lh10: 60), Gsi("3", lh10: 70), Gsi("4", heroId: 9, lh10: 10) };
        var pace = new PaceAdvisor(history, 2, Position.Offlane);
        var usual = pace.UsualLastHits(600)!.Value;
        Assert.Equal(60, usual.Avg);
        Assert.True(usual.SameHero);

        var check = pace.CheckpointDue(605, 45);
        Assert.NotNull(check);
        Assert.Equal(-15, check!.Delta);
        Assert.Null(pace.CheckpointDue(610, 45)); // один раз

        var other = new PaceAdvisor(history, 77, Position.Offlane);
        Assert.False(other.UsualLastHits(600)!.Value.SameHero);
        Assert.Null(new PaceAdvisor(history.Take(2), 2, Position.Offlane).UsualLastHits(600)); // мало матчей
    }

    [Fact]
    public void Goal_SuggestsGoldOnDeath_AndEvaluates()
    {
        var m = Gsi("10");
        for (int i = 0; i < 3; i++) m.DeathEvents.Add(new DeathEvent { Clock = 900 + i * 60, GoldAtRisk = 1600, HadTeleport = true });
        var goal = GoalEngine.Suggest(m, Array.Empty<MatchRecord>());
        Assert.NotNull(goal);
        Assert.Equal(GoalKind.GoldOnDeath, goal!.Kind);
        Assert.Equal(1000, goal.Target);

        var clean = Gsi("11");
        clean.DeathEvents.Add(new DeathEvent { Clock = 1000, GoldAtRisk = 300, HadTeleport = true });
        Assert.Equal((true, 0), GoalEngine.Evaluate(goal, clean));
        Assert.Equal((false, 3), GoalEngine.Evaluate(goal, m));
        Assert.Null(GoalEngine.Evaluate(goal, new MatchRecord { Id = "od", Source = "OpenDota" }));
    }

    [Fact]
    public void Goal_NoIssues_NoGoal()
    {
        var m = Gsi("12");
        m.DeathEvents.Add(new DeathEvent { Clock = 1000, GoldAtRisk = 100, HadTeleport = true });
        Assert.Null(GoalEngine.Suggest(m, Array.Empty<MatchRecord>()));
    }

    [Fact]
    public void GoalStore_TracksAndClosesAfterTwoSuccesses()
    {
        var path = Path.Combine(Path.GetTempPath(), "roshiq-goals-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new GoalStore(path);
            var bad = Gsi("20");
            bad.DeathEvents.AddRange(Enumerable.Range(0, 3).Select(i => new DeathEvent { Clock = 800 + i, GoldAtRisk = 2000, HadTeleport = true }));
            Assert.Null(store.OnMatchSaved(bad, new[] { bad }));
            Assert.Equal(GoalKind.GoldOnDeath, store.Active!.Kind);

            var ok1 = Gsi("21");
            var r1 = store.OnMatchSaved(ok1, new[] { bad, ok1 });
            Assert.True(r1!.Value.Result.Achieved);
            Assert.NotNull(store.Active);

            Assert.Null(store.OnMatchSaved(ok1, new[] { bad, ok1 })); // тот же матч второй раз не считается

            var ok2 = Gsi("22");
            Assert.True(store.OnMatchSaved(ok2, new[] { bad, ok1, ok2 })!.Value.Result.Achieved);
            Assert.Null(store.Active); // цель освоена, новых проблем нет

            var again = new GoalStore(path);
            Assert.Equal(2, again.Goals.Single().Results.Count);
        }
        finally { try { File.Delete(path); } catch { } }
    }

    [Fact]
    public void Trends_CompareHalves()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var list = Enumerable.Range(0, 10).Select(i => Gsi("t" + i, gpm: 400 + i * 10, when: start.AddDays(i))).ToList();
        var s = Trends.Build("gpm", list, m => m.Gpm);
        Assert.Equal(10, s.Points.Count);
        Assert.Equal(400, s.Points[0].Value);
        Assert.Equal(420, s.Before);
        Assert.Equal(470, s.After);
        Assert.True(s.Change > 0);
        Assert.Null(Trends.Build("gpm", list.Take(4), m => m.Gpm).Before);
    }

    [Fact]
    public void Trends_WinRate()
    {
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var list = Enumerable.Range(0, 20).Select(i => Gsi("w" + i, when: start.AddDays(i),
            result: i >= 10 ? (i < 17 ? MatchResult.Win : MatchResult.Loss) : MatchResult.Loss)).ToList();
        var (recent, previous) = Trends.WinRate(list);
        Assert.Equal(0.7, recent!.Value, 3);
        Assert.Equal(0.0, previous!.Value, 3);
    }

    [Fact]
    public void Briefing_FindsThreatsAndLane()
    {
        var invis = TestData.Db.Heroes.Where(h => h.Has("invisibility")).Take(1);
        var others = TestData.Db.Heroes.Where(h => !h.Has("invisibility")).Take(4);
        var enemies = invis.Concat(others).ToList();
        var b = BriefingBuilder.Build(enemies, Position.Carry, maxLines: 50);
        Assert.Contains(b.Lines, l => l.Heroes.Any(h => h.Has("invisibility")) && l.En.Contains("Invisibility"));
        Assert.Equal(new[] { Position.Offlane, Position.SoftSupport }, BriefingBuilder.LaneOpponentPositions(Position.Carry));
        Assert.Equal(new[] { Position.Mid }, BriefingBuilder.LaneOpponentPositions(Position.Mid));
        Assert.True(BriefingBuilder.Build(enemies, Position.Carry).Lines.Count <= 6);
    }

    [Fact]
    public void Buyback_AlertOnlyLateAndWhenShort()
    {
        var slots = new Dictionary<string, string> { ["teleport0"] = "item_tpscroll" };
        var s = new AlertSettings();
        Assert.Contains(MatchAlerts.NoBuyback, MatchAlerts.Conditions(new InventoryState(2000, true, 500, 100, slots, Position.Carry, 1500, 0), s));
        Assert.DoesNotContain(MatchAlerts.NoBuyback, MatchAlerts.Conditions(new InventoryState(2000, true, 2000, 100, slots, Position.Carry, 1500, 0), s));
        Assert.DoesNotContain(MatchAlerts.NoBuyback, MatchAlerts.Conditions(new InventoryState(2000, true, 500, 100, slots, Position.Carry, 1500, 40), s));
        Assert.DoesNotContain(MatchAlerts.NoBuyback, MatchAlerts.Conditions(new InventoryState(900, true, 500, 100, slots, Position.Carry, 1500, 0), s));
    }

    [Fact]
    public void Alerts_TrackIgnoredAndAdapt()
    {
        var a = new MatchAlerts();
        var s = new AlertSettings();
        var noTp = new Dictionary<string, string> { ["slot0"] = "item_blink", ["teleport0"] = "empty" };
        InventoryState St(int clock) => new(clock, true, 500, null, noTp, Position.Carry);
        for (int c = 300; c <= 319; c++) a.Tick(St(c), s);
        Assert.Equal(MatchAlerts.NoTeleport, a.Tick(St(320), s));            // выдержка 20 с при тактах раз в секунду
        for (int c = 321; c <= 380; c++) a.Tick(St(c), s);
        Assert.Equal(1, a.Ignored[MatchAlerts.NoTeleport]);

        var history = Enumerable.Range(0, 2).Select(i => new MatchRecord
        {
            Id = "h" + i, Source = "GSI", StartedUtc = DateTime.UtcNow.AddDays(-i),
            Alerts = { [MatchAlerts.NoTeleport] = 2 }, AlertsIgnored = { [MatchAlerts.NoTeleport] = 2 }
        });
        var mult = MatchAlerts.AdaptiveMultipliers(history);
        Assert.Equal(3.0, mult[MatchAlerts.NoTeleport]);
        Assert.False(mult.ContainsKey(MatchAlerts.ItemsInStash));
    }

    [Fact]
    public void StackTimer_OnlyForSupports()
    {
        var stack = TestData.Db.Timers.Single(t => t.Key == "stack");
        Assert.True(stack.AppliesTo(Position.HardSupport));
        Assert.True(stack.AppliesTo(Position.SoftSupport));
        Assert.False(stack.AppliesTo(Position.Carry));
        Assert.Equal(173, GameTimers.NextOccurrence(stack, 120));
        Assert.True(TestData.Db.Timers.Single(t => t.Key == "power_rune").AppliesTo(Position.Carry));
    }

    [Fact]
    public void Gsi_ParsesBuyback()
    {
        const string json = """
        { "hero": { "id": 2, "name": "npc_dota_hero_axe", "alive": true, "buyback_cost": 1234, "buyback_cooldown": 0 } }
        """;
        var s = GsiParser.Parse(json, null);
        Assert.Equal(1234, s.BuybackCost);
        Assert.Equal(0, s.BuybackCooldown);
    }
}
