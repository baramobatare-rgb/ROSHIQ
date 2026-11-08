using Roshiq.Core.Models;
using Roshiq.Core.Settings;
using Roshiq.Recommendations;
using Xunit;

namespace Roshiq.Tests;

/// <summary>0.17.8: советы не делают выводов по чужой роли, по пропуску данных и по одному таймеру.</summary>
public class AdviceIntegrityTests
{
    // ------------------------------------------------------------------ подсказки инвентаря после пропуска GSI

    private static readonly Dictionary<string, string> NoTp = new() { ["slot0"] = "item_blink", ["teleport0"] = "empty" };
    private static InventoryState NoTpAt(int clock) => new(clock, true, 500, null, NoTp, Position.Carry);

    [Fact]
    public void Alerts_GapIsNotSustain_NoInstantAlertAfterResume()
    {
        var a = new MatchAlerts();
        var s = new AlertSettings();
        Assert.Null(a.Tick(NoTpAt(300), s));
        Assert.Null(a.Tick(NoTpAt(600), s));                                 // непрерывности не видели — выдержка заново
        for (int c = 601; c < 620; c++) Assert.Null(a.Tick(NoTpAt(c), s));
        Assert.Equal(MatchAlerts.NoTeleport, a.Tick(NoTpAt(620), s));        // 20 с после возврата данных
    }

    [Fact]
    public void Alerts_GapAfterShow_NeitherIgnoredNorFollowed_CountsAndCooldownKept()
    {
        var a = new MatchAlerts();
        var s = new AlertSettings();
        for (int c = 300; c <= 319; c++) a.Tick(NoTpAt(c), s);
        Assert.Equal(MatchAlerts.NoTeleport, a.Tick(NoTpAt(320), s));
        for (int c = 600; c <= 679; c++) a.Tick(NoTpAt(c), s);              // пропуск 321…599, потом условие держится
        Assert.False(a.Ignored.ContainsKey(MatchAlerts.NoTeleport));         // показ 320 оценить нельзя
        Assert.False(a.Followed.ContainsKey(MatchAlerts.NoTeleport));
        Assert.Equal(2, a.Counts[MatchAlerts.NoTeleport]);                   // счётчик общий: 320 и снова после выдержки (620)

        var b = new MatchAlerts();
        for (int c = 300; c <= 321; c++) b.Tick(NoTpAt(c), s);
        var withTp = new Dictionary<string, string> { ["slot0"] = "item_blink", ["teleport0"] = "item_tpscroll" };
        b.Tick(new InventoryState(600, true, 400, null, withTp, Position.Carry), s);   // ТП есть, но когда купил — не видели
        Assert.False(b.Followed.ContainsKey(MatchAlerts.NoTeleport));
    }

    // ------------------------------------------------------------------ итог матча: только своя роль и надёжные значения

    private static MatchRecord Game(string id, Position? pos, int gpm, int deaths = 5, int lh10 = 40, int heroId = 14, bool ended = true)
    {
        var m = new MatchRecord
        {
            Id = id, MatchId = id, Source = "GSI", HeroId = heroId, Position = pos, Result = MatchResult.Loss,
            DurationSec = 35 * 60, Kills = 3, Deaths = deaths, Assists = 5, Gpm = gpm, Ended = ended
        };
        m.Samples.Add(new MatchSample { Clock = 600, LastHits = lh10, Gpm = gpm });
        m.Coverage.Add(new CoverageSpan { From = 0, To = m.DurationSec });
        return m;
    }

    [Fact]
    public void PostMatch_SameHeroOtherRole_IsNotCompared()
    {
        var now = Game("now", Position.Carry, gpm: 400);
        var history = Enumerable.Range(0, 3).Select(i => Game("s" + i, Position.HardSupport, gpm: 900)).ToList();
        var sum = PostMatch.Build(now, history);
        Assert.NotEqual("gpm", sum.MainMistake?.Kind);
        Assert.Equal(0, sum.Compared);
        Assert.Null(sum.Basis);
    }

    [Fact]
    public void PostMatch_MetricNeedsThreeValidValues()
    {
        var now = Game("now", Position.Carry, gpm: 400);
        var history = new List<MatchRecord> { Game("a", Position.Carry, 900), Game("b", Position.Carry, 0), Game("c", Position.Carry, 0) };
        var sum = PostMatch.Build(now, history);
        Assert.DoesNotContain(sum.Good, g => g.Kind == "gpm");
        Assert.NotEqual("gpm", sum.MainMistake?.Kind);                       // одно пригодное значение — не «обычное»
        Assert.Equal(3, sum.Compared);
        Assert.Equal("bd_basis_hero_role", sum.Basis);
    }

    [Fact]
    public void PostMatch_RoleFallback_AndPartialIsNotPraisedForFewDeaths()
    {
        var history = Enumerable.Range(0, 4).Select(i => Game("r" + i, Position.Carry, 500, deaths: 8, heroId: 50 + i)).ToList();
        var full = PostMatch.Build(Game("now", Position.Carry, 500, deaths: 1), history);
        Assert.Equal("bd_basis_role", full.Basis);
        Assert.Contains(full.Good, g => g.Kind == "deaths");

        var cut = Game("cut", Position.Carry, 500, deaths: 1, ended: false);   // запись оборвалась
        Assert.DoesNotContain(PostMatch.Build(cut, history).Good, g => g.Kind == "deaths");

        var withCut = history.Take(2).Append(Game("x", Position.Carry, 500, ended: false)).ToList();
        Assert.Equal(0, PostMatch.Build(Game("now2", Position.Carry, 500), withCut).Compared);   // оборванные — не в «обычное»
    }

