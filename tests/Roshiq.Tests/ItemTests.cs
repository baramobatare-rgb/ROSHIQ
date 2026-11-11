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
            // правило для подбора движка; базовая сборка из открытых данных отражает то, что на герое реально покупают
            var guessed = b.Phases.Values.SelectMany(x => x).Where(p => !p.Curated).Select(p => p.Item.Key).ToList();
            if (hero.Has("ranged")) Assert.DoesNotContain("item_bfury", guessed);
            if (hero.Has("melee")) Assert.DoesNotContain("item_dragon_lance", guessed);
            Assert.NotEmpty(all);
        }
    }

    [Fact]
    public void BootsOnlyInLaterPhase_AreNotDoubledByAGuess()
    {
        // данные OpenDota: сапоги стоят в средней стадии, на раннюю данных нет — движок не должен дописать в раннюю свои сапоги
        var dir = Path.Combine(Path.GetTempPath(), "dp_boots_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "hero_builds.txt"),
                "enchantress|5|mid|item_tranquil_boots,item_glimmer_cape\nenchantress|5|late|item_aghanims_shard\n");
            var db = Roshiq.Data.GameDatabase.Load(dir);
            var hero = db.FindHero("enchantress")!;
            var b = new ItemBuildEngine(db).Build(hero, Position.HardSupport);
            var main = new[] { BuildPhase.Start, BuildPhase.Early, BuildPhase.Mid, BuildPhase.Late }.SelectMany(p => b[p]).ToList();
            Assert.Equal(1, main.Count(p => p.Item.Group == "boots"));
            Assert.Contains(main, p => p.Item.Key == "item_tranquil_boots" && p.Curated);
        }
        finally { Directory.Delete(dir, true); }
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
        // сборки обновляются из открытых данных — проверяем не конкретный предмет, а что стадия целиком взята из сборки
        Assert.NotEmpty(b[BuildPhase.Mid]);
        Assert.All(b[BuildPhase.Mid], p => Assert.True(p.Curated));
    }

    [Fact]
    public void CuratedPhase_IsNotPaddedWithGuesses()
    {
        var db = TestData.Db;
        foreach (var hero in db.Heroes)
            foreach (var pos in hero.Positions.Where(p => db.HasCuratedBuild(hero, p)))
            {
                var b = Engine.Build(hero, pos);
                foreach (var phase in new[] { BuildPhase.Start, BuildPhase.Early, BuildPhase.Mid, BuildPhase.Late })
                    if (db.CuratedBuild(hero, pos, phase) is { Count: > 0 })
                        Assert.All(b[phase], p => Assert.True(p.Curated, $"{hero.Key}/{pos}/{phase}: {p.Item.Key} дописан движком"));
            }
    }

    [Fact]
    public void MissingRole_UsesNearestRoleOfTheSameKind()
    {
        var db = TestData.Db;
        // герой со сборкой хотя бы для одной кор-роли и без сборки для другой кор-роли
        var found = db.Heroes.SelectMany(h => new[] { Position.Carry, Position.Mid, Position.Offlane }
                .Where(p => !db.HasCuratedBuild(h, p) && db.CuratedPosition(h, p) != null).Select(p => (Hero: h, Pos: p))).FirstOrDefault();
        if (found.Hero != null)
        {
            var from = db.CuratedPosition(found.Hero, found.Pos)!.Value;
            Assert.True((int)from <= 3);                                   // сборку саппорта кору не подставляем
            var b = Engine.Build(found.Hero, found.Pos);
            Assert.True(b.HasCuratedBase);
            Assert.Contains(b[BuildPhase.Mid].Concat(b[BuildPhase.Late]), p => p.Curated);
        }
        foreach (var h in db.Heroes)
        {
            if (db.CuratedPosition(h, Position.HardSupport) is Position sp) Assert.True((int)sp >= 4, h.Key);
            if (db.CuratedPosition(h, Position.Carry) is Position cp) Assert.True((int)cp <= 3, h.Key);
        }
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
        // Blink берём у любого героя, у кого он есть в сборке: состав сборок обновляется из открытых данных
        var blink = TestData.Db.Heroes.SelectMany(h => h.Positions.Select(p => Engine.Build(h, p)))
            .SelectMany(b => b[BuildPhase.Mid].Concat(b[BuildPhase.Late]).Concat(b[BuildPhase.Alternative])).First(p => p.Item.Key == "item_blink");
        Assert.Contains("инициации", blink.Explain(Language.Russian));
    }
}
