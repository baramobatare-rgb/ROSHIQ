using Roshiq.Core.Models;
using Xunit;

namespace Roshiq.Tests;

public class DataTests
{
    [Fact]
    public void Database_Loads_WithoutWarnings()
    {
        var db = TestData.Db;
        Assert.True(db.Heroes.Count >= 120, $"героев {db.Heroes.Count}");
        Assert.True(db.Items.Count >= 80);
        Assert.NotEmpty(db.CounterRules);
        Assert.NotEmpty(db.SynergyRules);
        Assert.Empty(db.Warnings);
    }

    [Fact]
    public void EveryHero_HasPositionsAndKnownTraits()
    {
        foreach (var h in TestData.Db.Heroes)
        {
            Assert.NotEmpty(h.Positions);
            Assert.True(h.Has("melee") ^ h.Has("ranged"), $"{h.Key}: должен быть ровно один тип атаки");
            foreach (var t in h.Traits) Assert.NotNull(TestData.Db.Trait(t));
        }
    }

    [Fact]
    public void HeroIdsAndKeys_AreUnique()
    {
        var heroes = TestData.Db.Heroes;
        Assert.Equal(heroes.Count, heroes.Select(h => h.Id).Distinct().Count());
        Assert.Equal(heroes.Count, heroes.Select(h => h.Key).Distinct().Count());
    }

    [Theory]
    [InlineData("antimage", 1)]
    [InlineData("npc_dota_hero_axe", 2)]
    [InlineData("Shadow Fiend", 11)]
    [InlineData("Пудж", 14)]
    [InlineData("zuus", 22)]
    public void FindHero_MatchesKeyNameAndInternalName(string query, int expectedId)
    {
        Assert.Equal(expectedId, TestData.Db.FindHero(query)?.Id);
    }

    [Fact]
    public void FindHero_ById_AndUnknown()
    {
        Assert.Equal("crystal_maiden", TestData.Db.FindHero(5)?.Key);
        Assert.Null(TestData.Db.FindHero("not_a_hero"));
        Assert.Null(TestData.Db.FindHero(9999));
    }

    [Fact]
    public void Search_FiltersByPositionAndText()
    {
        var supports = TestData.Db.Search(null, Position.HardSupport).ToList();
        Assert.Contains(supports, h => h.Key == "crystal_maiden");
        Assert.DoesNotContain(supports, h => h.Key == "antimage");
        Assert.Contains(TestData.Db.Search("лион"), h => h.Key == "lion");
    }

    [Fact]
    public void Items_HaveRussianAndEnglishPurpose()
    {
        foreach (var i in TestData.Db.Items)
        {
            Assert.False(string.IsNullOrWhiteSpace(i.PurposeRu), i.Key);
            Assert.False(string.IsNullOrWhiteSpace(i.PurposeEn), i.Key);
            Assert.StartsWith("item_", i.Key);
        }
    }

    [Fact]
    public void Override_Directory_ReplacesBuiltInFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dp_override_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "data_version.txt"), "dataVersion=test-123\npatchBaseline=x\n");
            var db = Roshiq.Data.GameDatabase.Load(dir);
            Assert.Equal("test-123", db.DataVersion);
            Assert.Contains("data_version.txt", db.OverriddenFiles);
            Assert.Equal(TestData.Db.Heroes.Count, db.Heroes.Count);
        }
        finally { Directory.Delete(dir, true); }
    }
}
