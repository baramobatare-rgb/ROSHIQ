using Roshiq.Core.Models;
using Roshiq.Recommendations;
using Xunit;

namespace Roshiq.Tests;

public class AdaptationTests
{
    private readonly ItemBuildEngine _engine = new(TestData.Db);
    [Fact]
    public void NoEnemies_NoInventedAdvice()
    {
        var h = TestData.Hero("sniper");
        Assert.Empty(_engine.Adaptations(h, Position.Carry, Array.Empty<Hero>(), _engine.Build(h, Position.Carry)));
    }
    [Fact]
    public void Evasion_GetsSuitableCounterWithNamedReason_WithoutMutatingBaseline()
    {
        var h = TestData.Hero("sniper");
        var enemy = TestData.Hero("phantom_assassin");
        var baseline = _engine.Build(h, Position.Carry);
        var before = baseline.Phases.Values.SelectMany(p => p).Select(p => p.Item.Key).ToArray();
        var options = _engine.Adaptations(h, Position.Carry, new[] { enemy }, baseline);
        Assert.Contains(options, o => o.Pick.Item.Key == "item_monkey_king_bar");
        Assert.All(options, o => {
            Assert.True(o.Pick.Item.SuitsHero(h) && o.Pick.Item.SuitsPosition(Position.Carry));
            Assert.Contains(o.Pick.Reasons, reason => reason.En.Contains(enemy.Name));
            if (o.Replace != null) Assert.Equal(o.Pick.Item.Group, o.Replace.Group);
        });
        Assert.Equal(before, baseline.Phases.Values.SelectMany(p => p).Select(p => p.Item.Key));
    }
    [Fact]
    public void DuplicateEnemies_DoNotInflateScores()
    {
        var h = TestData.Hero("sniper"); var enemy = TestData.Hero("phantom_assassin");
        var b = _engine.Build(h, Position.Carry);
        Assert.Equal(_engine.Adaptations(h, Position.Carry, new[] { enemy }, b).Select(o => o.Pick.Score),
            _engine.Adaptations(h, Position.Carry, new[] { enemy, enemy }, b).Select(o => o.Pick.Score));
    }
    [Fact]
    public void ExistingCounter_IsMarkedAlreadyIncluded()
    {
        var h = TestData.Hero("sniper"); var enemy = TestData.Hero("phantom_assassin");
        var item = TestData.Db.FindItem("item_monkey_king_bar")!;
        var b = new BuildRecommendation { Hero = h, Position = Position.Carry, EnemyAware = false, HasCuratedBase = false,
            Phases = new Dictionary<BuildPhase, IReadOnlyList<ItemPick>> { [BuildPhase.Late] = new[] { new ItemPick(item, 1, Array.Empty<Reason>(), false) } } };
        Assert.True(_engine.Adaptations(h, Position.Carry, new[] { enemy }, b).Single(o => o.Pick.Item.Key == item.Key).AlreadyIncluded);
    }
    [Theory]
    [InlineData("deaths", "мини-карту")]
    [InlineData("lh10", "10-й")]
    [InlineData("no_tp", "телепорта")]
    [InlineData("gold_risk", "золота")]
    public void NextGoal_AddressesObservedIssue(string kind, string expected)
    {
        var s = new PostMatchSummary(new MatchRecord(), Array.Empty<Insight>(), new Insight(kind, -2, "", ""), 3);
        Assert.Contains(expected, PostMatch.NextGoal(s).Text(Language.Russian));
    }
    [Fact]
    public void NoMistake_GetsNeutralGoalWithoutInventedDiagnosis()
    {
        var s = new PostMatchSummary(new MatchRecord(), Array.Empty<Insight>(), null, 0);
        Assert.Contains("одну цель", PostMatch.NextGoal(s).Text(Language.Russian));
    }
}
