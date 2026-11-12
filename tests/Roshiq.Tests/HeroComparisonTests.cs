using System.Text.Json;
using Roshiq.Core.Models;
using Roshiq.Data;
using Roshiq.Recommendations;
using Xunit;

namespace Roshiq.Tests;

/// <summary>0.21.1: сравнение с другими игроками на герое по перцентилям OpenDota (ответ для Anti-Mage, 12.11.2026).</summary>
public class HeroComparisonTests
{
    private const string Json = """
    {"hero_id":1,"result":{
     "gold_per_min":[{"percentile":0.1,"value":418},{"percentile":0.5,"value":663},{"percentile":0.9,"value":845},{"percentile":0.99,"value":972}],
     "xp_per_min":[{"percentile":0.1,"value":539},{"percentile":0.5,"value":927},{"percentile":0.9,"value":1274}],
     "last_hits_per_min":[{"percentile":0.1,"value":5.35},{"percentile":0.5,"value":9.43},{"percentile":0.9,"value":12.4}],
     "deaths_per_min":[{"percentile":0.1,"value":0.04},{"percentile":0.5,"value":0.13},{"percentile":0.9,"value":0.26}]}}
    """;

    private static HeroBenchmarks B() => HeroBenchmarks.Parse(JsonDocument.Parse(Json).RootElement, DateTime.UtcNow)!;

    [Fact]
    public void Percentile_InterpolatesBetweenPoints()
    {
        var b = B();
        Assert.Equal(1, b.HeroId);
        Assert.Equal(50, b.Percentile("gold_per_min", 663)!.Value, 1);
        Assert.Equal(70, b.Percentile("gold_per_min", 754)!.Value, 0);   // посередине между 663 и 845
        Assert.Equal(99, b.Percentile("gold_per_min", 2000)!.Value, 1);  // выше всех точек — последняя
        Assert.Null(b.Percentile("hero_damage_per_min", 500));
    }

    [Fact]
    public void Rows_DeathsAreBetterWhenLower()
    {
        var m = new MatchRecord { HeroId = 1, DurationSec = 2400, Gpm = 663, Xpm = 927, LastHits = 377, Deaths = 13, Ended = true };
        var rows = HeroComparison.Build(m, B());
        Assert.Equal(new[] { "gold_per_min", "xp_per_min", "last_hits_per_min", "deaths_per_min" }, rows.Select(r => r.Metric));
        Assert.Equal(50, rows[0].BetterThan);
        // 13 смертей за 40 минут = 0,325 в минуту — больше, чем у 90% игроков → лучше, чем у 10% и меньше
        Assert.True(rows[3].BetterThan <= 10);
        Assert.Equal("3.3", rows[3].Value);
        Assert.Equal("deaths_per_min", HeroComparison.Weakest(rows)?.Metric);
        Assert.Contains("лучше, чем у 50% игроков на этом герое", rows[0].Text(Language.Russian));
    }

    [Fact]
    public void ShortOrUnknownMatch_GivesNothing()
    {
        Assert.Empty(HeroComparison.Build(new MatchRecord { HeroId = 1, DurationSec = 300, Gpm = 500 }, B()));
        Assert.Empty(HeroComparison.Build(new MatchRecord { HeroId = 1, DurationSec = 2400, Gpm = 500 }, null));
    }

    [Fact]
    public void Cache_RoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), "roshiq-bench-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            new HeroBenchmarksCache(path).Put(B());
            var again = new HeroBenchmarksCache(path);
            Assert.True(again.IsFresh(1, DateTime.UtcNow));
            Assert.False(again.IsFresh(1, DateTime.UtcNow.AddDays(8)));
            Assert.Equal(50, again.Get(1)!.Percentile("gold_per_min", 663)!.Value, 1);
        }
        finally { File.Delete(path); }
    }
    [Fact]
    public void Score_IsWeightedShareOfPlayersBeaten()
    {
        var rows = new[]
        {
            new ComparisonRow("gold_per_min", "", "", "", 80),
            new ComparisonRow("xp_per_min", "", "", "", 60),
            new ComparisonRow("last_hits_per_min", "", "", "", 40),
            new ComparisonRow("deaths_per_min", "", "", "", 20),
        };
        // 0,3·80 + 0,2·60 + 0,2·40 + 0,3·20 = 50
        Assert.Equal(50, HeroComparison.Score(rows));
        Assert.Equal("score_mid", HeroComparison.ScoreVerdictKey(50));
        Assert.Equal("score_strong", HeroComparison.ScoreVerdictKey(70));
        Assert.Equal("score_weak", HeroComparison.ScoreVerdictKey(39));
    }

    [Fact]
    public void Score_NeedsAtLeastThreeMetrics()
    {
        Assert.Null(HeroComparison.Score(new[] { new ComparisonRow("gold_per_min", "", "", "", 80), new ComparisonRow("xp_per_min", "", "", "", 60) }));
        var m = new MatchRecord { HeroId = 1, DurationSec = 2400, Gpm = 663, Xpm = 927, LastHits = 377, Deaths = 5, Ended = true };
        Assert.InRange(HeroComparison.Score(HeroComparison.Build(m, B()))!.Value, 30, 70);
    }

    [Fact]
    public void Score_SurvivesSaveAndLoad()
    {
        var dir = Path.Combine(Path.GetTempPath(), "roshiq-score-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new MatchHistoryStore(dir);
            store.Save(new MatchRecord { Id = "s1", HeroId = 1, DurationSec = 2400, Score = 64 });
            Assert.Equal(64, new MatchHistoryStore(dir).Find("s1")!.Score);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
