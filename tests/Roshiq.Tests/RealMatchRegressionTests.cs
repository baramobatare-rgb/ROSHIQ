using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Roshiq.Core.Diagnostics;
using Roshiq.Core.Models;
using Roshiq.Recommendations;
using Xunit;

namespace Roshiq.Tests;

/// <summary>
/// 0.18: регрессия на настоящих матчах. В Fixtures/real — записи реальных игр через GSI (обезличены: номера матчей
/// и даты заменены), в том числе старого формата без покрытия и признака конца. Тест прогоняет их через загрузку истории,
/// итог матча, разбор по фазам и цели и сравнивает весь вывод с эталоном golden.txt.
/// Изменился вывод — тест падает и показывает разницу; если изменение задумано, эталон обновляют осознанно
/// (удалить golden.txt — при следующем запуске он запишется заново).
/// </summary>
public class RealMatchRegressionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "roshiq-real-" + Guid.NewGuid().ToString("N"));

    public RealMatchRegressionTests()
    {
        Directory.CreateDirectory(_dir);
        Log.FileEnabled = false;
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static string FixturesDir([CallerFilePath] string self = "") => Path.Combine(Path.GetDirectoryName(self)!, "Fixtures", "real");

    private IReadOnlyList<MatchRecord> LoadAll()
    {
        foreach (var f in Directory.GetFiles(FixturesDir(), "*.json"))
            File.Copy(f, Path.Combine(_dir, Path.GetFileName(f)));
        var store = new MatchHistoryStore(_dir);
        Assert.Empty(store.Quarantined);
        return store.All();
    }

    [Fact]
    public void RealMatches_LoadIncludingLegacyFormat()
    {
        var all = LoadAll();
        Assert.Equal(4, all.Count);
        foreach (var m in all.Where(m => m.FromGsi))
        {
            Assert.True(m.Samples.Count > 40);
            Assert.True(m.CoverageOf(0, m.DurationSec) > 0.85, m.Id + ": записи без явного покрытия считаются по сэмплам");
            Assert.False(m.Partial, m.Id);
        }
    }

    [Fact]
    public void RealMatches_OutputMatchesGolden()
    {
        var all = LoadAll();
        var actual = Describe(all);
        var golden = Path.Combine(FixturesDir(), "golden.txt");
        if (!File.Exists(golden))
        {
            File.WriteAllText(golden, actual);   // первый запуск: эталон записан, его нужно просмотреть и сохранить вместе с кодом
            return;
        }
        var expected = File.ReadAllText(golden).Replace("\r\n", "\n");
        if (expected != actual)
        {
            var e = expected.Split('\n');
            var a = actual.Split('\n');
            int i = 0;
            while (i < Math.Min(e.Length, a.Length) && e[i] == a[i]) i++;
            Assert.Fail($"Вывод на реальных матчах изменился (строка {i + 1}).\nбыло:  {(i < e.Length ? e[i] : "<конец>")}\nстало: {(i < a.Length ? a[i] : "<конец>")}");
        }
    }

    /// <summary>Текстовый снимок всех выводов помощника по каждому матчу: одинаковый на любой машине.</summary>
    internal static string Describe(IReadOnlyList<MatchRecord> all)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        foreach (var m in all.OrderBy(x => x.Id, StringComparer.Ordinal))
        {
            sb.Append(inv, $"== {m.Id} hero={m.HeroId} pos={m.Position?.ToString() ?? "-"} dur={m.DurationSec} k/d/a={m.Kills}/{m.Deaths}/{m.Assists} gpm={m.Gpm}").Append('\n');
            sb.Append(inv, $"partial={m.Partial} coverage={m.CoverageOf(0, m.DurationSec):0.00} samples={m.Samples.Count} deaths={m.DeathEvents.Count}").Append('\n');

            var pm = PostMatch.Build(m, all);
            sb.Append(inv, $"postmatch compared={pm.Compared} basis={pm.Basis ?? "-"} main={pm.MainMistake?.Kind ?? "-"} good={string.Join(",", pm.Good.Select(g => g.Kind))}").Append('\n');
            if (pm.MainMistake is { } mm) sb.Append("  main: ").Append(mm.Ru).Append('\n');
            foreach (var g in pm.Good) sb.Append("  good: ").Append(g.Ru).Append('\n');
            sb.Append(inv, $"story core={MatchStory.PlaysCore(m)} mismatch={pm.RoleMismatch}").Append('\n');
            foreach (var p in pm.Timeline) sb.Append(inv, $"  {p.Clock} {p.Insight.Kind} {p.Insight.Severity}: {p.Insight.Ru}").Append('\n');

            if (PlayerReport.Breakdown(m, all) is { } bd)
            {
                sb.Append(inv, $"breakdown compared={bd.Compared} basis={bd.ComparisonBasis ?? "-"} late={bd.LateItems.Count}").Append('\n');
                foreach (var p in bd.Phases)
                    sb.Append(inv, $"  {p.Key} {p.From}-{p.To} deaths={p.Deaths} goldLost={p.GoldLost} lh={p.LastHits?.ToString(inv) ?? "-"}/{p.LastHitsUsual?.ToString(inv) ?? "-"} gpm={p.Gpm?.ToString(inv) ?? "-"}/{p.GpmUsual?.ToString(inv) ?? "-"} covered={p.Covered}").Append('\n');
            }
            else sb.Append("breakdown -\n");

            var goal = GoalEngine.Suggest(m, all);
            sb.Append(inv, $"goal suggest={goal?.Kind.ToString() ?? "-"} target={goal?.Target.ToString(inv) ?? "-"} item={goal?.ItemKey ?? "-"}").Append('\n');
            foreach (var kind in new[] { GoalKind.GoldOnDeath, GoalKind.FewerDeaths, GoalKind.CarryTeleport, GoalKind.CourierStash })
            {
                var target = kind switch { GoalKind.GoldOnDeath => 1000, GoalKind.FewerDeaths => 5, _ => 0 };
                var r = GoalEngine.Evaluate(new CoachGoal { Kind = kind, Target = target }, m);
                sb.Append(inv, $"  goal {kind}({target}) = {(r is { } v ? $"{v.Achieved}/{v.Value}" : "-")}").Append('\n');
            }
        }
        return sb.ToString();
    }
}
