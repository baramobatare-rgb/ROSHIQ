using Roshiq.Core.Models;
using Roshiq.Recommendations;
using Xunit;

namespace Roshiq.Tests;

/// <summary>0.17.5: сохранность истории, покрытие матча, восстановление после перезапуска, единые фазы.</summary>
public class HistoryIntegrityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "roshiq-hist-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static MatchRecord Rec(string id, MatchResult result = MatchResult.Win, int kills = 5) => new()
    {
        Id = id, MatchId = id, Source = "GSI", StartedUtc = DateTime.UtcNow, HeroId = 2, DurationSec = 1800, Result = result, Kills = kills
    };

    // ------------------------------------------------------------------ сохранение

    [Fact]
    public void Save_Success_PersistsAndReloads()
    {
        var store = new MatchHistoryStore(_dir);
        Assert.True(store.Save(Rec("a")));
        Assert.Single(new MatchHistoryStore(_dir).All());
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Save_Failure_ReturnsFalse_AndCacheUnchanged()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "a.json"));     // путь занят каталогом — записать файл нельзя
        var store = new MatchHistoryStore(_dir);
        Assert.False(store.Save(Rec("a")));
        Assert.Empty(store.All());
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    [Fact]
    public void Save_Failure_KeepsPreviousVersion()
    {
        var store = new MatchHistoryStore(_dir);
        Assert.True(store.Save(Rec("a", MatchResult.Win)));
        bool saved;
        using (new FileStream(Path.Combine(_dir, "a.json"), FileMode.Open, FileAccess.Read, FileShare.None))   // файл занят
            saved = store.Save(Rec("a", MatchResult.Loss));
        Assert.False(saved);
        Assert.Equal(MatchResult.Win, store.All().Single().Result);
        Assert.Equal(MatchResult.Win, new MatchHistoryStore(_dir).All().Single().Result);
    }

    [Fact]
    public void Save_KeepsOwnSnapshot_CallerMutationsDoNotLeak()
    {
        var store = new MatchHistoryStore(_dir);
        var r = Rec("a", kills: 5);
        store.Save(r);
        r.Kills = 99;
        Assert.Equal(5, store.All().Single().Kills);
    }

    [Fact]
    public void Load_IgnoresLeftoverTmp_FromInterruptedWrite()
    {
        new MatchHistoryStore(_dir).Save(Rec("a", MatchResult.Win));
        File.WriteAllText(Path.Combine(_dir, "a.json.tmp"), "{\"Id\":\"a\",\"Resu");   // оборванная запись
        var all = new MatchHistoryStore(_dir).All();
        Assert.Equal(MatchResult.Win, Assert.Single(all).Result);
        Assert.False(File.Exists(Path.Combine(_dir, "a.json.tmp")));
    }

    [Fact]
    public void Load_QuarantinesCorruptFile()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "broken.json"), "{ not json");
        var first = new MatchHistoryStore(_dir);
        first.Save(Rec("ok"));                                        // первое чтение откладывает битый файл
        Assert.Single(first.Quarantined);
        Assert.True(File.Exists(Path.Combine(_dir, "broken.json.bad")));
        Assert.False(File.Exists(Path.Combine(_dir, "broken.json")));
        Assert.Single(new MatchHistoryStore(_dir).All());             // целые записи на месте
    }

    [Fact]
    public void Checkpoint_RoundTrip_AndNotListedAsMatch()
    {
        var store = new MatchHistoryStore(_dir);
        Assert.True(store.SaveCheckpoint(Rec("live")));
        Assert.Empty(new MatchHistoryStore(_dir).All());
        Assert.Equal("live", store.LoadCheckpoint()!.Value.Record.Id);
        store.ClearCheckpoint("other");
        Assert.NotNull(store.LoadCheckpoint());                       // чужой id — не трогаем
        store.ClearCheckpoint("live");
        Assert.Null(store.LoadCheckpoint());
    }

    // ------------------------------------------------------------------ трассы GSI

    private static MatchTick T(int clock, int deaths, string state = "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS", int lh = 0,
        string? item = null, string? win = null) =>
        new("555", state, clock, "radiant", win, 2, "axe", 10, 1, deaths, 2, lh, 1, 450, 500, 900,
            new Dictionary<string, string> { ["slot0"] = item ?? "empty", ["teleport0"] = "item_tpscroll" },
            Position.Offlane, new DateTime(2026, 11, 8, 12, 0, 0, DateTimeKind.Utc).AddSeconds(clock), 500, 1200, 0,
            HeroX: clock, HeroY: -clock);

    /// <summary>Полная трасса: 0…1800 с шагом 5 с, смерти на 300 и 1000, предмет на 900, конец матча.</summary>
    private static List<MatchTick> FullTrace()
    {
        var list = new List<MatchTick>();
        for (int c = 0; c <= 1800; c += 5)
            list.Add(T(c, c >= 1000 ? 2 : c >= 300 ? 1 : 0, lh: c / 10, item: c >= 900 ? "item_blink" : null));
        list.Add(T(1805, 2, "DOTA_GAMERULES_STATE_POST_GAME", 180, "item_blink", "radiant"));
        return list;
    }

    private static MatchRecord Run(MatchRecorder r, IEnumerable<MatchTick> ticks)
    {
        MatchRecord? done = null;
        foreach (var t in ticks) done = r.Observe(t) ?? done;
        return done ?? r.Current!;
    }

    [Fact]
    public void Trace_Full_IsCompleteAndEnded()
    {
        var m = Run(new MatchRecorder(), FullTrace());
        Assert.True(m.Ended);
        Assert.False(m.Partial);
        Assert.Equal(new[] { 300, 1000 }, m.DeathEvents.Select(d => d.Clock));
        Assert.Equal(0, m.UnlocatedDeaths);
        Assert.Equal(1.0, m.CoverageOf(0, m.DurationSec), 3);
    }

    [Fact]
    public void Trace_RestartMidMatch_FromCheckpoint_NoDuplicates_SameAsFull()
    {
        var full = Run(new MatchRecorder(), FullTrace());
        var trace = FullTrace();
        var first = new MatchRecorder();
        foreach (var t in trace.Take(150)) first.Observe(t);           // до 745 с
        var checkpoint = MatchHistoryStore.Clone(first.Current!);      // контрольная запись → перезапуск
        var second = new MatchRecorder();
        second.Restore(checkpoint);
        var resumed = Run(second, trace.Skip(150));                    // все пакеты поданы повторно без пропуска

        Assert.True(resumed.Resumed);
        Assert.Equal(full.DeathEvents.Select(d => d.Clock), resumed.DeathEvents.Select(d => d.Clock));
        Assert.Equal(full.Samples.Select(s => s.Clock), resumed.Samples.Select(s => s.Clock));
        Assert.Equal(full.ItemTimings, resumed.ItemTimings);
        Assert.Equal(0, resumed.UnlocatedDeaths);
        Assert.False(resumed.Partial);
    }

    [Fact]
    public void Trace_DataGap_DeathsWithoutPlace_AndPhaseNotCovered()
    {
        var trace = FullTrace();
        var r = new MatchRecorder();
        // данные пропали с 250 до 1100 с: за это время обе смерти
        var m = Run(r, trace.Where(t => t.Clock is < 250 or > 1100));
        Assert.Empty(m.DeathEvents);                                    // старые координаты не выдаём за место смерти
        Assert.Equal(2, m.UnlocatedDeaths);
        Assert.Equal(2, m.Deaths);
        Assert.True(m.Partial);
        Assert.False(m.Covers(GamePhases.LaneEnd, GamePhases.MidEnd));
        Assert.Equal(2, m.Coverage.Count);
    }

    [Fact]
    public void Trace_StartedLate_EarlyPhaseUnknown()
    {
        var m = Run(new MatchRecorder(), FullTrace().Where(t => t.Clock >= 900));
        Assert.Equal(1, m.UnlocatedDeaths);                             // смерть на 300 с — до начала записи
        Assert.Single(m.DeathEvents);                                   // смерть на 1000 с — наблюдали
        Assert.False(m.Covers(0, GamePhases.LaneEnd));
        Assert.True(m.Partial);
    }

    [Fact]
    public void Trace_FlushWithoutEnd_MarkedNotEnded()
    {
        var r = new MatchRecorder();
        foreach (var t in FullTrace().Take(100)) r.Observe(t);
        var m = r.Flush()!;
        Assert.False(m.Ended);
        Assert.True(m.Partial);
    }

    [Fact]
    public void LegacyRecord_CoverageFromSamples()
    {
        var m = Rec("old");
        m.DurationSec = 1200;
        for (int c = 0; c <= 1200; c += 30) m.Samples.Add(new MatchSample { Clock = c });
        Assert.Null(m.Ended);
        Assert.Equal(1.0, m.CoverageOf(0, 1200), 3);
        Assert.False(m.Partial);
    }

    // ------------------------------------------------------------------ единые фазы

    [Fact]
    public void Phases_SameEverywhere()
    {
        const int at27 = 27 * 60;
        Assert.Equal(GamePhase.Late, GamePhases.Of(at27));
        var m = Rec("p");
        m.DeathEvents.Add(new DeathEvent { Clock = at27, X = 0, Y = 0 });
        Assert.Equal(1, DeathAnalysis.Analyze(m)!.Late);
        var map = MapZones.Summarize(new[] { (m, m.DeathEvents[0]) });
        Assert.Equal(1, map.Late);
        Assert.Equal(GamePhases.LaneEnd, DeathAnalysis.EarlyEnd);
        Assert.Equal(GamePhases.MidEnd, DeathAnalysis.MidEnd);
    }

    // ------------------------------------------------------------------ 0.17.5+: доводка по ревью

    [Fact]
    public void Checkpoint_FinalRecordWins_OverStaleCheckpoint()
    {
        var cp = Rec("gsi-1"); cp.DurationSec = 1500; cp.Ended = null;
        var final = Rec("gsi-1"); final.DurationSec = 1400; final.Ended = true;      // итог сохранён, контрольная не удалена
        Assert.Equal(CheckpointAction.Discard, CheckpointPolicy.Decide(cp, final, "1", false));
        Assert.Equal(CheckpointAction.Discard, CheckpointPolicy.Decide(cp, final, null, false));

        var longer = Rec("gsi-1"); longer.DurationSec = 1600; longer.Ended = false;  // уже сохранена более полная неполная
        Assert.Equal(CheckpointAction.Discard, CheckpointPolicy.Decide(cp, longer, "1", false));

        var shorter = Rec("gsi-1"); shorter.DurationSec = 900; shorter.Ended = false;
        Assert.Equal(CheckpointAction.Resume, CheckpointPolicy.Decide(cp, shorter, "gsi-1", false));
        Assert.Equal(CheckpointAction.Resume, CheckpointPolicy.Decide(cp, null, "gsi-1", false));
        Assert.Equal(CheckpointAction.SaveAsPartial, CheckpointPolicy.Decide(cp, null, "999", false));
        Assert.Equal(CheckpointAction.SaveAsPartial, CheckpointPolicy.Decide(cp, null, "gsi-1", recorderBusy: true));
        Assert.Equal(CheckpointAction.SaveAsPartial, CheckpointPolicy.Decide(cp, null, null, false));
    }

    [Fact]
    public void Checkpoint_StaleNextToFinal_InStore_FinalKept()
    {
        var store = new MatchHistoryStore(_dir);
        var final = Rec("gsi-1"); final.Ended = true; final.DurationSec = 2000; final.Kills = 11;
        Assert.True(store.Save(final));
        var stale = Rec("gsi-1"); stale.DurationSec = 1700; stale.Kills = 8;
        store.SaveCheckpoint(stale);                                   // авария до ClearCheckpoint
        var cp = store.LoadCheckpoint()!.Value.Record;
        Assert.Equal(CheckpointAction.Discard, CheckpointPolicy.Decide(cp, store.Find(cp.Id), null, false));
        Assert.Equal(11, store.Find("gsi-1")!.Kills);
    }

    [Fact]
    public void All_ReturnsIndependentCopies_EveryCall()
    {
        var store = new MatchHistoryStore(_dir);
        store.Save(Rec("a", kills: 5));
        store.All()[0].Kills = 99;                                     // читающий код испортил объект
        store.Find("a")!.Kills = 77;
        Assert.Equal(5, store.All()[0].Kills);                         // следующий All без всякого Save — цел
        Assert.Equal(5, store.Find("a")!.Kills);                       // и Find (его использует CheckpointPolicy)
        Assert.NotSame(store.All()[0], store.All()[0]);
        Assert.Equal(5, new MatchHistoryStore(_dir).All()[0].Kills);   // диск цел
    }

    [Fact]
    public void UnsavedQueue_StaleEntry_DoesNotOverwriteFinal()
    {
        var store = new MatchHistoryStore(_dir);
        var final = Rec("gsi-7"); final.Ended = true; final.DurationSec = 2100; final.Kills = 12;
        Assert.True(store.Save(final));
        var stale = Rec("gsi-7"); stale.Ended = false; stale.DurationSec = 1500; stale.Kills = 6;   // авария до очистки очереди
        var fresh = Rec("gsi-8");
        store.SavePending(new[] { stale, fresh });

        var again = new MatchHistoryStore(_dir);
        var needed = UnsavedQueuePolicy.StillNeeded(again.LoadPending(), again.Find);
        Assert.Equal(new[] { "gsi-8" }, needed.Select(r => r.Id));
        Assert.Equal(12, again.Find("gsi-7")!.Kills);

        // незавершённая сохранённая версия короче — кандидат нужен; завершённый кандидат главнее незавершённой
        var shortSaved = Rec("s"); shortSaved.Ended = false; shortSaved.DurationSec = 900;
        var longer = Rec("s"); longer.Ended = false; longer.DurationSec = 1200;
        Assert.False(CheckpointPolicy.IsAtLeastAsComplete(shortSaved, longer));
        var endedShorter = Rec("s"); endedShorter.Ended = true; endedShorter.DurationSec = 800;
        Assert.False(CheckpointPolicy.IsAtLeastAsComplete(shortSaved, endedShorter));
        Assert.True(CheckpointPolicy.IsAtLeastAsComplete(endedShorter, longer));
    }

    [Fact]
    public void PendingQueue_SurvivesRestart()
    {
        var store = new MatchHistoryStore(_dir);
        Assert.True(store.SavePending(new[] { Rec("p1"), Rec("p2") }));
        var again = new MatchHistoryStore(_dir);
        Assert.Equal(new[] { "p1", "p2" }, again.LoadPending().Select(r => r.Id));
        Assert.Empty(again.All());                                     // очередь — не история
        Assert.True(again.SavePending(Array.Empty<MatchRecord>()));
        Assert.Empty(new MatchHistoryStore(_dir).LoadPending());
    }

    [Fact]
    public void PendingQueue_Corrupt_SetAside()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, MatchHistoryStore.PendingFile), "[{ broken");
        Assert.Empty(new MatchHistoryStore(_dir).LoadPending());
        Assert.True(File.Exists(Path.Combine(_dir, MatchHistoryStore.PendingFile + ".bad")));
    }

    [Fact]
    public void ItemSeenAfterGap_IsApproximate_ContinuousIsExact()
    {
        var trace = FullTrace();                                       // blink появляется на 900 с
        var gap = Run(new MatchRecorder(), trace.Where(t => t.Clock is < 850 or > 1000));
        Assert.True(gap.IsApproxTiming("item_blink"));
        Assert.DoesNotContain(gap.ExactItemTimings, kv => kv.Key == "item_blink");

        var full = Run(new MatchRecorder(), trace);
        Assert.False(full.IsApproxTiming("item_blink"));
        Assert.Equal(900, full.ItemTimings["item_blink"]);

        // восстановление без пропуска (все пакеты поданы) — тайминг точный
        var first = new MatchRecorder();
        foreach (var t in trace.Take(150)) first.Observe(t);
        var second = new MatchRecorder();
        second.Restore(MatchHistoryStore.Clone(first.Current!));
        Assert.False(Run(second, trace.Skip(150)).IsApproxTiming("item_blink"));

        // запись началась посреди игры — всё, что уже было в инвентаре, приблизительно
        var late = Run(new MatchRecorder(), trace.Where(t => t.Clock >= 1200));
        Assert.True(late.IsApproxTiming("item_blink"));

        // стартовый инвентарь: с предматча (clock ≤ 0) — точно; первый снимок на 30-й секунде — уже приблизительно
        var fromPregame = Run(new MatchRecorder(), new[] { T(-30, 0, item: "item_quelling_blade"), T(-25, 0, item: "item_quelling_blade") });
        Assert.False(fromPregame.IsApproxTiming("item_quelling_blade"));
        var from30 = Run(new MatchRecorder(), new[] { T(30, 0, item: "item_quelling_blade"), T(35, 0, item: "item_quelling_blade") });
        Assert.True(from30.IsApproxTiming("item_quelling_blade"));
    }

    // ------------------------------------------------------------------ запас на выкуп

    private static InventoryState Late(int gold, int unreliable, int? bbCost = 1800, int? bbCd = 0, int clock = 2400) =>
        new(clock, true, gold, unreliable, new Dictionary<string, string> { ["teleport0"] = "item_tpscroll" }, Position.Carry, bbCost, bbCd);

    [Fact]
    public void UnspentGold_RespectsKnownBuybackReserve()
    {
        var s = new Roshiq.Core.Settings.AlertSettings { UnspentGoldThreshold = 2000, BuybackFromMinute = 30 };
        // 3000 при выкупе 1800: сверх запаса 1200 — копит на выкуп, не ругаем
        Assert.DoesNotContain(MatchAlerts.UnspentGold, MatchAlerts.Conditions(Late(3000, 2500), s));
        // сверх запаса 3200 и под риском 4000 — подсказываем
        Assert.Contains(MatchAlerts.UnspentGold, MatchAlerts.Conditions(Late(5000, 4000), s));
        // выкуп на перезарядке, цена неизвестна или ещё рано — запас не учитываем (как раньше)
        Assert.Contains(MatchAlerts.UnspentGold, MatchAlerts.Conditions(Late(3000, 2500, bbCd: 40), s));
        Assert.Contains(MatchAlerts.UnspentGold, MatchAlerts.Conditions(Late(3000, 2500, bbCost: null), s));
        Assert.Contains(MatchAlerts.UnspentGold, MatchAlerts.Conditions(Late(3000, 2500, bbCd: null), s));
        Assert.Contains(MatchAlerts.UnspentGold, MatchAlerts.Conditions(Late(3000, 2500, clock: 1200), s));
    }
}
