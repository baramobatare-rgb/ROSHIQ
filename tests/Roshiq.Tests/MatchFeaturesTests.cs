using Roshiq.Core.Models;
using Roshiq.Core.Settings;
using Roshiq.Data;
using Roshiq.Draft;
using Roshiq.GSI;
using Roshiq.Recommendations;
using Xunit;

namespace Roshiq.Tests;

public class TimerTests
{
    private static readonly TimerRule Power = new("power_rune", "Руна силы", "Power rune", 360, 120, null, 20);
    private static readonly TimerRule Water = new("water_rune", "Водная руна", "Water rune", 120, 120, 240, 15);
    private static readonly TimerRule Tormentor = new("tormentor", "Терзатель", "Tormentor", 900, 0, null, 30);

    [Theory]
    [InlineData(-60, 360)]
    [InlineData(0, 360)]
    [InlineData(359, 360)]
    [InlineData(360, 480)]
    [InlineData(479, 480)]
    [InlineData(1000, 1080)]
    public void PowerRune_NextOccurrence(int clock, int expected) =>
        Assert.Equal(expected, GameTimers.NextOccurrence(Power, clock));

    [Fact]
    public void LastSpawn_IsRespected()
    {
        Assert.Equal(240, GameTimers.NextOccurrence(Water, 150));
        Assert.Null(GameTimers.NextOccurrence(Water, 240));
    }

    [Fact]
    public void OneShotEvent_DisappearsAfterIt()
    {
        Assert.Equal(900, GameTimers.NextOccurrence(Tormentor, 100));
        Assert.Null(GameTimers.NextOccurrence(Tormentor, 900));
    }

    [Fact]
    public void Upcoming_SortedAndFiltered()
    {
        var list = GameTimers.Upcoming(new[] { Power, Water, Tormentor }, 100, k => k != "water_rune");
        Assert.Equal(new[] { "power_rune", "tormentor" }, list.Select(e => e.Rule.Key));
        Assert.Equal(260, list[0].InSec);
    }

    [Fact]
    public void Announcer_OncePerSpawn_AndResetsWhenClockGoesBack()
    {
        var a = new TimerAnnouncer();
        Assert.Empty(a.Due(new[] { Power }, 330, _ => true));        // 30 с до — рано (lead 20)
        Assert.Single(a.Due(new[] { Power }, 345, _ => true));       // 15 с до — объявить
        Assert.Empty(a.Due(new[] { Power }, 350, _ => true));        // уже объявлено
        Assert.Single(a.Due(new[] { Power }, 465, _ => true));       // следующая руна
        Assert.Single(a.Due(new[] { Power }, 345, _ => true));       // часы ушли назад — новый матч
    }

    [Fact]
    public void Clock_Formats()
    {
        Assert.Equal("1:05", GameTimers.Clock(65));
        Assert.Equal("-0:30", GameTimers.Clock(-30));
        Assert.Equal("0:00", GameTimers.Clock(0));
    }

    [Fact]
    public void Database_LoadsTimers()
    {
        var keys = TestData.Db.Timers.Select(t => t.Key).ToList();
        Assert.Contains("power_rune", keys);
        Assert.Contains("bounty_rune", keys);
        Assert.Contains("wisdom_rune", keys);
        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.DoesNotContain(TestData.Db.Warnings, w => w.StartsWith("timers.txt"));
    }
}

public class RoshanTests
{
    [Fact]
    public void Timeline_AfterKill()
    {
        var r = new RoshanTracker();
        Assert.Equal(RoshanPhase.None, r.State(1000).Phase);
        r.MarkKilled(1200);
        var st = r.State(1300);
        Assert.Equal(RoshanPhase.Dead, st.Phase);
        Assert.Equal(200, st.AegisLeftSec);
        Assert.Equal(1680, st.RespawnMinAt);
        Assert.Equal(1860, st.RespawnMaxAt);
        Assert.Equal(0, r.State(1500).AegisLeftSec);
        Assert.Equal(RoshanPhase.MaybeAlive, r.State(1700).Phase);
    }

