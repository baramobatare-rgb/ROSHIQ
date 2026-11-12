using Roshiq.Recommendations;
using Xunit;

namespace Roshiq.Tests;

/// <summary>0.21.5: таймер глифа врагов по ручной отметке и волна с катапультой.</summary>
public class GlyphTimerTests
{
    [Fact]
    public void Glyph_ReadyAfterFiveMinutes_AnnouncedOnce()
    {
        var g = new GlyphTracker();
        Assert.Null(g.ReadyIn(600));
        g.MarkUsed(600);
        Assert.Equal(300, g.ReadyIn(600));
        Assert.Equal(60, g.ReadyIn(840));
        Assert.False(g.DueReady(840));
        Assert.True(g.DueReady(900));
        Assert.False(g.DueReady(905));   // второй раз не напоминаем
        Assert.Equal(0, g.ReadyIn(905));
        Assert.Null(g.ReadyIn(961));     // давно готов — отметка уходит из строки таймеров
    }

    [Fact]
    public void Glyph_ClearsWhenClockGoesBack()
    {
        var g = new GlyphTracker();
        g.MarkUsed(1500);
        Assert.Null(g.ReadyIn(100));     // новый матч
        Assert.Null(g.UsedAt);
    }

    [Fact]
    public void SiegeWave_EveryFiveMinutesFromFive()
    {
        var siege = TestData.Db.Timers.Single(t => t.Key == "siege");
        Assert.Equal(300, siege.FirstSec);
        Assert.Equal(300, siege.IntervalSec);
    }
}
