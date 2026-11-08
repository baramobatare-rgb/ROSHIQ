using Roshiq.Core.Models;
using Roshiq.Recommendations;
using Xunit;
namespace Roshiq.Tests;
public class ReportComparisonTests
{
    private static MatchRecord Game(string id, Position? role = Position.Mid, int hero = 14, int lh = 60)
    {
        var m = new MatchRecord { Id = id, Position = role, HeroId = hero, DurationSec = 1800, Ended = true };
        m.Coverage.Add(new CoverageSpan { From = 0, To = 1800 });
        for (int c = 0; c <= 1800; c += 30) m.Samples.Add(new MatchSample { Clock = c, LastHits = lh * c / 600, Gpm = 450 });
        return m;
    }
    [Fact] public void SameHeroOnDifferentRoleCannotSetTheBaseline()
    {
        var history = Enumerable.Range(0, 4).Select(i => Game("support"+i, Position.HardSupport, lh: 20));
        var result = PlayerReport.Breakdown(Game("now"), history)!;
        Assert.Equal(0, result.Compared); Assert.Null(result.ComparisonBasis);
        Assert.All(result.Phases, p => Assert.Null(p.LastHitsUsual));
    }
    [Fact] public void SameHeroAndRolePreferredOverOtherHeroes()
    {
        var history = Enumerable.Range(0, 3).Select(i => Game("same"+i))
            .Concat(Enumerable.Range(0, 4).Select(i => Game("other"+i, hero: 2, lh: 90)));
        var result = PlayerReport.Breakdown(Game("now"), history)!;
        Assert.Equal("bd_basis_hero_role", result.ComparisonBasis);
        Assert.Equal(3, result.Compared); Assert.Equal(60, result.Phases[0].LastHitsUsual);
    }
    [Fact] public void RoleFallbackExcludesUnknownRoles()
    {
        var history = Enumerable.Range(0, 3).Select(i => Game("role"+i, hero: 2))
            .Concat(Enumerable.Range(0, 4).Select(i => Game("unknown"+i, role: null, lh: 10)));
        var result = PlayerReport.Breakdown(Game("now"), history)!;
        Assert.Equal("bd_basis_role", result.ComparisonBasis); Assert.Equal(3, result.Compared);
        Assert.Equal(60, result.Phases[0].LastHitsUsual);
    }
    [Fact] public void UnknownRoleUsesOnlySameHeroWithUnknownRole()
    {
        var history = Enumerable.Range(0, 3).Select(i => Game("known"+i));
        Assert.Equal(0, PlayerReport.Breakdown(Game("now", role:null), history)!.Compared);
        var unknown = Enumerable.Range(0, 3).Select(i => Game("unknown"+i, role:null));
        Assert.Equal("bd_basis_hero", PlayerReport.Breakdown(Game("now", role:null), unknown)!.ComparisonBasis);
    }
    [Fact] public void IncompleteReferenceGamesDoNotCreateAnUsualValue()
    {
        var history = Enumerable.Range(0, 3).Select(i => Game("gap"+i)).ToArray();
        history[0].Coverage.Clear(); history[0].Coverage.Add(new CoverageSpan { From=500, To=700 });
        var result = PlayerReport.Breakdown(Game("now"), history)!;
        Assert.Equal(0, result.Compared); Assert.All(result.Phases, p => Assert.Null(p.GpmUsual));
    }
    [Fact] public void ApproximateCurrentItemCannotBeCalledLate()
    {
        var history = Enumerable.Range(0, 3).Select(i => Game("base"+i)).ToArray();
        foreach (var m in history) m.ItemTimings["item_black_king_bar"] = 900;
        var now = Game("now"); now.ItemTimings["item_black_king_bar"] = 1500;
        Assert.Single(PlayerReport.Breakdown(now, history)!.LateItems);
        now.ApproxItems.Add("item_black_king_bar");
        Assert.Empty(PlayerReport.Breakdown(now, history)!.LateItems);
    }

}
