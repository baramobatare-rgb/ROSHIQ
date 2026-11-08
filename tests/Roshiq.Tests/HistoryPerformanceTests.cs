using System.Diagnostics;
using Roshiq.Core.Models;
using Roshiq.Data;
using Roshiq.Recommendations;
using Xunit;
using Xunit.Abstractions;

namespace Roshiq.Tests;

/// <summary>0.17.7: дешёвые признаки истории, пакетный импорт, замер на 1000 игр.</summary>
public class HistoryPerformanceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "roshiq-perf-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _out;

    public HistoryPerformanceTests(ITestOutputHelper output) { _out = output; }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static MatchRecord Game(int i, MatchResult result = MatchResult.Win)
    {
        var m = new MatchRecord
        {
            Id = "gsi-" + i, MatchId = i.ToString(), Source = "GSI", HeroId = 1 + i % 120, Position = Position.Mid,
            StartedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i), DurationSec = 2400, Result = result, Ended = true
        };
        for (int c = 0; c <= 2400; c += 30) m.Samples.Add(new MatchSample { Clock = c, Gold = c, Gpm = 450, Xpm = 500, LastHits = c / 10, Level = 1 + c / 150 });
        m.Coverage.Add(new CoverageSpan { From = 0, To = 2400 });
        m.ItemTimings["item_blink"] = 900;
        m.DeathEvents.Add(new DeathEvent { Clock = 700, X = 100, Y = -200, GoldAtRisk = 300 });
        return m;
    }

    [Fact]
    public void Revision_ChangesOnEverySave_IncludingSameIdResultFix_AndDelete()
    {
        var store = new MatchHistoryStore(_dir);
        int r0 = store.Revision;
        Assert.True(store.Save(Game(1, MatchResult.Unknown)));
        Assert.True(store.Save(Game(2)));
        int r1 = store.Revision;
        Assert.True(r1 > r0);
        var fixedResult = store.Find("gsi-1")!;                         // старый матч, не самый новый
        fixedResult.Result = MatchResult.Loss;
        Assert.True(store.Save(fixedResult));
        Assert.True(store.Revision > r1);                              // исправленный итог меняет версию, хотя число и «последний» те же
        int r2 = store.Revision;
        store.Delete("gsi-2");
        Assert.True(store.Revision > r2);

        Directory.CreateDirectory(Path.Combine(_dir, "gsi-9.json"));   // запись не удастся
        int r3 = store.Revision;
        Assert.False(store.Save(Game(9)));
        Assert.Equal(r3, store.Revision);                              // неудача версию не меняет
    }

    [Fact]
    public void Revision_Count_Latest_DoNotDeserializeWholeHistory()
    {
        var store = new MatchHistoryStore(_dir);
        for (int i = 0; i < 50; i++) store.Save(Game(i));
        long before = store.Deserializations;
        for (int i = 0; i < 100; i++) { _ = store.Revision; _ = store.Count; }
        Assert.Equal(before, store.Deserializations);                  // ни одного разбора JSON
        Assert.Equal("gsi-49", store.Latest()!.Id);
        Assert.Equal(before + 1, store.Deserializations);              // Latest — одна запись, не 50
        Assert.Equal(50, store.Count);
    }

    [Fact]
    public void Import_OneChangedEvent_HonestTotals_AndGsiKeepsPriority()
    {
        var store = new MatchHistoryStore(_dir);
        var gsi = Game(111, MatchResult.Unknown);
        store.Save(gsi);
        Directory.CreateDirectory(Path.Combine(_dir, "od-333.json"));  // этот матч записать не получится
        int changed = 0;
        store.Changed += (_, _) => changed++;
        int rev = store.Revision;

        var rows = new List<RecentMatchRow>
        {
            new() { MatchId = 111, PlayerSlot = 1, RadiantWin = true, HeroId = 2, Duration = 2000 },          // GSI: только итог
            new() { MatchId = 333, PlayerSlot = 1, RadiantWin = false, HeroId = 3, Duration = 1700 },         // ошибка записи
        };
        for (int i = 0; i < 20; i++)
            rows.Add(new RecentMatchRow { MatchId = 1000 + i, PlayerSlot = 130, RadiantWin = true, HeroId = 5, Duration = 1800, StartTime = 1730000000 + i });
        var result = store.Import(rows);

        Assert.Equal(new ImportResult(20, 1, 1), result);
        Assert.Equal(1, changed);                                       // одно уведомление на всю пачку
        Assert.True(store.Revision > rev);
        var reread = new MatchHistoryStore(_dir).All();
        Assert.Equal(21, reread.Count);                                 // 1 GSI + 20 новых; неудачный не появился
        var kept = reread.Single(m => m.Id == "gsi-111");
        Assert.Equal(MatchResult.Win, kept.Result);
        Assert.Equal(81, kept.Samples.Count);                           // запись GSI не заменена данными OpenDota

        // повторный импорт тех же строк: ничего не меняется — и уведомления нет
        changed = 0;
        var again = store.Import(rows.Where(r => r.MatchId != 333));
        Assert.Equal(new ImportResult(0, 0, 0), again);
        Assert.Equal(0, changed);
    }

    [Fact]
    public void Benchmark_1000Games()
    {
        var store = new MatchHistoryStore(_dir);
        for (int i = 0; i < 1000; i++) store.Save(Game(i));
        var fresh = new MatchHistoryStore(_dir);

        var sw = Stopwatch.StartNew();
        int count = fresh.Count;                                        // первое обращение: чтение файлов с диска
        long load = sw.ElapsedMilliseconds;

        sw.Restart();
        for (int i = 0; i < 30; i++) { _ = fresh.Revision; _ = fresh.Latest(); }
        long cheap = sw.ElapsedMilliseconds;
        long cheapDeserialized = fresh.Deserializations;

        sw.Restart();
        var all = fresh.All();
        long full = sw.ElapsedMilliseconds;

        _out.WriteLine($"1000 игр × 81 точка: загрузка {load} мс; 30 × (Revision+Latest) {cheap} мс, разборов {cheapDeserialized}; All() {full} мс");
        Assert.Equal(1000, count);
        Assert.Equal(1000, all.Count);
        Assert.Equal(30, cheapDeserialized);                            // 30 × Latest = 30 записей, а не 30 000
    }
}
