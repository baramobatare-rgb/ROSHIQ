using Roshiq.Core.Models;
using Roshiq.Core.Settings;
using Roshiq.Recommendations;
using Xunit;

namespace Roshiq.Tests;

public class ReminderTests
{
    private static NotificationSettings Settings() => new()
    {
        Enabled = true, FirstReminderAtSec = 60, DisplaySec = 6, MinGapSec = 45,
        ObserverIntervalSec = 360, SentryIntervalSec = 420, SmokeIntervalSec = 600, ConsumablesIntervalSec = 480
    };

    private static List<(int T, Reminder R)> Run(SupportReminderScheduler s, Position pos, int from, int to)
    {
        var shown = new List<(int, Reminder)>();
        for (int t = from; t <= to; t++)
        {
            var r = s.Tick(TimeSpan.FromSeconds(t), pos, true);
            if (r != null) shown.Add((t, r));
        }
        return shown;
    }

    [Fact]
    public void NeverMoreThanOneActive_AndAutoHides()
    {
        var s = new SupportReminderScheduler(Settings());
        int active = 0, maxActive = 0;
        s.Shown += (_, _) => { active++; maxActive = Math.Max(maxActive, active); };
        s.Hidden += (_, _) => active--;
        var shown = Run(s, Position.HardSupport, -90, 1800);
        Assert.True(shown.Count >= 5);
        Assert.Equal(1, maxActive);
        for (int i = 1; i < shown.Count; i++)
            Assert.True(shown[i].T - shown[i - 1].T >= 6 + 45, "между напоминаниями соблюдается пауза");
    }

    [Fact]
    public void NoReminders_BeforeFirstTime_OrForCores_OrWhenDisabled()
    {
        var s = new SupportReminderScheduler(Settings());
        Assert.Empty(Run(s, Position.SoftSupport, -90, 59));
        Assert.Empty(Run(new SupportReminderScheduler(Settings()), Position.Carry, 0, 1800));
        var off = Settings(); off.Enabled = false;
        Assert.Empty(Run(new SupportReminderScheduler(off), Position.HardSupport, 0, 1800));
    }

    [Fact]
    public void DisabledKind_IsNeverShown()
    {
        var st = Settings(); st.SmokeEnabled = false;
        var shown = Run(new SupportReminderScheduler(st), Position.SoftSupport, 0, 3000);
        Assert.DoesNotContain(shown, x => x.R.Kind == ReminderKind.Smoke);
        Assert.Contains(shown, x => x.R.Kind == ReminderKind.ObserverWard);
    }

    [Fact]
    public void Observer_RepeatsRoughlyAtInterval()
    {
        var shown = Run(new SupportReminderScheduler(Settings()), Position.HardSupport, 0, 2400)
            .Where(x => x.R.Kind == ReminderKind.ObserverWard).Select(x => x.T).ToList();
        Assert.True(shown.Count >= 4);
        for (int i = 1; i < shown.Count; i++) Assert.InRange(shown[i] - shown[i - 1], 360, 360 + 120);
    }

    [Fact]
    public void ClockGoingBack_ResetsSchedule()
    {
        var s = new SupportReminderScheduler(Settings());
        Run(s, Position.HardSupport, 0, 1000);
        var again = Run(s, Position.HardSupport, 0, 70);
        Assert.Contains(again, x => x.R.Kind == ReminderKind.ObserverWard && x.T == 60);
    }

    [Fact]
    public void PausedGame_DoesNotShow()
    {
        var s = new SupportReminderScheduler(Settings());
        for (int t = 0; t < 200; t++) Assert.Null(s.Tick(TimeSpan.FromSeconds(t), Position.HardSupport, false));
    }
}