    // ------------------------------------------------------------------ темп: роль уточнилась после старта

    [Fact]
    public void Pace_RoleRefinedAfterStart_UsesNewRole_AndDoesNotRepeat()
    {
        var history = Enumerable.Range(0, 3).Select(i => Game("c" + i, Position.Carry, 600, lh10: 60))
            .Concat(Enumerable.Range(0, 3).Select(i => Game("h" + i, Position.HardSupport, 250, lh10: 8))).ToList();
        var pace = new PaceAdvisor(history, 14, Position.Carry, "now");
        Assert.Equal(60, pace.UsualLastHits(600)!.Value.Avg);

        var support = pace.ForPosition(Position.HardSupport);
        Assert.Equal(Position.HardSupport, support.Position);
        var check = support.CheckpointDue(605, 10);
        Assert.Equal(8, check!.Usual);                                       // не «отстаёте на 50» от керри

        var announced = new PaceAdvisor(history, 14, Position.Carry, "now");
        Assert.NotNull(announced.CheckpointDue(602, 40));
        Assert.Null(announced.ForPosition(Position.HardSupport).CheckpointDue(606, 10));   // 10:00 уже объявлена
    }

    // ------------------------------------------------------------------ цели: отсутствие события в пропуске — не успех

    [Fact]
    public void Goal_AbsenceOnPartialRecord_IsUnknown_ButViolationCounts()
    {
        var goldGoal = new CoachGoal { Kind = GoalKind.GoldOnDeath, Target = 1000 };
        var partial = Game("p", Position.Carry, 500);
        partial.Coverage.Clear();
        partial.Coverage.Add(new CoverageSpan { From = 0, To = 900 });       // данные пропали с 15:00
        partial.UnlocatedDeaths = 4;
        Assert.Null(GoalEngine.Evaluate(goldGoal, partial));
        Assert.Null(GoalEngine.Evaluate(new CoachGoal { Kind = GoalKind.CarryTeleport }, partial));
        Assert.Null(GoalEngine.Evaluate(new CoachGoal { Kind = GoalKind.CourierStash }, partial));

        partial.DeathEvents.Add(new DeathEvent { Clock = 700, GoldAtRisk = 1400, HadTeleport = true });
        Assert.Equal((false, 1), GoalEngine.Evaluate(goldGoal, partial)!.Value);   // замеченная ошибка — настоящий провал

        var complete = Game("c", Position.Carry, 500);
        Assert.Equal((true, 0), GoalEngine.Evaluate(goldGoal, complete)!.Value);

        var cut = Game("cut", Position.Carry, 500, deaths: 2, ended: false);
        Assert.Null(GoalEngine.Evaluate(new CoachGoal { Kind = GoalKind.FewerDeaths, Target = 4 }, cut));
        Assert.Equal((false, 6), GoalEngine.Evaluate(new CoachGoal { Kind = GoalKind.FewerDeaths, Target = 4 },
            Game("cut6", Position.Carry, 500, deaths: 6, ended: false))!.Value);
    }

    // ------------------------------------------------------------------ фазы составов и предметы: без уверенных выводов

    [Fact]
    public void Insights_ClockReminderIsConditional_AndHasDeadline()
    {
        var early = new[] { "axe", "bane", "bloodseeker" }.Select(TestData.Hero).ToList();
        var late = new[] { "antimage", "spectre", "medusa" }.Select(TestData.Hero).ToList();
        var t = new TeamTiming(TeamPhase.LateStronger, Array.Empty<Hero>(), late, early, Array.Empty<Hero>());
        var r = PowerSpikes.ClockReminder(t, 2110)!.Value;
        Assert.Equal("spike_now", r.Key);
        Assert.True(r.Until > 2160);
        Assert.DoesNotContain("уже сильнее", r.Ru);
        Assert.Contains("если", r.Ru);
        Assert.DoesNotContain("already stronger", r.En);
        foreach (var clock in new[] { 610, 730, 1690, 2110 })
            foreach (var phase in new[] { TeamPhase.EarlyStronger, TeamPhase.LateStronger })
                if (PowerSpikes.ClockReminder(t with { Ours = phase }, clock) is { } x)
                    Assert.Contains("если", x.Ru);
    }

    [Fact]
    public void Insights_FarmItemsAreAnnouncedNeutrally()
    {
        Assert.Equal("spike_item_farm", PowerSpikes.ItemMessageKey("item_hand_of_midas"));
        Assert.Equal("spike_item_farm", PowerSpikes.ItemMessageKey("item_bfury"));
        Assert.Equal("spike_item", PowerSpikes.ItemMessageKey("item_black_king_bar"));
        var fight = Roshiq.Core.Localization.Loc.T("spike_item");
        Assert.DoesNotContain("Ищите драку", fight);
        Assert.DoesNotContain("Look for a fight", fight);
    }
}
