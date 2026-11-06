using Roshiq.Core.Models;
using Roshiq.Data;
using Roshiq.Draft;
using Xunit;

namespace Roshiq.Tests;

public class DraftTests
{
    private static DraftAnalyzer Analyzer(IMatchupStatsProvider? stats = null) => new(TestData.Db, stats, 200);

    private static DraftState State(Position p, string[] allies, string[] enemies) =>
        new(allies.Select(TestData.Hero).ToList(), enemies.Select(TestData.Hero).ToList(), p);

    [Fact]
    public void ReturnsThreeTop_ForPosition_ExcludingPickedHeroes()
    {
        var a = Analyzer().Analyze(State(Position.Offlane, new[] { "crystal_maiden" }, new[] { "phantom_lancer", "naga_siren" }));
        Assert.Equal(3, a.Top.Count);
        Assert.All(a.Top.Concat(a.Alternatives), r => Assert.True(r.Hero.PlaysPosition(Position.Offlane)));
        Assert.DoesNotContain(a.Top, r => r.Hero.Key is "phantom_lancer" or "naga_siren" or "crystal_maiden");
        Assert.True(a.Alternatives.Count > 0);
    }

    [Fact]
    public void IllusionHeavyEnemies_FavorAreaDamage()
    {
        var a = Analyzer().Analyze(State(Position.Offlane, Array.Empty<string>(), new[] { "phantom_lancer", "naga_siren", "chaos_knight" }));
        Assert.All(a.Top, r => Assert.True(r.Hero.Has("aoe") || r.Hero.Has("cleave"), r.Hero.Key));
        Assert.Contains(a.Top[0].Reasons, x => x.Kind == ReasonKind.Counter && x.Ru.Contains("Phantom Lancer"));
    }

    [Fact]
    public void AttackCarries_PromoteAttackPunishers_LikeAxe()
    {
        var st = State(Position.Offlane, Array.Empty<string>(), new[] { "phantom_assassin", "juggernaut", "troll_warlord" });
        var axe = Analyzer().Evaluate(TestData.Hero("axe"), st);
        Assert.Contains(axe.Reasons, r => r.Kind == ReasonKind.Counter && r.En.Contains("right-clicks"));
        var rank = Analyzer().Analyze(st, 3, 20);
        Assert.Contains(rank.Top.Concat(rank.Alternatives), r => r.Hero.Key == "axe");
    }

    [Fact]
    public void Risks_AreReported_WhenEnemyCountersCandidate()
    {
        var st = State(Position.Carry, Array.Empty<string>(), new[] { "silencer", "doom_bringer" });
        var pa = Analyzer().Evaluate(TestData.Hero("phantom_assassin"), st);
        Assert.Contains(pa.Reasons, r => r.Kind == ReasonKind.Risk); // break против пассивок
    }

    [Fact]
    public void WithoutStats_NoPercentagesAreInvented()
    {
        var a = Analyzer().Analyze(State(Position.Mid, new[] { "axe" }, new[] { "storm_spirit", "tinker" }), 3, 10);
        foreach (var r in a.Top.Concat(a.Alternatives).SelectMany(x => x.Reasons))
            Assert.DoesNotContain("%", r.Ru + r.En);
        Assert.Contains(a.Notes, n => n.En.Contains("statistics not loaded"));
        Assert.All(a.Top, r => Assert.Empty(r.Stats));
    }

    [Fact]
    public void EmptyDraft_GivesLowConfidenceAndNote()
    {
        var a = Analyzer().Analyze(DraftState.Empty(Position.HardSupport));
        Assert.False(a.HasEnoughData);
        Assert.All(a.Top, r => Assert.Equal(Confidence.Low, r.Confidence));
    }

    private sealed class FakeStats : IMatchupStatsProvider
    {
        public readonly Dictionary<(int, int), MatchupStat> Data = new();
        public MatchupStat? Get(int heroId, int opponentId) => Data.TryGetValue((heroId, opponentId), out var s) ? s : null;
    }

    [Fact]
    public void Stats_UsedOnlyWithEnoughGames_AndShownWithSource()
    {
        var stats = new FakeStats();
        var axe = TestData.Hero("axe");
        var pa = TestData.Hero("phantom_assassin");
        var jugg = TestData.Hero("juggernaut");
        stats.Data[(axe.Id, pa.Id)] = new MatchupStat(axe.Id, pa.Id, 1000, 560, "OpenDota", DateTime.UtcNow);
        stats.Data[(axe.Id, jugg.Id)] = new MatchupStat(axe.Id, jugg.Id, 50, 40, "OpenDota", DateTime.UtcNow); // мало игр — игнор
        var st = new DraftState(Array.Empty<Hero>(), new[] { pa, jugg }, Position.Offlane);
        var rec = Analyzer(stats).Evaluate(axe, st);
        Assert.Single(rec.Stats);
        Assert.Contains(rec.Reasons, r => r.Kind == ReasonKind.Stats && r.En.Contains("1000 games") && r.En.Contains("OpenDota"));
    }

    [Fact]
    public void CountersOf_ReturnsPositiveCounters()
    {
        var list = Analyzer().CountersOf(TestData.Hero("riki"), 10);
        Assert.NotEmpty(list);
        Assert.Contains(list, x => x.Hero.Has("reveal"));
        Assert.All(list, x => Assert.True(x.Score > 0));
    }
}
