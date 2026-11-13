using System.Text.Json;
using Roshiq.Core.Models;
using Roshiq.Data;
using Roshiq.Recommendations;
using Xunit;

namespace Roshiq.Tests;

/// <summary>0.23.0: тир-лист по данным OpenDota /heroStats.</summary>
public class TierListTests
{
    // поля — как в реальном ответе /heroStats (13.11.2026): 1_pick…8_pick, 1_win…8_win, pro_pick, pro_win
    private static HeroTierStats Stats()
    {
        string Row(int id, int pickEach, int winEach, int pro, int proWin)
        {
            var parts = new List<string> { $"\"id\":{id}", $"\"pro_pick\":{pro}", $"\"pro_win\":{proWin}" };
            for (int i = 1; i <= 8; i++) { parts.Add($"\"{i}_pick\":{pickEach}"); parts.Add($"\"{i}_win\":{winEach}"); }
            return "{" + string.Join(",", parts) + "}";
        }
        var json = "[" + string.Join(",",
            Row(1, 10000, 5400, 40, 26),    // Anti-Mage: 54% → S
            Row(2, 10000, 5000, 5, 5),      // Axe: 50% → B, на про-сцене слишком мало игр
            Row(26, 10000, 4700, 30, 12),   // Lion: 47% → D
            Row(44, 30, 27, 0, 0),          // Phantom Assassin: 90% из 240 игр — редкая, отсекается порогом
            Row(11, 50000, 25600, 0, 0)) + "]";
        using var doc = JsonDocument.Parse(json);
        return HeroTierStats.Parse(doc.RootElement, DateTime.UtcNow);
    }

    [Fact]
    public void Parse_ReadsAllBrackets()
    {
        var s = Stats();
        Assert.Equal(5, s.Rows.Count);
        Assert.Equal(10000, s.Rows[0].Picks[7]);
        Assert.Equal(26, s.Rows[0].ProWin);
    }

    [Fact]
    public void Carry_TiersByWinRate_RareHeroesHidden()
    {
        var list = TierList.Build(Stats(), TestData.Db.Heroes, Position.Carry, RankBracket.All);
        var am = list.Single(e => e.Hero.Key == "antimage");
        Assert.Equal("S", am.Tier);
        Assert.InRange(am.WinRate, 0.535, 0.541);
        Assert.DoesNotContain(list, e => e.Hero.Key == "phantom_assassin");   // меньше 0,5% матчей
        Assert.DoesNotContain(list, e => e.Hero.Key == "lion");               // Lion не керри по базе
        Assert.Equal(list.OrderByDescending(e => e.WinRate).Select(e => e.Hero.Id), list.Select(e => e.Hero.Id));
    }

    [Fact]
    public void ProBracket_NeedsTenGames_AndIsSmoothedToFifty()
    {
        var list = TierList.Build(Stats(), TestData.Db.Heroes, Position.Carry, RankBracket.Pro);
        var am = list.Single(e => e.Hero.Key == "antimage");
        // 26 из 40 = 65%, но со сглаживанием (+100 игр по 50%) — около 54%
        Assert.InRange(am.WinRate, 0.53, 0.55);
    }

    [Theory]
    [InlineData(null, RankBracket.All)]
    [InlineData(15, RankBracket.Low)]
    [InlineData(54, RankBracket.Mid)]
    [InlineData(72, RankBracket.High)]
    [InlineData(80, RankBracket.Immortal)]
    public void Bracket_FromMedal(int? rankTier, RankBracket expected) => Assert.Equal(expected, TierList.FromRankTier(rankTier));

    [Theory]
    [InlineData(0.531, "S")]
    [InlineData(0.52, "A")]
    [InlineData(0.501, "B")]
    [InlineData(0.49, "C")]
    [InlineData(0.46, "D")]
    public void Tier_Thresholds(double wr, string tier) => Assert.Equal(tier, TierList.TierFor(wr));
}