    [Fact]
    public void Announcements_FireOnce()
    {
        var r = new RoshanTracker();
        r.MarkKilled(0);
        Assert.Equal(new[] { "rosh_aegis_soon" }, r.Due(275));
        Assert.Empty(r.Due(280));
        Assert.Equal(new[] { "rosh_respawn_min" }, r.Due(480));
        Assert.Equal(new[] { "rosh_respawn_max" }, r.Due(661));
    }

    [Fact]
    public void ClockGoingBack_ClearsMark()
    {
        var r = new RoshanTracker();
        r.MarkKilled(2000);
        Assert.Equal(RoshanPhase.None, r.State(100).Phase);
        Assert.Null(r.KilledAt);
    }
}

public class AlertTests
{
    private static InventoryState State(int clock, int gold = 500, bool tp = true, string? stash = null, Position pos = Position.Carry, int? unreliable = null)
    {
        var slots = new Dictionary<string, string> { ["slot0"] = "item_blink", ["teleport0"] = tp ? "item_tpscroll" : "empty" };
        if (stash != null) slots["stash0"] = stash;
        return new InventoryState(clock, true, gold, unreliable, slots, pos);
    }

    [Fact]
    public void NoTeleport_NeedsSustainAndCooldown()
    {
        var a = new MatchAlerts();
        var s = new AlertSettings();
        Assert.Null(a.Tick(State(300, tp: false), s));
        Assert.Null(a.Tick(State(310, tp: false), s));
        Assert.Equal(MatchAlerts.NoTeleport, a.Tick(State(321, tp: false), s));
        Assert.Null(a.Tick(State(330, tp: false), s)); // кулдаун
        Assert.Equal(1, a.Counts[MatchAlerts.NoTeleport]);
    }

    [Fact]
    public void NoTeleport_NotBeforeTwoMinutes_OrWithoutGold()
    {
        Assert.DoesNotContain(MatchAlerts.NoTeleport, MatchAlerts.Conditions(State(60, tp: false), new AlertSettings()));
        Assert.DoesNotContain(MatchAlerts.NoTeleport, MatchAlerts.Conditions(State(600, gold: 40, tp: false), new AlertSettings()));
        Assert.Contains(MatchAlerts.NoTeleport, MatchAlerts.Conditions(State(600, tp: false), new AlertSettings()));
    }

    [Fact]
    public void Stash_WardsForSupports()
    {
        var s = new AlertSettings();
        Assert.Contains(MatchAlerts.WardsInStash, MatchAlerts.Conditions(State(400, stash: "item_ward_observer", pos: Position.HardSupport), s));
        Assert.Contains(MatchAlerts.ItemsInStash, MatchAlerts.Conditions(State(400, stash: "item_ward_observer", pos: Position.Carry), s));
        Assert.DoesNotContain(MatchAlerts.ItemsInStash, MatchAlerts.Conditions(State(400, stash: "empty"), s));
    }

    [Fact]
    public void UnspentGold_UsesUnreliableWhenKnown()
    {
        var s = new AlertSettings { UnspentGoldThreshold = 2000 };
        Assert.DoesNotContain(MatchAlerts.UnspentGold, MatchAlerts.Conditions(State(900, gold: 3000, unreliable: 800), s));
        Assert.Contains(MatchAlerts.UnspentGold, MatchAlerts.Conditions(State(900, gold: 3000, unreliable: 2500), s));
    }

    [Fact]
    public void Disabled_ReturnsNothing()
    {
        Assert.Empty(MatchAlerts.Conditions(State(900, gold: 9000, tp: false, stash: "item_tango"), new AlertSettings { Enabled = false }));
    }
}

public class MatchHistoryTests
{
    private static MatchTick Tick(string state, int clock, string? win = null, params (string Slot, string Item)[] items) =>
        new("7000000001", state, clock, "radiant", win, 2, "axe", 10, 3, 2, 7, 120, 5, 520, 610, 900,
            items.ToDictionary(x => x.Slot, x => x.Item), Position.Offlane, DateTime.UtcNow);

