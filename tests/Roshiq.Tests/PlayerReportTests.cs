using Roshiq.Core.Models;
using Roshiq.Data;
using Roshiq.Recommendations;
using Xunit;

namespace Roshiq.Tests;

/// <summary>0.17: статистика за неделю, повторяющиеся ошибки, разбор по фазам, сравнение с другими.</summary>
public class PlayerReportTests
{
    private static readonly DateTime Now = new(2026, 11, 7, 20, 0, 0, DateTimeKind.Utc);

    private static MatchRecord M(string id, double daysAgo, MatchResult r, int heroId = 14, int deaths = 4, int k = 6, int a = 8,
        Position pos = Position.Mid, int gpm = 500, string source = "GSI")
    {
        return new MatchRecord
        {
            Id = id, MatchId = id, Source = source, StartedUtc = Now.AddDays(-daysAgo), HeroId = heroId, Position = pos, Result = r,
            DurationSec = 40 * 60, Kills = k, Deaths = deaths, Assists = a, Gpm = gpm
        };
    }

    [Fact]
    public void Week_CountsOnlyLastSevenDaysAndStreak()
    {
        var all = new List<MatchRecord>
        {
            M("a", 0.1, MatchResult.Win), M("b", 0.2, MatchResult.Win), M("c", 1, MatchResult.Loss, heroId: 2),
            M("d", 2, MatchResult.Loss, heroId: 2), M("e", 3, MatchResult.Win),
            M("old1", 9, MatchResult.Loss), M("old2", 10, MatchResult.Loss), M("old3", 11, MatchResult.Loss),
        };
        var w = PlayerReport.Week(all, Now);
        Assert.Equal(5, w.Games);
        Assert.Equal(3, w.Wins);
        Assert.Equal(0.6, w.WinRate!.Value, 3);
        Assert.Equal(0.0, w.PrevWinRate!.Value, 3);
        Assert.Equal(2, w.Streak);                                   // две победы подряд с конца
        Assert.Equal(14, w.Best!.HeroId);                            // 3 из 3 на герое 14
        Assert.Equal(2, w.Worst!.HeroId);                            // 0 из 2 на герое 2
        Assert.True(w.Kda > 3);
    }

    [Fact]
    public void Week_EmptyAndLossStreak()
    {
        var w = PlayerReport.Week(new[] { M("x", 20, MatchResult.Loss), M("y", 21, MatchResult.Loss) }, Now);
        Assert.Equal(0, w.Games);
        Assert.Null(w.WinRate);
        Assert.Equal(-2, w.Streak);
        Assert.Null(w.Best);
    }

    [Fact]
    public void Recurring_FindsDeathsAndTrend()
    {
        var all = new List<MatchRecord>();
        // свежие 5 игр — много смертей, старые 5 — нормально: стало хуже
        for (int i = 0; i < 5; i++) all.Add(M("n" + i, i * 0.1, MatchResult.Loss, deaths: 10));
        for (int i = 0; i < 5; i++) all.Add(M("o" + i, 2 + i, MatchResult.Win, deaths: 3));
        var list = PlayerReport.Recurring(all);
        var deaths = Assert.Single(list, x => x.Kind == "deaths");
        Assert.Equal(5, deaths.Count);
        Assert.Equal(10, deaths.Of);
        Assert.Equal(1, deaths.Trend);
        Assert.Equal(3, deaths.Examples.Count);
    }

    [Fact]
    public void Recurring_SupportsHaveHigherDeathLimitAndRareMistakesHidden()
    {
        var all = Enumerable.Range(0, 8).Select(i => M("s" + i, i, MatchResult.Win, deaths: 8, pos: Position.HardSupport)).ToList();
        Assert.DoesNotContain(PlayerReport.Recurring(all), x => x.Kind == "deaths");      // 8 смертей у саппорта — не ошибка
        var one = Enumerable.Range(0, 8).Select(i => M("c" + i, i, MatchResult.Win, deaths: i == 0 ? 12 : 2)).ToList();
        Assert.DoesNotContain(PlayerReport.Recurring(one), x => x.Kind == "deaths");      // один раз — не закономерность
    }

