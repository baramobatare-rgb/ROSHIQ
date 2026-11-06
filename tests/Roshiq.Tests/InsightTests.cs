using Roshiq.Core.Models;
using Roshiq.Data;
using Roshiq.Recommendations;
using Xunit;

namespace Roshiq.Tests;

public class InsightTests
{
    private static Hero H(int id, params string[] traits) =>
        new(id, "h" + id, "Hero " + id, "Герой " + id, PrimaryAttribute.Agility, new[] { Position.Carry }, traits);

    [Fact]
    public void LaneTips_LoadedFromData()
    {
        Assert.NotEmpty(TestData.Db.LaneTips);
        Assert.Contains(TestData.Db.LaneTips, t => t.Trait == "dot");
        Assert.Empty(TestData.Db.Warnings.Where(w => w.Contains("lane_tips")));
    }

    [Fact]
    public void LaneAdvisor_TakesTopTipsAndPersonalStats()
    {
        var rules = new[]
        {
            new LaneTip("dot", "доt", "dot"), new LaneTip("early_game", "рано", "early"), new LaneTip("ranged", "дальний", "ranged")
        };
        var viper = H(1, "dot", "early_game", "ranged");
        var plain = H(2, "melee");
        var against = new Dictionary<int, (int, int)> { [1] = (20, 6), [2] = (2, 0) };
        var advice = LaneAdvisor.Advise(new[] { viper, plain }, rules, against);
        var a = Assert.Single(advice); // у второго нет ни советов, ни статистики (2 игры — мало)
        Assert.Equal(new[] { "dot", "early_game" }, a.Tips.Select(t => t.Trait)); // не больше двух, по порядку важности
        Assert.Equal(20, a.GamesAgainst);
        Assert.Contains("30%", a.Personal(Language.English).Replace(" ", "").Replace("\u00A0", ""));
    }

    [Fact]
    public void Teams_EarlyVsLate()
    {
        var early = Enumerable.Range(1, 3).Select(i => H(i, "early_game")).ToList();
        var late = Enumerable.Range(10, 3).Select(i => H(i, "late_game")).ToList();
        var t = PowerSpikes.Compare(early, late);
        Assert.Equal(TeamPhase.EarlyStronger, t.Ours);
        Assert.NotNull(t.Plan);
        Assert.Equal(TeamPhase.LateStronger, PowerSpikes.Compare(late, early).Ours);
        Assert.Equal(TeamPhase.Even, PowerSpikes.Compare(early.Take(2).ToList(), late).Ours); // мало героев — без выводов

        Assert.Equal("spike_push", PowerSpikes.ClockReminder(t, 730)!.Value.Key);
        Assert.Null(PowerSpikes.ClockReminder(t, 800));
        Assert.Equal("spike_survive", PowerSpikes.ClockReminder(PowerSpikes.Compare(late, early), 610)!.Value.Key);
    }

    [Fact]
    public void Spikes_AnnounceEachKeyItemOnce()
    {
        var shown = new HashSet<string>();
        int? Cost(string k) => k == "item_expensive_core" ? 4000 : k == "item_cheap_core" ? 1000 : null;
        var first = PowerSpikes.NewSpikes(new[] { "item_black_king_bar", "item_tango", "item_expensive_core", "item_cheap_core" },
            shown, Cost, new[] { "item_expensive_core", "item_cheap_core" });
        Assert.Equal(new[] { "item_black_king_bar", "item_expensive_core" }, first);
        Assert.Empty(PowerSpikes.NewSpikes(new[] { "item_black_king_bar" }, shown, Cost, Array.Empty<string>()));
    }

    [Fact]
    public void Meta_MapsIdsFiltersAndDeduplicates()
    {
        var ids = new Dictionary<int, string> { [1] = "item_blink", [2] = "item_tango", [3] = "item_black_king_bar", [4] = "item_unknown_thing", [5] = "item_ward_observer" };
        var raw = new Dictionary<BuildPhase, IReadOnlyDictionary<int, int>>
        {
            [BuildPhase.Start] = new Dictionary<int, int> { [2] = 900, [5] = 400 },
            [BuildPhase.Mid] = new Dictionary<int, int> { [1] = 300, [3] = 600, [2] = 50, [4] = 999 },
            [BuildPhase.Late] = new Dictionary<int, int> { [3] = 100, [1] = 20 },
        };
        var meta = MetaBuildBuilder.Build(7, raw, ids, k => k != "item_unknown_thing");
        Assert.Equal(new[] { "item_tango", "item_ward_observer" }, meta.Phases[BuildPhase.Start].Select(i => i.ItemKey)); // расходники в старте можно
        Assert.Equal(new[] { "item_black_king_bar", "item_blink" }, meta.Phases[BuildPhase.Mid].Select(i => i.ItemKey)); // без расходников и неизвестных
        Assert.Equal(1.0, meta.Phases[BuildPhase.Mid][0].Share);
        Assert.False(meta.Phases.ContainsKey(BuildPhase.Late)); // всё уже было в средней фазе

        var path = Path.Combine(Path.GetTempPath(), "roshiq-meta-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            new MetaBuildCache(path).Set(meta);
            var again = new MetaBuildCache(path);
            Assert.Equal(2, again.Get(7)!.Phases[BuildPhase.Mid].Count);
            Assert.False(again.IsStale(7));
            Assert.True(again.IsStale(8));
        }
        finally { try { File.Delete(path); } catch { } }
        Assert.Equal(BuildPhase.Early, MetaBuildCache.PhaseOf("early_game_items"));
    }
}
