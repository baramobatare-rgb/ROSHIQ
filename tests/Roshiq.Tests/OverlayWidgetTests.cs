using Roshiq.Core.Layout;
using Roshiq.Recommendations;
using Xunit;

namespace Roshiq.Tests;

/// <summary>0.24: карточка-совет у виджета и «тишина в драке».</summary>
public class OverlayWidgetTests
{
    private static readonly PixelRect Screen = new(0, 0, 1920, 1080);

    [Fact]
    public void Card_SitsAboveWidget_SameWidth()
    {
        var widget = new PixelRect(1500, 400, 360, 200);
        var card = CardPlacement.Place(widget, Screen, 110, 8);
        Assert.Equal(new PixelRect(1500, 282, 360, 110), card);
    }

    [Fact]
    public void Card_GoesBelow_WhenTopBarIsInTheWay()
    {
        var widget = new PixelRect(1500, 100, 360, 200);   // над виджетом только 100 px — там верхняя панель игры
        var card = CardPlacement.Place(widget, Screen, 110, 8);
        Assert.Equal(308, card.Y);
        Assert.Equal(1500, card.X);
    }

    [Fact]
    public void Card_StaysOnScreen_WhenNoRoomAnywhere()
    {
        var widget = new PixelRect(1700, 90, 360, 960);
        var card = CardPlacement.Place(widget, Screen, 110, 8);
        Assert.True(card.X + card.Width <= Screen.Width);
        Assert.True(card.Y >= (int)(Screen.Height * SafeZone.TopBar.Bottom) - 1);
    }

    [Fact]
    public void Fight_StartsOnFastHealthDrop_AndEndsAfterHold()
    {
        var f = new FightDetector();
        var t0 = new DateTime(2026, 11, 14, 12, 0, 0, DateTimeKind.Utc);
        f.Observe(t0, 1000, 1000, true);
        f.Observe(t0.AddSeconds(1), 980, 1000, true);          // −2% — не драка
        Assert.False(f.InFight(t0.AddSeconds(1)));
        f.Observe(t0.AddSeconds(2), 820, 1000, true);          // −18% за 2 секунды
        Assert.True(f.InFight(t0.AddSeconds(2)));
        Assert.True(f.InFight(t0.AddSeconds(5)));
        Assert.False(f.InFight(t0.AddSeconds(6.5)));
    }

    [Fact]
    public void Fight_SlowDrain_IsNotAFight_AndDeathResets()
    {
        var f = new FightDetector();
        var t0 = DateTime.UtcNow;
        for (int i = 0; i < 10; i++) f.Observe(t0.AddSeconds(i), 1000 - i * 30, 1000, true);   // −3% в секунду
        Assert.False(f.InFight(t0.AddSeconds(9)));
        f.Observe(t0.AddSeconds(10), 500, 1000, true);
        Assert.True(f.InFight(t0.AddSeconds(10)));
        f.Observe(t0.AddSeconds(11), 0, 1000, false);
        Assert.False(f.InFight(t0.AddSeconds(11)));
    }

    [Fact]
    public void Dispatcher_HoldsNormalTips_DuringFight_ButNotUrgent()
    {
        var d = new TipDispatcher();
        var now = DateTime.UtcNow;
        d.Enqueue(new Tip { Kind = "macro", Header = "H", Text = "Давите вышку", InGameOnly = true }, now);
        var fight = new TipContext(false, true, false, false, false, InFight: true);
        Assert.Null(d.Next(fight, now));
        d.Enqueue(new Tip { Kind = "timer", Header = "H", Text = "Руны", InGameOnly = true, Priority = TipPriority.Urgent }, now);
        Assert.Equal("timer", d.Next(fight, now)!.Kind);
        Assert.Equal("macro", d.Next(fight with { InFight = false }, now.AddSeconds(20))!.Kind);
    }
}
