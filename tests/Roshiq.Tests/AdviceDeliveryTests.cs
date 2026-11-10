using Roshiq.Core.Settings;
using Roshiq.Recommendations;
using Xunit;

namespace Roshiq.Tests;

public class AdviceDeliveryTests
{
    [Fact]
    public void OptionalCategoriesCanBeSavedForRecapWhileUrgentAlertsStayLive()
    {
        var settings = new AdviceDeliverySettings { StrategyDuringMatch = false, PaceDuringMatch = false, ItemsDuringMatch = false };
        Assert.False(settings.ShowDuringMatch("lane"));
        Assert.False(settings.ShowDuringMatch("spike"));
        Assert.False(settings.ShowDuringMatch("pace_lh"));
        Assert.False(settings.ShowDuringMatch("pace_item"));
        Assert.False(settings.ShowDuringMatch("spike_item"));
        Assert.True(settings.ShowDuringMatch("alert_no_tp"));
        Assert.True(settings.ShowDuringMatch("timer"));
    }

    [Fact]
    public void SavedTipAloneKeepsPostMatchRecapAvailable()
    {
        var match = new MatchRecord();
        match.DeferredTips.Add(new DeferredMatchTip { Kind = "pace_lh", Text = "Review pace", Clock = 600 });
        var summary = new PostMatchSummary(match, Array.Empty<Insight>(), null, 0);
        Assert.True(summary.HasContent);
    }

    [Fact]
    public void DeferredListHasNoDuplicatesAndIsBounded()
    {
        var list = new List<DeferredMatchTip>();
        Assert.True(DeferredTipList.Add(list, new DeferredMatchTip { Kind = "lane", Text = "A", Clock = 0 }));
        Assert.False(DeferredTipList.Add(list, new DeferredMatchTip { Kind = "lane", Text = "A", Clock = 30 }));   // тот же совет
        Assert.False(DeferredTipList.Add(list, new DeferredMatchTip { Kind = "lane", Text = " ", Clock = 30 }));   // пустой
        for (int i = 0; i < 40; i++) DeferredTipList.Add(list, new DeferredMatchTip { Kind = "spike", Text = "T" + i, Clock = i });
        Assert.Equal(DeferredTipList.Max, list.Count);
    }

    [Fact]
    public void FeedbackNamesWhatHelpedAndWhatWasMissed()
    {
        var m = new MatchRecord();
        m.AlertsFollowed[MatchAlerts.NoTeleport] = 3;
        m.AlertsFollowed[MatchAlerts.ItemsInStash] = 1;
        m.AlertsIgnored[MatchAlerts.NoBuyback] = 2;
        m.AlertsIgnored[MatchAlerts.UnspentGold] = 0;
        var fb = PostMatch.Feedback(m);
        Assert.Equal(MatchAlerts.NoTeleport, fb.HelpedKey);
        Assert.Equal(3, fb.HelpedCount);
        Assert.Equal(MatchAlerts.NoBuyback, fb.MissedKey);
        Assert.Equal(2, fb.MissedCount);
        Assert.True(fb.HasFeedback);
    }

    [Fact]
    public void FeedbackIsEmptyForAMatchWithoutTips()
    {
        var fb = PostMatch.Feedback(new MatchRecord());
        Assert.False(fb.Any);
        Assert.Null(fb.HelpedKey);
        Assert.Null(fb.MissedKey);
    }

    [Fact]
    public void DeferredTipsAreOrderedByClockAndTheRestIsCounted()
    {
        var m = new MatchRecord();
        m.DeferredTips.Add(new DeferredMatchTip { Kind = "pace_lh", Text = "late", Clock = 900 });
        m.DeferredTips.Add(new DeferredMatchTip { Kind = "lane", Text = "early", Clock = 5 });
        m.DeferredTips.Add(new DeferredMatchTip { Kind = "spike", Text = "mid", Clock = 600 });
        var fb = PostMatch.Feedback(m, maxDeferred: 2);
        Assert.Equal(new[] { "early", "mid" }, fb.Deferred.Select(t => t.Text).ToArray());
        Assert.Equal(1, fb.DeferredMore);
        Assert.Equal("10:00 · mid", PostMatch.DeferredLine(fb.Deferred[1]));
        Assert.Equal(3, PostMatch.Feedback(m).Deferred.Count);
    }

    [Fact]
    public void WithoutAMainMistakeTheGoalComesFromTheMissedTip()
    {
        var m = new MatchRecord();
        m.AlertsIgnored[MatchAlerts.NoTeleport] = 2;
        var goal = PostMatch.NextGoal(new PostMatchSummary(m, Array.Empty<Insight>(), null, 0));
        Assert.Contains("телепорт", goal.Ru);
        // нет ни ошибки, ни пропущенных подсказок — общая цель
        var plain = PostMatch.NextGoal(new PostMatchSummary(new MatchRecord(), Array.Empty<Insight>(), null, 0));
        Assert.Contains("одну цель", plain.Ru);
    }

    [Fact]
    public void SavedSettingsSurviveARoundTrip()
    {
        var s = new AppSettings();
        s.AdviceDelivery.PaceDuringMatch = false;
        var json = System.Text.Json.JsonSerializer.Serialize(s);
        var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json)!;
        back.Normalize();
        Assert.False(back.AdviceDelivery.PaceDuringMatch);
        Assert.True(back.AdviceDelivery.StrategyDuringMatch);
        // старый файл настроек без раздела — всё показывается в матче, как раньше
        var old = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"AdviceDelivery\":null}")!;
        old.Normalize();
        Assert.True(old.AdviceDelivery.ShowDuringMatch("pace_lh"));
    }
}
