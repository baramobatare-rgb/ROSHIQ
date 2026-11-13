using Roshiq.Recommendations;
using Xunit;

namespace Roshiq.Tests;

/// <summary>0.23.1: подсказки «что делать сейчас» по счёту убийств и смене дня и ночи.</summary>
public class MacroAdvisorTests
{
    [Fact]
    public void WonFight_SuggestsTowerEarly_AndRoshanLater()
    {
        var a = new MacroAdvisor();
        Assert.Null(a.Observe(600, 5, 5, true, true));
        Assert.Null(a.Observe(610, 7, 5, true, true));
        var tip = a.Observe(620, 8, 6, true, true);       // +3 на +1 за 20 секунд
        Assert.Equal("won_fight", tip!.Key);
        Assert.Contains("вышку", tip.Ru);
        Assert.DoesNotContain("Рошан", tip.Ru);

        var late = new MacroAdvisor();
        late.Observe(1200, 20, 20, true, true);
        Assert.Contains("Рошан", late.Observe(1215, 24, 20, true, true)!.Ru);
    }

    [Fact]
    public void LostFight_And_Cooldown()
    {
        var a = new MacroAdvisor();
        a.Observe(900, 10, 10, true, true);
        var tip = a.Observe(915, 10, 14, true, false);
        Assert.Equal("lost_fight", tip!.Key);
        Assert.Contains("после возрождения", tip.Ru);
        a.Observe(930, 10, 14, true, true);
        Assert.Null(a.Observe(945, 10, 18, true, true));   // кулдаун 75 секунд
    }

    [Fact]
    public void SlowTrades_AreNotAFight()
    {
        var a = new MacroAdvisor();
        a.Observe(600, 0, 0, true, true);
        a.Observe(630, 1, 0, true, true);
        a.Observe(660, 2, 0, true, true);
        Assert.Null(a.Observe(690, 3, 0, true, true));      // по одному убийству раз в 30 секунд
    }

    [Fact]
    public void Night_NotBeforeFiveMinutes()
    {
        var a = new MacroAdvisor();
        a.Observe(230, null, null, true, true);
        Assert.Null(a.Observe(240, null, null, false, true));
    }

    [Fact]
    public void Night_Tip()
    {
        var a = new MacroAdvisor();
        a.Observe(590, null, null, true, true);
        Assert.Equal("night", a.Observe(600, null, null, false, true)!.Key);
        a.Observe(890, null, null, true, true);
        Assert.Null(a.Observe(900, null, null, false, true));   // через 5 минут — не повторяем
        a.Observe(1190, null, null, true, true);
        Assert.Equal("night", a.Observe(1200, null, null, false, true)!.Key);
    }

    [Fact]
    public void ClockGoingBack_Resets()
    {
        var a = new MacroAdvisor();
        a.Observe(1000, 30, 30, true, true);
        Assert.Null(a.Observe(10, 0, 0, true, true));         // новый матч, не «проиграли 30 убийств»
    }
}
