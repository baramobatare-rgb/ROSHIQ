using System.Text.Json;
using Roshiq.Core.Models;
using Roshiq.Data;
using Roshiq.Draft;
using Roshiq.Recommendations;
using Xunit;

namespace Roshiq.Tests;

public class ReviewTests
{
    private const string MatchJson = """
    {
      "match_id": 7000000001, "duration": 2400, "radiant_win": false, "version": 21,
      "players": [
        {
          "account_id": 42, "player_slot": 1, "hero_id": 1, "kills": 5, "deaths": 4, "assists": 6,
          "lh_t": [0,3,7,11,15,19,23,26,29,32,35,40],
          "dn_t": [0,1,1,2,2,3,3,3,4,4,4,5],
          "benchmarks": {
            "gold_per_min": { "raw": 410, "pct": 0.12 },
            "last_hits_per_min": { "raw": 4.1, "pct": 0.2 },
            "hero_damage_per_min": { "raw": 620, "pct": 0.82 },
            "hero_healing_per_min": { "raw": 0, "pct": 0.5 }
          },
          "purchase_log": [
            { "time": -80, "key": "tango" },
            { "time": 1150, "key": "bfury" },
            { "time": 1700, "key": "manta" },
            { "time": 1800, "key": "bfury" }
          ],
          "obs_log": [ { "time": 300, "x": 128, "y": 128, "ehandle": 11 } ],
          "obs_left_log": [ { "time": 400, "x": 128, "y": 128, "ehandle": 11, "attackername": "npc_dota_hero_axe" } ],
          "lane_efficiency_pct": 41
        },
        {
          "account_id": 7, "player_slot": 128, "hero_id": 2,
          "kills_log": [
            { "time": 500, "key": "npc_dota_hero_antimage" },
            { "time": 900, "key": "npc_dota_hero_antimage" },
            { "time": 1300, "key": "npc_dota_hero_antimage" },
            { "time": 1500, "key": "npc_dota_hero_lion" }
          ]
        }
      ]
    }
    """;

    private static MatchDetails Details()
    {
        using var doc = JsonDocument.Parse(MatchJson);
        return MatchDetails.Parse(doc.RootElement, 42, id => id == 1 ? "npc_dota_hero_antimage" : null)!;
    }

    [Fact]
    public void Parse_ExtractsPlayerData()
    {
        var d = Details();
        Assert.True(d.Parsed);
        Assert.True(d.IsRadiant);
        Assert.False(d.Won);
        Assert.Equal(35, d.LastHitsAt(10));
        Assert.Equal(4, d.DeniesAt(10));
        Assert.Equal("item_bfury", d.Purchases[1].ItemKey);
        Assert.Equal(3, d.DeathsBy.Count);
        Assert.All(d.DeathsBy, x => Assert.Equal(2, x.KillerHeroId));
        var ward = Assert.Single(d.Observers);
        Assert.Equal(0.5, ward.X, 3);
        Assert.Equal(100, ward.Lifetime);
        Assert.True(ward.Dewarded);
        Assert.Equal(41, d.LaneEfficiency);
        using var doc = JsonDocument.Parse(MatchJson);
        Assert.Null(MatchDetails.Parse(doc.RootElement, 999, _ => null)); // вас нет в матче
    }

    [Fact]
    public void Review_FindsProblemsAndStrengths()
    {
        var d = Details();
        int? Cost(string k) => k switch { "item_bfury" => 4100, "item_manta" => 4650, "item_tango" => 90, _ => null };
        var norms = new Dictionary<string, int?> { ["item_bfury"] = 840, ["item_manta"] = 1720 };
        var r = MatchReviewBuilder.Build(d, Position.Carry, norms, k => (k, k), h => (h == 2 ? "Акс" : "?", h == 2 ? "Axe" : "?"), Cost);

        Assert.Equal(new[] { "item_bfury", "item_manta" }, r.Items.Select(i => i.ItemKey)); // повторная покупка и расходники не считаются
        Assert.Equal(310, r.Items[0].Delta);
        Assert.Equal(35, r.LastHits10);
        Assert.Equal(50, r.LastHits10Target);
        Assert.DoesNotContain(r.Benchmarks, b => b.Key == "hero_healing_per_min"); // нулевое лечение не показываем

        Assert.Contains(r.Insights, i => i.Kind == "benchmark" && i.Severity == -3 && i.En.Contains("Gold per minute"));
        Assert.Contains(r.Insights, i => i.Kind == "benchmark" && i.Severity > 0 && i.En.Contains("Hero damage"));
        Assert.Contains(r.Insights, i => i.Kind == "lane" && i.Severity == -1 && i.En.Contains("efficiency"));
        Assert.Contains(r.Insights, i => i.Kind == "item" && i.Severity == -2 && i.En.Contains("item_bfury"));
        Assert.Contains(r.Insights, i => i.Kind == "deaths" && i.Ru.Contains("Акс") && i.Ru.Contains("3"));
        Assert.Equal(3, r.Top.Count);
        Assert.True(r.Top[0].Severity <= r.Top[1].Severity);
        Assert.True(r.Top[2].Severity > 0);
    }

