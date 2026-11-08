using Roshiq.Core.Models;
using Roshiq.Core.Settings;
using Roshiq.Recommendations;
using Xunit;

namespace Roshiq.Tests;

/// <summary>0.17: единый диспетчер советов и честный учёт исходов подсказок.</summary>
public class TipDispatcherTests
{
    private static readonly DateTime T0 = new(2026, 11, 7, 18, 0, 0, DateTimeKind.Utc);
    private static readonly TipContext Game = new(Blocked: false, InGame: true, Paused: false, Stale: false, Dead: false);

    private static Tip TipOf(string kind, TipPriority p = TipPriority.Normal, bool inGame = true, int ttl = 30, Func<bool>? valid = null, string? key = null) =>
        new() { Kind = kind, Key = key, Header = "H", Text = kind, Priority = p, InGameOnly = inGame, Ttl = TimeSpan.FromSeconds(ttl), StillValid = valid };

    [Fact]
    public void UrgentFirstAndGapsRespected()
    {
        var d = new TipDispatcher();
        d.Enqueue(TipOf("lane"), T0);
        d.Enqueue(TipOf("no_tp", TipPriority.Urgent), T0.AddSeconds(1));
        var first = d.Next(Game, T0.AddSeconds(1));
        Assert.Equal("no_tp", first!.Kind);
        d.Report(first, true, T0.AddSeconds(1));
        Assert.Null(d.Next(Game, T0.AddSeconds(5)));                 // обычный — не раньше чем через 12 с
        Assert.Equal("lane", d.Next(Game, T0.AddSeconds(14))!.Kind);
    }

    [Fact]
    public void BlockedOrPausedWaitsInsteadOfLosing()
    {
        var d = new TipDispatcher();
        d.Enqueue(TipOf("pace", ttl: 60), T0);
        Assert.Null(d.Next(Game with { Blocked = true }, T0.AddSeconds(1)));    // висит напоминание
        Assert.Null(d.Next(Game with { Paused = true }, T0.AddSeconds(2)));
        Assert.Null(d.Next(Game with { Stale = true }, T0.AddSeconds(3)));
        Assert.Equal("pace", d.Next(Game, T0.AddSeconds(4))!.Kind);             // дождался, а не пропал
    }

    [Fact]
    public void ExpiredAndInvalidAreRemovedWithOutcome()
    {
        var d = new TipDispatcher();
        var outcomes = new List<(string, TipOutcome)>();
        d.Outcome += (t, o) => outcomes.Add((t.Kind, o));
        bool tpMissing = true;
        d.Enqueue(TipOf("no_tp", valid: () => tpMissing), T0);
        d.Enqueue(TipOf("spike", ttl: 10), T0);
        tpMissing = false;                                                       // купил телепорт, пока ждал
        Assert.Null(d.Next(Game with { Blocked = true }, T0.AddSeconds(11)));
        Assert.Contains(("no_tp", TipOutcome.Invalidated), outcomes);
        Assert.Contains(("spike", TipOutcome.Expired), outcomes);
        Assert.Equal(0, d.Pending);
    }

    [Fact]
    public void SameKeyMergesKeepingHigherPriority()
    {
        var d = new TipDispatcher();
        var outcomes = new List<TipOutcome>();
        d.Outcome += (_, o) => outcomes.Add(o);
        d.Enqueue(TipOf("pace", TipPriority.Urgent, key: "pace_lh"), T0);
        d.Enqueue(TipOf("pace2", TipPriority.Normal, key: "pace_lh"), T0.AddSeconds(1));
        Assert.Equal(1, d.Pending);
        Assert.Equal(TipOutcome.Merged, Assert.Single(outcomes));
        var t = d.Next(Game, T0.AddSeconds(2))!;
        Assert.Equal("pace2", t.Kind);                                           // свежий текст
        Assert.Equal(TipPriority.Urgent, t.Priority);                           // срочность сохранилась
    }

    [Fact]
    public void ServiceWaitsForEndOfMatchAndDeadHeroSilencesGameTips()
    {
        var d = new TipDispatcher();
        d.Enqueue(TipOf("data_updated", TipPriority.Background, inGame: false, ttl: 900), T0);
        d.Enqueue(TipOf("stash", ttl: 60), T0);
        Assert.Null(d.Next(Game with { Dead = true }, T0.AddSeconds(30)));      // мёртв: игровой молчит, служебный ждёт конца матча
        Assert.Equal("stash", d.Next(Game, T0.AddSeconds(31))!.Kind);
        var after = Game with { InGame = false, Stale = true };
        Assert.Equal("data_updated", d.Next(after, T0.AddSeconds(60))!.Kind);
    }

