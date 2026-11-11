using System.Runtime.CompilerServices;
using Roshiq.Core.Diagnostics;
using Roshiq.Core.Models;
using Roshiq.Recommendations;
using Xunit;

namespace Roshiq.Tests;

/// <summary>
/// 0.20: разбор по ходу матча на настоящих играх (обезличены: номера матчей и даты заменены).
/// 11.11.2026 после каждой игры повторялось одно и то же — «смерти» и «ТП»; разбор по ходу матча должен находить
/// своё в каждой игре, а роль из поиска не должна ломать сравнение.
/// </summary>
public class MatchStoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "roshiq-story-" + Guid.NewGuid().ToString("N"));

    public MatchStoryTests()
    {
        Directory.CreateDirectory(_dir);
        Log.FileEnabled = false;
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static string FixturesDir([CallerFilePath] string self = "") => Path.Combine(Path.GetDirectoryName(self)!, "Fixtures", "story");

    private MatchRecord Load(string name)
    {
        var dir = Path.Combine(_dir, name);
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(FixturesDir(), name + ".json"), Path.Combine(dir, name + ".json"));
        return new MatchHistoryStore(dir).All().Single();
    }

    [Fact]
    public void SupportQueueButCoreFarm_IsAnalysedAsCore()
    {
        // Omniknight записан как поз. 5 (роль в поиске), а собран Radiance и 230 добиваний
        var m = Load("omniknight-core-as-pos5");
        Assert.Equal(Position.HardSupport, m.Position);
        Assert.True(MatchStory.PlaysCore(m));
        Assert.True(MatchStory.RoleMismatch(m));
        var pm = PostMatch.Build(m, new[] { m });
        Assert.True(pm.RoleMismatch);
    }

    [Fact]
    public void CoreQueueButSupportFarm_IsAnalysedAsSupport()
    {
        // Invoker записан как поз. 1, а сыграно 1/5/37 и 43 добивания — это игра саппорта, «простой без добиваний» тут не ошибка
        var m = Load("invoker-support-as-pos1");
        Assert.False(MatchStory.PlaysCore(m));
        Assert.DoesNotContain(MatchStory.Build(m), p => p.Insight.Kind == "stall");
    }

    [Fact]
    public void Story_FindsWhatHappenedInThisMatch()
    {
        var m = Load("lifestealer-carry");
        var story = MatchStory.Build(m);
        var kinds = story.Select(p => p.Insight.Kind).ToList();
        Assert.Contains("farm_drop", kinds);      // 50:00–60:00 доход упал примерно втрое
        Assert.Contains("no_buyback", kinds);     // 5 смертей после 30:00 без выкупа
        Assert.Contains("gold_hold", kinds);      // больше 3000 золота на 16:00
        Assert.Equal(story.OrderBy(p => p.Clock).Select(p => p.Clock), story.Select(p => p.Clock));
        Assert.All(story, p => Assert.False(string.IsNullOrWhiteSpace(p.Insight.Ru)));
        Assert.All(story, p => Assert.False(string.IsNullOrWhiteSpace(p.Insight.En)));
    }

    [Fact]
    public void Phases_IncomeIsComputedFromTheMatchGraph()
    {
        var m = Load("lifestealer-carry");
        var phases = MatchStory.Phases(m);
        Assert.True(phases.Count >= 6);
        Assert.Equal(0, phases[0].From);
        Assert.All(phases, p => Assert.InRange(p.GoldPerMin, 100, 2000));
    }

    [Fact]
    public void MainMistake_ChangesWhenTheSameKindRepeats()
    {
        // две игры подряд с одной и той же главной ошибкой: во второй — другая проблема почти такой же важности
        MatchRecord Game(string id, DateTime when)
        {
            var g = new MatchRecord
            {
                Id = "gsi-" + id, MatchId = id, Source = "GSI", HeroId = 1, Position = Position.Carry, Gpm = 500,
                StartedUtc = when, DurationSec = 2400, Ended = true, Deaths = 4, LastHits = 300
            };
            g.Coverage.Add(new CoverageSpan { From = 0, To = 2400 });
            for (int c = 0; c <= 2400; c += 30) g.Samples.Add(new MatchSample { Clock = c, Gpm = 500, LastHits = c / 8, Gold = 300 });
            // три смерти без ТП (−2) и две с большим золотом (−2)
            g.DeathEvents.AddRange(new[]
            {
                new DeathEvent { Clock = 700, HadTeleport = false, GoldAtRisk = 2000 },
                new DeathEvent { Clock = 1300, HadTeleport = false, GoldAtRisk = 2000 },
                new DeathEvent { Clock = 1900, HadTeleport = false, GoldAtRisk = 100 },
            });
            return g;
        }
        var first = Game("1", new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        var second = Game("2", new DateTime(2026, 1, 1, 13, 0, 0, DateTimeKind.Utc));
        var all = new[] { first, second };
        var a = PostMatch.Build(first, all).MainMistake!;
        var b = PostMatch.Build(second, all).MainMistake!;
        Assert.NotEqual(a.Kind, b.Kind);
    }
}
