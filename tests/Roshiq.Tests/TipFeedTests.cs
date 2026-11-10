using Roshiq.Recommendations;
using Xunit;

namespace Roshiq.Tests;

/// <summary>0.19.1: лента подсказок матча на «Обзоре» — исходы приходят позже показа и не путаются между видами.</summary>
public class TipFeedTests
{
    [Fact]
    public void OutcomeArrivesLaterAndMarksTheEarliestShownTip()
    {
        var feed = new TipFeed();
        feed.Add(252, MatchAlerts.NoTeleport, "Нет свитка телепорта", TipFeedStatus.Shown, MatchAlerts.NoTeleport);
        feed.Add(600, "pace_lh", "Отстаёшь на 8 добиваний", TipFeedStatus.Saved);
        feed.Add(760, MatchAlerts.UnspentGold, "2140 золота", TipFeedStatus.Shown, MatchAlerts.UnspentGold);
        feed.Add(900, MatchAlerts.NoTeleport, "Нет свитка телепорта", TipFeedStatus.Shown, MatchAlerts.NoTeleport);
        int before = feed.Revision;

        feed.Sync(new Dictionary<string, int> { [MatchAlerts.NoTeleport] = 1 }, new Dictionary<string, int> { [MatchAlerts.UnspentGold] = 1 });

        Assert.Equal(TipFeedStatus.Helped, feed.Items[0].Status);
        Assert.Equal(TipFeedStatus.Saved, feed.Items[1].Status);
        Assert.Equal(TipFeedStatus.Missed, feed.Items[2].Status);
        Assert.Equal(TipFeedStatus.Shown, feed.Items[3].Status);   // второй показ ещё не оценён
        Assert.True(feed.Revision > before);

        int stable = feed.Revision;
        feed.Sync(new Dictionary<string, int> { [MatchAlerts.NoTeleport] = 1 }, new Dictionary<string, int> { [MatchAlerts.UnspentGold] = 1 });
        Assert.Equal(stable, feed.Revision);                        // те же счётчики — ничего не меняется
    }

    [Fact]
    public void CountsAndRecentWindow()
    {
        var feed = new TipFeed();
        for (int i = 0; i < 7; i++) feed.Add(i * 60, "spike", "T" + i, i % 3 == 0 ? TipFeedStatus.Saved : TipFeedStatus.Shown);
        Assert.Equal(3, feed.SavedCount);
        Assert.Equal(4, feed.ShownCount);
        Assert.Equal(new[] { "T4", "T5", "T6" }, feed.Recent(3).Select(e => e.Text).ToArray());
        Assert.Equal(7, feed.Recent(50).Count);
    }

    [Fact]
    public void BoundedAndIgnoresEmptyText()
    {
        var feed = new TipFeed();
        feed.Add(10, "lane", "  ", TipFeedStatus.Shown);
        Assert.Empty(feed.Items);
        for (int i = 0; i < TipFeed.Max + 15; i++) feed.Add(i, "spike", "T" + i, TipFeedStatus.Shown);
        Assert.Equal(TipFeed.Max, feed.Items.Count);
        Assert.Equal("T15", feed.Items[0].Text);
        feed.Clear();
        Assert.Empty(feed.Items);
    }

    [Fact]
    public void OutcomeWithoutAShownTipIsIgnored()
    {
        var feed = new TipFeed();
        feed.Add(600, "pace_lh", "saved", TipFeedStatus.Saved);
        feed.Sync(new Dictionary<string, int> { [MatchAlerts.NoTeleport] = 2 }, new Dictionary<string, int>());
        Assert.Equal(TipFeedStatus.Saved, feed.Items[0].Status);
    }
}