    [Fact]
    public void Review_Store_RoundTripsAndFindsWeakest()
    {
        var dir = Path.Combine(Path.GetTempPath(), "roshiq-reviews-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new MatchReviewStore(dir);
            for (int i = 0; i < 3; i++)
                store.Save(new MatchReview
                {
                    MatchId = (100 + i).ToString(), CreatedUtc = DateTime.UtcNow,
                    Benchmarks = { new Benchmark("last_hits_per_min", 4, 0.2), new Benchmark("gold_per_min", 500, 0.6) }
                });
            Assert.Equal(3, new MatchReviewStore(dir).Recent(10).Count);
            var weak = MatchReviewStore.Weakest(store.Recent(10));
            Assert.Equal("last_hits_per_min", weak!.Value.Key);
            Assert.Null(new MatchReviewStore(dir).Get("../evil"));
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public void ItemNorm_WeightedMedian()
    {
        Assert.Equal(900, ItemTimingNormCache.Median(new[] { (600, 10), (900, 40), (1200, 30), (1500, 20) }));
        Assert.Null(ItemTimingNormCache.Median(new[] { (600, 5), (900, 5) })); // мало данных
    }

    [Fact]
    public void Patterns_LateGameAndTilt()
    {
        var start = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var list = new List<MatchRecord>();
        for (int i = 0; i < 24; i++)
        {
            bool longGame = i % 2 == 0;
            list.Add(new MatchRecord
            {
                Id = "p" + i, Source = "GSI", StartedUtc = start.AddHours(i * 5),
                DurationSec = longGame ? 3000 : 1500,
                Result = longGame ? (i % 6 == 0 ? MatchResult.Win : MatchResult.Loss) : MatchResult.Win
            });
        }
        var insights = Patterns.Insights(list);
        Assert.Contains(insights, x => x.Severity < 0 && x.En.StartsWith("Over 45"));
        Assert.Contains(insights, x => x.Severity > 0 && x.En.StartsWith("Under 30"));
        Assert.Empty(Patterns.Insights(list.Take(5)));
    }

    [Fact]
    public void Draft_WarnsAboutBadMatchups()
    {
        var axe = TestData.Hero("axe");
        var analyzer = new DraftAnalyzer(TestData.Db) { PersonalAgainst = new Dictionary<int, (int, int)> { [axe.Id] = (26, 8) } };
        var warn = Assert.Single(analyzer.AgainstWarnings(new[] { axe }));
        Assert.Equal(ReasonKind.Risk, warn.Kind);
        Assert.Contains("26", warn.En);
        var analysis = analyzer.Analyze(new DraftState(Array.Empty<Hero>(), new[] { axe }, Position.Carry));
        Assert.Contains(analysis.Notes, n => n.Kind == ReasonKind.Risk);
        analyzer.PersonalAgainst = new Dictionary<int, (int, int)> { [axe.Id] = (5, 0) }; // мало игр
        Assert.Empty(analyzer.AgainstWarnings(new[] { axe }));
    }

    [Fact]
    public void Recorder_StoresDeathPosition()
    {
        var r = new MatchRecorder();
        MatchTick T(int clock, int deaths, int x, int y) => new("900", "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS", clock, "radiant", null, 2, "axe", 6, 0, deaths, 0, 20, 1,
            400, 400, 300, new Dictionary<string, string>(), Position.Offlane, DateTime.UtcNow, 300, 500, 0, x, y);
        r.Observe(T(400, 0, -5000, 2000));
        r.Observe(T(410, 1, -6000, -6000));
        var d = Assert.Single(r.Current!.DeathEvents);
        Assert.Equal(-5000, d.X);
        Assert.Equal(2000, d.Y);
    }
}