    [Fact]
    public void Recorder_RecordsTimingsSamplesAndResult()
    {
        var r = new MatchRecorder();
        Assert.Null(r.Observe(Tick("DOTA_GAMERULES_STATE_GAME_IN_PROGRESS", -60, null, ("slot0", "item_tango"))));
        Assert.Null(r.Observe(Tick("DOTA_GAMERULES_STATE_GAME_IN_PROGRESS", 600, null, ("slot0", "item_phase_boots"))));
        Assert.Null(r.Observe(Tick("DOTA_GAMERULES_STATE_GAME_IN_PROGRESS", 640, null, ("slot0", "item_phase_boots"), ("stash0", "item_blink"))));
        var done = r.Observe(Tick("DOTA_GAMERULES_STATE_POST_GAME", 2100, "radiant"));
        Assert.NotNull(done);
        Assert.Equal(MatchResult.Win, done!.Result);
        Assert.Equal(600, done.ItemTimings["item_phase_boots"]);
        Assert.Equal(640, done.ItemTimings["item_blink"]);
        Assert.False(done.ItemTimings.ContainsKey("item_tango"));
        Assert.Equal(2, done.Samples.Count); // 600 и 640 (шаг 30 с)
        Assert.Equal(2100, done.DurationSec);
        // после итога этот матч больше не записывается
        Assert.Null(r.Observe(Tick("DOTA_GAMERULES_STATE_POST_GAME", 2110, "radiant")));
    }

    [Fact]
    public void Recorder_IgnoresDemoMatches()
    {
        var r = new MatchRecorder();
        var t = Tick("DOTA_GAMERULES_STATE_GAME_IN_PROGRESS", 600) with { MatchId = "0" };
        Assert.Null(r.Observe(t));
        Assert.Null(r.Current);
    }

    [Fact]
    public void Benchmark_HeroAndRole_ThenRole_MinThree()
    {
        MatchRecord R(string id, int hero, Position? pos, int at) =>
            new() { Id = id, HeroId = hero, Position = pos, ItemTimings = { ["item_blink"] = at } };
        var a = R("a", 2, Position.Offlane, 800);
        var b = R("b", 2, Position.Offlane, 1000);
        var c = R("c", 2, Position.Offlane, 900);
        var d = R("d", 5, Position.Offlane, 400);
        var heroOtherRole = R("e", 2, Position.SoftSupport, 300);   // тот же герой, другая роль — не в выборке
        var unknownRole = R("f", 2, null, 100);                      // неизвестная роль — не в выборке
        var current = R("x", 2, Position.Offlane, 950);

        var bench = MatchStats.Benchmark(new[] { a, b, c, d, heroOtherRole, unknownRole, current }, current, "item_blink");
        Assert.NotNull(bench);
        Assert.True(bench!.SameHero);
        Assert.Equal(900, bench.AvgSec);
        Assert.Equal(3, bench.Matches);

        // на герое в роли меньше трёх — та же роль
        var roleBench = MatchStats.Benchmark(new[] { a, b, d }, current, "item_blink");
        Assert.False(roleBench!.SameHero);
        Assert.Equal(3, roleBench.Matches);

        // меньше трёх и в роли — сравнения нет; неизвестная роль текущего матча — сравнения нет
        Assert.Null(MatchStats.Benchmark(new[] { a, b }, current, "item_blink"));
        Assert.Null(MatchStats.Benchmark(new[] { a, b, c }, R("y", 2, null, 900), "item_blink"));
    }

    [Fact]
    public void Benchmark_SkipsApproximateTimings()
    {
        MatchRecord R(string id, int at, bool approx = false)
        {
            var m = new MatchRecord { Id = id, HeroId = 2, Position = Position.Offlane, ItemTimings = { ["item_blink"] = at } };
            if (approx) m.ApproxItems.Add("item_blink");
            return m;
        }
        var current = R("x", 900);
        Assert.Null(MatchStats.Benchmark(new[] { R("a", 800), R("b", 1000), R("c", 2000, approx: true) }, current, "item_blink"));
        Assert.Equal(3, MatchStats.Benchmark(new[] { R("a", 800), R("b", 1000), R("c", 900), R("d", 2000, approx: true) }, current, "item_blink")!.Matches);
    }