    [Fact]
    public void DroppedIsReportedNotShown()
    {
        var d = new TipDispatcher();
        TipOutcome? got = null;
        d.Outcome += (_, o) => got = o;
        d.Enqueue(TipOf("x"), T0);
        var t = d.Next(Game, T0)!;
        d.Report(t, shown: false, T0);
        Assert.Equal(TipOutcome.Dropped, got);
        d.Enqueue(TipOf("y"), T0.AddSeconds(1));
        Assert.NotNull(d.Next(Game, T0.AddSeconds(1)));                         // неудачный показ паузу не включает
    }

    // ------------------------------------------------------------------ исходы подсказок инвентаря

    private static readonly Dictionary<string, string> NoTp = new() { ["slot0"] = "item_blink", ["teleport0"] = "empty" };
    private static readonly Dictionary<string, string> WithTp = new() { ["slot0"] = "item_blink", ["teleport0"] = "item_tpscroll" };

    [Fact]
    public void Alerts_RetractedHintIsNotCountedAsIgnored()
    {
        var a = new MatchAlerts();
        var s = new AlertSettings();
        for (int c = 300; c <= 319; c++) a.Tick(new InventoryState(c, true, 500, null, NoTp, Position.Carry), s);
        Assert.Equal(MatchAlerts.NoTeleport, a.Tick(new InventoryState(320, true, 500, null, NoTp, Position.Carry), s));
        a.Retract(MatchAlerts.NoTeleport);                                       // на экран не попала
        Assert.False(a.Counts.ContainsKey(MatchAlerts.NoTeleport));
        // снова можно предложить сразу, без кулдауна, и минуты «игнора» не будет
        Assert.Equal(MatchAlerts.NoTeleport, a.Tick(new InventoryState(321, true, 500, null, NoTp, Position.Carry), s));
        Assert.Equal(1, a.Counts[MatchAlerts.NoTeleport]);
    }

    [Fact]
    public void Alerts_FollowedWhenConditionClearsAndDeathIsNeutral()
    {
        var a = new MatchAlerts();
        var s = new AlertSettings();
        for (int c = 300; c <= 339; c++) a.Tick(new InventoryState(c, true, 500, null, NoTp, Position.Carry), s);   // показ на 320
        a.Tick(new InventoryState(340, true, 400, null, WithTp, Position.Carry), s);   // купил телепорт
        Assert.Equal(1, a.Followed[MatchAlerts.NoTeleport]);
        Assert.False(a.Ignored.ContainsKey(MatchAlerts.NoTeleport));

        var b = new MatchAlerts();
        for (int c = 300; c <= 329; c++) b.Tick(new InventoryState(c, true, 500, null, NoTp, Position.Carry), s);
        for (int c = 330; c < 400; c++) b.Tick(new InventoryState(c, false, 500, null, NoTp, Position.Carry), s);    // умер
        b.Tick(new InventoryState(400, true, 500, null, NoTp, Position.Carry), s);
        Assert.False(b.Followed.ContainsKey(MatchAlerts.NoTeleport));
        Assert.False(b.Ignored.ContainsKey(MatchAlerts.NoTeleport));
    }

    [Fact]
    public void Alerts_SavingForNextItemIsNotUnspentGold()
    {
        var s = new AlertSettings();
        var slots = new Dictionary<string, string> { ["slot0"] = "item_blink", ["teleport0"] = "item_tpscroll" };
        Assert.Contains(MatchAlerts.UnspentGold, MatchAlerts.Conditions(new InventoryState(900, true, 3000, 2500, slots, Position.Carry), s));
        Assert.DoesNotContain(MatchAlerts.UnspentGold,
            MatchAlerts.Conditions(new InventoryState(900, true, 3000, 2500, slots, Position.Carry, SavingFor: 4050), s));   // копит на BKB
        Assert.Contains(MatchAlerts.UnspentGold,
            MatchAlerts.Conditions(new InventoryState(900, true, 5000, 2500, slots, Position.Carry, SavingFor: 4050), s));   // уже хватает
    }
}
