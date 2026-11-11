using Roshiq.Core.Models;
using Roshiq.Recommendations;
using Xunit;

namespace Roshiq.Tests;

/// <summary>0.18.7: компоненты и промежуточные предметы (Кристалис, Кая, Яша…) ищутся, но движок их не советует.</summary>
public class ItemCatalogTests
{
    private static bool Matches(Item i, string q) =>
        i.Name.Contains(q, StringComparison.OrdinalIgnoreCase) || i.NameRu.Contains(q, StringComparison.OrdinalIgnoreCase)
        || i.Key.Contains(q, StringComparison.OrdinalIgnoreCase);

    [Theory]
    [InlineData("кристалис", "item_lesser_crit")]
    [InlineData("Crystalys", "item_lesser_crit")]
    [InlineData("кая", "item_kaya")]
    [InlineData("яша", "item_yasha")]
    [InlineData("санге", "item_sange")]
    [InlineData("Ogre", "item_ogre_axe")]
    [InlineData("Perseverance", "item_pers")]
    public void Search_FindsComponents(string query, string key)
    {
        Assert.Contains(TestData.Db.Items, i => i.Key == key && Matches(i, query));
    }

    [Fact]
    public void CatalogItems_NeverRecommended()
    {
        var engine = new ItemBuildEngine(TestData.Db);
        var enemies = TestData.Db.Heroes.Take(5).ToList();
        foreach (var h in TestData.Db.Heroes)
            foreach (var p in h.Positions)
            {
                var b = engine.Build(h, p, enemies);
                // каталожный предмет может прийти только из базовой сборки героя (данные), но не из подбора движка
                Assert.DoesNotContain(b.Phases.Values.SelectMany(x => x), x => x.Item.CatalogOnly && !x.Curated);
                Assert.DoesNotContain(engine.Adaptations(h, p, enemies, b), a => a.Pick.Item.CatalogOnly);
            }
    }

    [Fact]
    public void Catalog_IsLarge_AndKeysUnique()
    {
        var items = TestData.Db.Items;
        Assert.True(items.Count(i => i.CatalogOnly) >= 60);
        Assert.Equal(items.Count, items.Select(i => i.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
