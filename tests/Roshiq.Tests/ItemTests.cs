using Roshiq.Core.Models;
using Roshiq.Recommendations;
using Xunit;

namespace Roshiq.Tests;

public class ItemTests
{
    private static readonly ItemBuildEngine Engine = new(TestData.Db);

    [Fact]
    public void EveryHeroAndPosition_GetsNonEmptyHeroSpecificBuild()
    {
        foreach (var hero in TestData.Db.Heroes)
            foreach (var pos in hero.Positions)
            {
                var b = Engine.Build(hero, pos);
                Assert.NotEmpty(b[BuildPhase.Start]);
                Assert.NotEmpty(b[BuildPhase.Early]);
                Assert.NotEmpty(b[BuildPhase.Mid]);
                foreach (var phase in new[] { BuildPhase.Start, BuildPhase.Early, BuildPhase.Mid, BuildPhase.Late, BuildPhase.Situational, BuildPhase.Alternative })
                    foreach (var pick in b[phase])
                        Assert.True(pick.Item.SuitsHero(hero) || pick.Curated, $"{hero.Key}/{pos}: {pick.Item.Key} не подходит герою");
            }
    }

    [Fact]
    public void RangedHero_NeverGetsBattleFury_MeleeNeverGetsDragonLance()
    {
        foreach (var hero in TestData.Db.Heroes)
        {
            var b = Engine.Build(hero, hero.Positions[0]);
            var all = b.Phases.Values.SelectMany(x => x).Select(p => p.Item.Key).ToList();
            if (hero.Has("ranged")) Assert.DoesNotContain("item_bfury", all);
            if (hero.Has("melee")) Assert.DoesNotContain("item_dragon_lance", all);
        }
    }

    [Fact]
    public void MainBuild_HasAtMostOnePairOfBoots()
    {
        foreach (var hero in TestData.Db.Heroes)
        {
            var b = Engine.Build(hero, hero.Positions[0]);
            var main = new[] { BuildPhase.Start, BuildPhase.Early, BuildPhase.Mid, BuildPhase.Late }.SelectMany(p => b[p]);
            Assert.True(main.Count(p => p.Item.Group == "boots") <= 1, hero.Key);
        }
    }

    [Fact]
    public void CuratedBuild_IsUsed_ForAntiMage()
    {
        var b = Engine.Build(TestData.Hero("antimage"), Position.Carry);
        Assert.True(b.HasCuratedBase);
        Assert.Contains(b[BuildPhase.Mid], p => p.Item.Key == "item_bfury" && p.Curated);
    }

    [Fact]
    public void HardSupport_GetsVisionItems()
    {
        var b = Engine.Build(TestData.Hero("lich"), Position.HardSupport);
        var keys = b[BuildPhase.Start].Concat(b[BuildPhase.Early]).Select(p => p.Item.Key).ToList();
        Assert.Contains("item_ward_observer", keys);
    }

    [Fact]
    public void Situational_ReactsToEnemyEvasion_WithMkb()
    {
        var sniper = TestData.Hero("sniper");
        var enemies = new[] { TestData.Hero("phantom_assassin"), TestData.Hero("windrunner") };
        var b = Engine.Build(sniper, Position.Mid, enemies);
        Assert.True(b.EnemyAware);
        var mkb = b[BuildPhase.Situational].FirstOrDefault(p => p.Item.Key == "item_monkey_king_bar");
        Assert.NotNull(mkb);
        Assert.Contains(mkb!.Reasons, r => r.En.Contains("Phantom Assassin"));
    }

    [Fact]
    public void Situational_WithoutEnemies_IsConditional()
    {
        var b = Engine.Build(TestData.Hero("sniper"), Position.Mid);
        Assert.False(b.EnemyAware);
        Assert.All(b[BuildPhase.Situational], p => Assert.Contains(p.Reasons, r => r.En.StartsWith("if enemies have")));
    }

    [Fact]
    public void NextItem_SkipsOwnedAndStartItemsAfterHorn()
    {
        var b = Engine.Build(TestData.Hero("antimage"), Position.Carry);
        var first = b.NextItem(null);
        Assert.Equal(BuildPhase.Start, first!.Item.Phase);
        var afterHorn = b.NextItem(Array.Empty<string>(), 120);
        Assert.NotEqual(BuildPhase.Start, afterHorn!.Item.Phase);
        var owned = b[BuildPhase.Early].Select(p => p.Item.Key).ToList();
        var next = b.NextItem(owned, 300);
        Assert.DoesNotContain(next!.Item.Key, owned);
    }

    [Fact]
    public void Explanations_MentionItemPurpose()
    {
        var b = Engine.Build(TestData.Hero("axe"), Position.Offlane);
        var blink = b[BuildPhase.Mid].First(p => p.Item.Key == "item_blink");
        Assert.Contains("инициации", blink.Explain(Language.Russian));
    }
}
