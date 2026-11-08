using Roshiq.Core.Diagnostics;
using Roshiq.Data;
using Xunit;

namespace Roshiq.Tests;

/// <summary>0.17.9: отчёты прошлых версий не считаются «недавними ошибками», кэш статистики пишется по одному.</summary>
public class ReportHygieneTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "roshiq-hyg-" + Guid.NewGuid().ToString("N"));

    public ReportHygieneTests()
    {
        Directory.CreateDirectory(_dir);
        Log.FileEnabled = false;
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Report(string version, DateTime when, string prefix)
    {
        var text = CrashReport.Build(new InvalidOperationException("x"), version, "тест", Array.Empty<string>(), when);
        var path = CrashReport.Save(_dir, text, when, prefix)!;
        File.SetLastWriteTimeUtc(path, when.ToUniversalTime());
        return path;
    }

    [Fact]
    public void VersionOf_ReadsReportHeader()
    {
        var path = Report("0.13.0", new DateTime(2026, 11, 7, 12, 41, 52), CrashReport.ErrorPrefix);
        Assert.Equal("0.13.0", CrashReport.VersionOf(path));
        Assert.Null(CrashReport.VersionOf(Path.Combine(_dir, "нет.txt")));
    }

    [Fact]
    public void LatestForVersion_IgnoresReportsOfOlderVersions()
    {
        Report("0.13.0", new DateTime(2026, 11, 7, 12, 41, 52), CrashReport.ErrorPrefix);   // как у пользователя: ошибка старой версии
        Assert.Null(CrashReport.LatestForVersion(_dir, "0.17.9"));

        var older = Report("0.17.9", new DateTime(2026, 11, 9, 10, 0, 0), CrashReport.Prefix);
        var newer = Report("0.17.9", new DateTime(2026, 11, 9, 11, 0, 0), CrashReport.ErrorPrefix);
        Assert.Equal(newer, CrashReport.LatestForVersion(_dir, "0.17.9"));
        Assert.NotEqual(older, CrashReport.LatestForVersion(_dir, "0.17.9"));
        Assert.Null(CrashReport.LatestForVersion(Path.Combine(_dir, "нет"), "0.17.9"));
    }

    [Fact]
    public void MatchupCache_ConcurrentPuts_AllSaved()
    {
        var path = Path.Combine(_dir, "cache", "matchups.json");
        var cache = new MatchupStatsCache(path);
        var rows = new[] { new MatchupStatsCache.OpponentRow { HeroId = 2, Games = 10, Wins = 6 } };
        Parallel.For(1, 41, new ParallelOptions { MaxDegreeOfParallelism = 8 }, id => cache.Put(id, rows, "test", DateTime.UtcNow));

        var reread = new MatchupStatsCache(path);
        Assert.Equal(40, reread.HeroesCached);
        Assert.Equal(6, reread.Get(17, 2)!.Wins);
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void GsiWatchdog_ExposesGameStart()
    {
        var w = new GsiWatchdog();
        var t0 = new DateTime(2026, 11, 8, 18, 0, 0, DateTimeKind.Utc);
        w.Tick(true, true, null, 0, t0);
        Assert.Equal(t0, w.GameSinceUtc);
        w.Tick(false, true, null, 0, t0.AddMinutes(1));
        Assert.Null(w.GameSinceUtc);
    }
}

/// <summary>0.18.1: обновление без переустановки.</summary>
public class InstallerLaunchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "roshiq-inst-" + Guid.NewGuid().ToString("N"));

    public InstallerLaunchTests() { Directory.CreateDirectory(_dir); }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void InstalledCopy_IsDetectedByUninstaller_PortableIsNot()
    {
        Assert.False(Roshiq.Core.Updates.InstallerLaunch.IsInstalledCopy(_dir));                 // распакованный zip
        File.WriteAllText(Path.Combine(_dir, Roshiq.Core.Updates.InstallerLaunch.UninstallerName), "");
        Assert.True(Roshiq.Core.Updates.InstallerLaunch.IsInstalledCopy(_dir));
        Assert.False(Roshiq.Core.Updates.InstallerLaunch.IsInstalledCopy(Path.Combine(_dir, "нет")));
    }

    [Fact]
    public void SilentArguments_NoWizard_ClosesApp_NoReboot_PortableGetsWizard()
    {
        var silent = Roshiq.Core.Updates.InstallerLaunch.Arguments(true);
        foreach (var flag in new[] { "/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS" })
            Assert.Contains(flag, silent);
        Assert.Equal("", Roshiq.Core.Updates.InstallerLaunch.Arguments(false));
    }

    [Fact]
    public void InstallerScript_RelaunchesAppOnlyAfterSilentUpdate()
    {
        var iss = File.ReadAllText(Path.Combine(RepoRoot(), "installer", "Roshiq.iss"));
        Assert.Contains("Flags: nowait skipifnotsilent", iss);
        Assert.Contains("Flags: nowait postinstall skipifsilent", iss);
    }

    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string self = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(self)!, "..", ".."));
}