    [Fact]
    public void Recurring_NoTeleportOnlyForGsi()
    {
        var all = new List<MatchRecord>();
        for (int i = 0; i < 4; i++)
        {
            var m = M("g" + i, i, MatchResult.Loss);
            m.DeathEvents.Add(new DeathEvent { Clock = 700, HadTeleport = false });
            m.DeathEvents.Add(new DeathEvent { Clock = 900, HadTeleport = false });
            all.Add(m);
        }
        for (int i = 0; i < 6; i++) all.Add(M("od" + i, 5 + i, MatchResult.Win, source: "OpenDota"));
        var tp = Assert.Single(PlayerReport.Recurring(all), x => x.Kind == "no_tp");
        Assert.Equal(4, tp.Of);                                      // импортированные матчи не считаются
    }

    [Fact]
    public void Breakdown_PhasesAndLateItems()
    {
        MatchRecord G(string id, int lhPerMin, int bkb)
        {
            var m = M(id, 1, MatchResult.Win);
            for (int c = 0; c <= 2400; c += 300) m.Samples.Add(new MatchSample { Clock = c, LastHits = lhPerMin * c / 60, Gpm = 400 + c / 30 });
            m.ItemTimings["item_black_king_bar"] = bkb;
            return m;
        }
        var history = new[] { G("h1", 6, 1200), G("h2", 6, 1260), G("h3", 6, 1230) };
        var now = G("now", 4, 1700);
        now.DeathEvents.Add(new DeathEvent { Clock = 300, GoldAtRisk = 250 });
        now.DeathEvents.Add(new DeathEvent { Clock = 1000, GoldAtRisk = 600 });
        var b = PlayerReport.Breakdown(now, history.Append(now))!;
        Assert.Equal(3, b.Phases.Count);
        var lane = b.Phases[0];
        Assert.Equal(1, lane.Deaths);
        Assert.Equal(250, lane.GoldLost);
        Assert.Equal(40, lane.LastHits);
        Assert.Equal(60, lane.LastHitsUsual);
        var item = Assert.Single(b.LateItems);
        Assert.Equal("item_black_king_bar", item.ItemKey);
        Assert.Equal(470, item.Delta);
        Assert.Equal(3, b.Compared);
    }

    [Fact]
    public void Breakdown_NoUsualWithFewGames()
    {
        MatchRecord G(string id)
        {
            var m = M(id, 1, MatchResult.Win);
            for (int c = 0; c <= 2400; c += 300) m.Samples.Add(new MatchSample { Clock = c, LastHits = c / 10, Gpm = 450 });
            return m;
        }
        var now = G("now");
        var b = PlayerReport.Breakdown(now, new[] { G("only"), now })!;
        Assert.Null(b.Phases[0].LastHitsUsual);                      // одна прошлая игра — «обычно» не показываем
        Assert.Equal(0, b.Compared);
    }

    [Fact]
    public void Median_AndNoise()
    {
        Assert.Equal(900, MatchStats.Median(new[] { 800, 1000 }));
        Assert.Equal(60, MatchStats.Median(new[] { 70, 50, 60 }));
        Assert.False(MatchStats.IsNoisy(new[] { 50, 60, 70 }));
        Assert.True(MatchStats.IsNoisy(new[] { 5, 60, 140 }));
        Assert.True(MatchStats.IsNoisy(new[] { 50, 60 }));               // меньше трёх — сравнивать не с чем
    }

    [Fact]
    public void Breakdown_NullForImported()
    {
        Assert.Null(PlayerReport.Breakdown(M("x", 1, MatchResult.Win, source: "OpenDota"), Array.Empty<MatchRecord>()));
    }

    [Fact]
    public void Percentiles_AveragePerKeyInFixedOrder()
    {
        var reviews = new[]
        {
            new MatchReview { HeroId = 14, Benchmarks = { new Benchmark("xp_per_min", 600, 0.8), new Benchmark("gold_per_min", 500, 0.4) } },
            new MatchReview { HeroId = 14, Benchmarks = { new Benchmark("gold_per_min", 520, 0.6) } },
            new MatchReview { HeroId = 2, Benchmarks = { new Benchmark("gold_per_min", 300, 0.1) } },
        };
        var all = PlayerReport.Percentiles(reviews);
        Assert.Equal("gold_per_min", all[0].Key);
        Assert.Equal(3, all[0].Matches);
        var hero = PlayerReport.Percentiles(reviews, 14);
        Assert.Equal(0.5, hero.Single(r => r.Key == "gold_per_min").AvgPct, 3);
        Assert.Equal(0.8, hero.Single(r => r.Key == "xp_per_min").AvgPct, 3);
    }
}