    [Fact]
    public void Stats_WinRateOnlyFromKnownResults()
    {
        var list = new[]
        {
            new MatchRecord { Id = "1", HeroId = 2, Result = MatchResult.Win, Gpm = 500 },
            new MatchRecord { Id = "2", HeroId = 2, Result = MatchResult.Loss, Gpm = 400 },
            new MatchRecord { Id = "3", HeroId = 2, Result = MatchResult.Unknown, Gpm = 600 },
        };
        var (_, g) = MatchStats.ByHero(list).Single();
        Assert.Equal(3, g.Games);
        Assert.Equal(0.5, g.WinRate);
        Assert.Equal(500, g.AvgGpm);
    }

    [Fact]
    public void Store_ImportDoesNotDuplicateGsiMatches()
    {
        var dir = Path.Combine(Path.GetTempPath(), "roshiq-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new MatchHistoryStore(dir);
            store.Save(new MatchRecord { Id = "gsi-111", MatchId = "111", HeroId = 2, Result = MatchResult.Unknown, StartedUtc = DateTime.UtcNow });
            int added = store.Import(new[]
            {
                new RecentMatchRow { MatchId = 111, PlayerSlot = 1, RadiantWin = true, HeroId = 2, Duration = 2000 },
                new RecentMatchRow { MatchId = 222, PlayerSlot = 130, RadiantWin = true, HeroId = 5, Duration = 1800, StartTime = 1730000000 },
            }).Added;
            Assert.Equal(1, added);
            var all = new MatchHistoryStore(dir).All(); // перечитать с диска
            Assert.Equal(2, all.Count);
            Assert.Equal(MatchResult.Win, all.Single(m => m.MatchId == "111").Result);
            Assert.Equal(MatchResult.Loss, all.Single(m => m.MatchId == "222").Result);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}

public class OnlineDataTests
{
    [Theory]
    [InlineData("7.42", "7.41d (пояснение)", true)]
    [InlineData("7.41", "7.41d", false)]
    [InlineData("7.40", "7.41d", false)]
    [InlineData("8.00", "7.41", true)]
    [InlineData(null, "7.41", false)]
    public void PatchCompare(string? latest, string baseline, bool newer) =>
        Assert.Equal(newer, PatchVersion.IsNewer(latest, baseline));

    [Fact]
    public void PatchShort() => Assert.Equal("7.41", PatchVersion.Short("7.41d (сверено)"));

    [Theory]
    [InlineData("123456789", 123456789L)]
    [InlineData("76561198083722517", 123456789L)]
    [InlineData("  76561198083722517 ", 123456789L)]
    [InlineData("abc", null)]
    [InlineData("", null)]
    public void SteamId(string text, long? expected) => Assert.Equal(expected, SteamIds.ToAccountId(text));

    [Fact]
    public void ItemCosts_PersistAndIgnoreZero()
    {
        var path = Path.Combine(Path.GetTempPath(), "roshiq-costs-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var c = new ItemCostCache(path);
            Assert.Null(c.Cost("item_blink"));
            c.Replace(new Dictionary<string, int> { ["item_blink"] = 2250, ["item_ward_observer"] = 0 }, DateTime.UtcNow);
            var again = new ItemCostCache(path);
            Assert.Equal(2250, again.Cost("ITEM_BLINK"));
            Assert.Null(again.Cost("item_ward_observer"));
        }
        finally { try { File.Delete(path); } catch { } }
    }
}

public class PoolAndTrainerTests
{
    private static DraftState Sample() => new(Array.Empty<Hero>(),
        new[] { TestData.Hero("phantom_assassin"), TestData.Hero("riki") }, Position.HardSupport);

    [Fact]
    public void PoolOnly_LimitsCandidates()
    {
        var a = new DraftAnalyzer(TestData.Db);
        var pool = TestData.Db.Heroes.Where(h => h.PlaysPosition(Position.HardSupport)).Take(3).Select(h => h.Id).ToHashSet();
        a.Pool = pool;
        a.PoolMode = PoolMode.Only;
        var r = a.Analyze(Sample(), top: 10, alternatives: 10);
        Assert.All(r.Top.Concat(r.Alternatives), x => Assert.True(pool.Contains(x.Hero.Id)));
    }

    [Fact]
    public void PoolPrefer_BoostsPoolHero()
    {
        var plain = new DraftAnalyzer(TestData.Db);
        var hero = TestData.Db.Heroes.First(h => h.PlaysPosition(Position.HardSupport));
        var before = plain.Evaluate(hero, Sample()).Score;
        var pref = new DraftAnalyzer(TestData.Db) { Pool = new HashSet<int> { hero.Id }, PoolMode = PoolMode.Prefer };
        Assert.True(pref.Evaluate(hero, Sample()).Score > before);
    }

    [Fact]
    public void Trainer_ScenarioIsValid_AndTopPickIsExcellent()
    {
        var trainer = new DraftTrainer(TestData.Db, new DraftAnalyzer(TestData.Db));
        var rnd = new Random(42);
        for (int i = 0; i < 20; i++)
        {
            var s = trainer.NewScenario(rnd, Position.Mid);
            Assert.InRange(s.Enemies.Count, 3, 5);
            Assert.InRange(s.Allies.Count, 0, 3);
            var ids = s.Enemies.Concat(s.Allies).Select(h => h.Id).ToList();
            Assert.Equal(ids.Count, ids.Distinct().Count());

            var best = new DraftAnalyzer(TestData.Db).Analyze(s, top: 1).Top.FirstOrDefault();
            if (best == null) continue;
            var grade = trainer.Grade(s, best.Hero);
            Assert.Equal(1, grade.Rank);
            Assert.Equal(TrainerVerdict.Excellent, grade.Verdict);
        }
    }

    [Fact]
    public void Trainer_OffRolePick()
    {
        var trainer = new DraftTrainer(TestData.Db, new DraftAnalyzer(TestData.Db));
        var s = Sample();
        var offRole = TestData.Db.Heroes.First(h => !h.PlaysPosition(Position.HardSupport) && s.Enemies.All(e => e.Id != h.Id));
        Assert.Equal(TrainerVerdict.OffRole, trainer.Grade(s, offRole).Verdict);
    }
}

public class SettingsAndGsiExtraTests
{
    [Fact]
    public void Normalize_FixesProfilesAndNotes()
    {
        var s = new AppSettings
        {
            OverlayProfiles = new List<OverlayProfile>(),
            ActiveOverlayProfile = "missing",
            HeroNotes = new Dictionary<string, string> { ["Axe"] = "  BKB  ", [""] = "x", ["riki"] = " " },
            Alerts = new AlertSettings { UnspentGoldThreshold = 10 }
        }.Normalize();
        Assert.Equal(2, s.OverlayProfiles.Count);
        Assert.Null(s.ActiveOverlayProfile);
        Assert.Equal("BKB", s.HeroNotes["axe"]);
        Assert.Single(s.HeroNotes);
        Assert.Equal(500, s.Alerts.UnspentGoldThreshold);
        Assert.Equal("Ctrl+Shift+K", s.Overlay.RoshanHotkey);
    }

    [Fact]
    public void Profile_ApplyAndCopy()
    {
        var o = new OverlaySettings();
        var p = OverlayProfile.Defaults().Single(x => x.Id == "stream");
        p.ApplyTo(o);
        Assert.Equal(0.9, o.Scale);
        var copy = new OverlayProfile { Id = "c" };
        copy.CopyFrom(o);
        Assert.Equal(o.Opacity, copy.Opacity);
    }

    [Fact]
    public void Gsi_ParsesPlayerStatsAndWinTeam()
    {
        const string json = """
        { "map": { "matchid": "1", "game_state": "DOTA_GAMERULES_STATE_POST_GAME", "win_team": "Dire" },
          "player": { "kills": 4, "deaths": 2, "assists": 9, "last_hits": 140, "denies": 7, "gpm": 512, "xpm": 640,
                      "gold": 900, "gold_unreliable": 600, "team_name": "dire" } }
        """;
        var s = GsiParser.Parse(json, null);
        Assert.Equal("dire", s.WinTeam);
        Assert.Equal(4, s.Kills);
        Assert.Equal(140, s.LastHits);
        Assert.Equal(512, s.Gpm);
        Assert.Equal(640, s.Xpm);
        Assert.Equal(600, s.GoldUnreliable);
    }
}
