using Roshiq.Data;
using Xunit;

namespace Roshiq.Tests;

/// <summary>0.21.4: порядок прокачки из про-матчей (ключи как в OpenDota constants/ability_ids, Anti-Mage, 12.11.2026).</summary>
public class SkillBuildTests
{
    private const string Q = "antimage_mana_break", W = "antimage_blink", E = "antimage_counterspell", R = "antimage_mana_void";
    private const string T10a = "special_bonus_hp_regen_3", T10b = "special_bonus_unique_antimage_manavoid_aoe";
    private const string T15a = "special_bonus_unique_antimage_5", T15b = "special_bonus_unique_antimage_6";

    private static readonly Dictionary<string, int> Talents = new()
    {
        [T10a] = 1, [T10b] = 1, [T15a] = 2, [T15b] = 2
    };

    private static string Name(string k) => k switch { Q => "Mana Break", W => "Blink", E => "Counterspell", R => "Mana Void", _ => k };

    private static List<string> Seq(params string[] s) => s.ToList();

    [Fact]
    public void PicksTheRealBuildClosestToTheOthers()
    {
        var common = Seq(Q, W, Q, W, W, R, W, E, Q, T10b, Q, R, E, E, T15b, E, "special_bonus_attributes");
        var seqs = new List<IReadOnlyList<string>>
        {
            common,
            Seq(Q, W, Q, W, W, R, W, E, Q, T10b, Q, R, E, E, T15b, E),
            Seq(W, Q, Q, W, W, R, W, E, Q, T10a, Q, R, E, E, T15b, E),
            Seq(Q, E, Q, E, E, R, E, W, W, T10b, W, R, Q, Q, T15a, Q),   // необычная прокачка — не должна стать образцом
        };
        var b = SkillBuildBuilder.Build(1, seqs, Talents, Name, DateTime.UtcNow)!;
        Assert.Equal(4, b.Matches);
        Assert.Equal(new[] { "Mana Break", "Blink", "Mana Break", "Blink", "Blink", "Mana Void" }, b.Order.Take(6).Select(s => s.Name));
        Assert.Equal(SkillBuildBuilder.ReliableLevels, b.Order.Count);
        Assert.True(b.Order[9].Talent);
        Assert.Equal("Mana Void", b.AtLevel(6)!.Name);
        Assert.Null(b.AtLevel(17));   // после 16-го уровня номер уровня и номер очка расходятся — не подсказываем
        Assert.Null(b.AtLevel(0));

        Assert.Equal(2, b.Talents.Count);
        Assert.Equal(new TalentPick(10, T10b, 3, 4), b.Talents[0]);
        Assert.Equal(new TalentPick(15, T15b, 3, 4), b.Talents[1]);
    }

    [Fact]
    public void NeedsAtLeastThreeUsableMatches()
    {
        var seqs = new List<IReadOnlyList<string>> { Seq(Q, W, Q, W, W, R, W, E, Q, Q), Seq(Q, W, Q, W, W, R, W, E, Q, Q), Seq(Q, W, Q) };
        Assert.Null(SkillBuildBuilder.Build(1, seqs, Talents, Name, DateTime.UtcNow));
    }

    [Theory]
    [InlineData("special_bonus_unique_antimage_manavoid_aoe", "+{s:bonus_mana_void_aoe_radius} Mana Void Radius", "Mana Void Radius")]
    [InlineData("special_bonus_evasion_15", "+{s:value}% Evasion", "Evasion")]
    [InlineData("antimage_blink", "Blink", "Blink")]
    [InlineData("special_bonus_attributes", null, "Attributes")]
    [InlineData("special_bonus_unknown", null, "special_bonus_unknown")]
    public void CleansTalentTemplates(string key, string? dname, string expected) =>
        Assert.Equal(expected, SkillBuildBuilder.CleanName(key, dname));

    [Fact]
    public void Cache_RoundTrip()
    {
        var path = Path.Combine(Path.GetTempPath(), "roshiq-skills-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var seqs = Enumerable.Range(0, 3).Select(_ => (IReadOnlyList<string>)Seq(Q, W, Q, W, W, R, W, E, Q, T10b, Q, R)).ToList();
            new SkillBuildCache(path).Put(SkillBuildBuilder.Build(1, seqs, Talents, Name, DateTime.UtcNow)!);
            var cache = new SkillBuildCache(path);
            Assert.True(cache.IsFresh(1, DateTime.UtcNow));
            Assert.False(cache.IsFresh(1, DateTime.UtcNow.AddDays(8)));
            Assert.Equal("Blink", cache.Get(1)!.AtLevel(2)!.Name);
        }
        finally { try { File.Delete(path); } catch { } }
    }
}
