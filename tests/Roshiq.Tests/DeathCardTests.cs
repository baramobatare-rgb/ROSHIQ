using Roshiq.Recommendations;
using Xunit;

namespace Roshiq.Tests;

/// <summary>0.21: карточка после смерти — только известные факты и один самый важный совет.</summary>
public class DeathCardTests
{
    private static DeathEvent D(int clock, int gold = 300, bool tp = true, bool? bb = null, int? x = null, int? y = null) =>
        new() { Clock = clock, GoldAtRisk = gold, HadTeleport = tp, BuybackReady = bb, X = x, Y = y };

    [Fact]
    public void Facts_ShowTimePlaceGoldTpAndBuyback()
    {
        // нижняя линия у Radiant: правый нижний край карты
        var (facts, _) = DeathCard.Build(D(1925, gold: 1840, tp: false, bb: false, x: 7000, y: -6000), Array.Empty<DeathEvent>(), "radiant");
        Assert.Equal("32:05 · Нижняя линия · 1840 золота при себе · без ТП · на выкуп не хватает", facts.Ru);
        Assert.Contains("no TP", facts.En);
    }

    [Fact]
    public void UnknownPlace_IsNotInvented_AndBuybackOnlyLate()
    {
        var (facts, _) = DeathCard.Build(D(600, bb: false), Array.Empty<DeathEvent>(), "dire");
        Assert.Equal("10:00 · 300 золота при себе · ТП был", facts.Ru);
    }

    [Fact]
    public void Tip_ThirdDeathInSameArea_BeatsEverythingElse()
    {
        var earlier = new[] { D(400, x: 7000, y: -6000), D(700, x: 7100, y: -5900) };
        var (_, tip) = DeathCard.Build(D(900, gold: 2500, tp: false, x: 6900, y: -6100), earlier, "radiant");
        Assert.Equal("death_zone", tip?.Kind);
        Assert.Contains("3-я", tip!.Ru);
    }

    [Fact]
    public void Tip_Order_NoTpThenGoldThenBuyback()
    {
        var none = Array.Empty<DeathEvent>();
        Assert.Equal("death_no_tp", DeathCard.Build(D(600, gold: 2000, tp: false), none, null).Tip?.Kind);
        Assert.Equal("death_gold", DeathCard.Build(D(600, gold: 2000), none, null).Tip?.Kind);
        Assert.Equal("death_buyback", DeathCard.Build(D(2000, bb: false), none, null).Tip?.Kind);
        Assert.Null(DeathCard.Build(D(600), none, null).Tip);
        // в первые 4 минуты ТП ещё не обязателен
        Assert.Null(DeathCard.Build(D(120, tp: false), none, null).Tip);
    }
}
