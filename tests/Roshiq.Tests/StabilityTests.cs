using Roshiq.Core.Diagnostics;
using Roshiq.GSI;
using Xunit;

namespace Roshiq.Tests;

/// <summary>0.13: безопасный режим, отчёты о сбоях, сторож связи с игрой, горячие клавиши, параметры запуска.</summary>
public class StabilityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dp_stab_" + Guid.NewGuid().ToString("N"));

    public StabilityTests()
    {
        Directory.CreateDirectory(_dir);
        Log.FileEnabled = false;
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void CrashGuard_SafeModeAfterTwoFailedStarts()
    {
        var path = Path.Combine(_dir, "guard.txt");
        Assert.False(CrashGuard.Begin(path).SafeMode);     // 1-й запуск
        Assert.False(CrashGuard.Begin(path).SafeMode);     // 1-й упал
        var third = CrashGuard.Begin(path);                // 2 сбоя подряд
        Assert.True(third.SafeMode);
        Assert.Equal(2, third.FailedStarts);

        CrashGuard.MarkStable(path);                       // минута без сбоя
        Assert.False(CrashGuard.Begin(path).SafeMode);
    }

    [Fact]
    public void CrashGuard_ForcedSafeMode()
    {
        var path = Path.Combine(_dir, "guard.txt");
        Assert.True(CrashGuard.Begin(path, forceSafe: true).SafeMode);
    }

    [Fact]
    public void CrashGuard_GarbageFileIsZero()
    {
        var path = Path.Combine(_dir, "guard.txt");
        File.WriteAllText(path, "мусор");
        Assert.Equal(0, CrashGuard.Read(path));
    }

    [Fact]
    public void CrashReport_ContainsErrorAndLog()
    {
        Exception ex;
        try { throw new InvalidOperationException("снаружи", new ArgumentException("внутри")); }
        catch (Exception e) { ex = e; }
        var text = CrashReport.Build(ex, "0.13.0", "тест", new[] { "строка журнала" }, new DateTime(2026, 11, 7, 12, 0, 0));
        Assert.Contains("0.13.0", text);
        Assert.Contains("InvalidOperationException: снаружи", text);
        Assert.Contains("ArgumentException: внутри", text);
        Assert.Contains("строка журнала", text);
    }

    [Fact]
    public void CrashReport_PendingUntilAcknowledged()
    {
        Assert.Null(CrashReport.Pending(_dir));
        var first = CrashReport.Save(_dir, "a", new DateTime(2026, 11, 7, 10, 0, 0))!;
        var second = CrashReport.Save(_dir, "b", new DateTime(2026, 11, 7, 11, 0, 0))!;
        Assert.Equal(second, CrashReport.Pending(_dir));
        CrashReport.Acknowledge(second);
        Assert.Null(CrashReport.Pending(_dir));
        Assert.Equal(second, CrashReport.Latest(_dir));
        // некритичные ошибки не вызывают окна при запуске
        CrashReport.Save(_dir, "c", new DateTime(2026, 11, 7, 12, 0, 0), CrashReport.ErrorPrefix);
        Assert.Null(CrashReport.Pending(_dir));
        Assert.NotNull(CrashReport.Latest(_dir, CrashReport.ErrorPrefix));
        Assert.True(File.Exists(first));
    }

    [Fact]
    public void CrashReport_KeepsLastTen()
    {
        for (int i = 0; i < 14; i++) CrashReport.Save(_dir, "x", new DateTime(2026, 11, 7, 10, i, 0));
        Assert.Equal(10, Directory.GetFiles(_dir, CrashReport.Prefix + "*.txt").Length);
    }

    [Fact]
    public void Watchdog_WarnsOnceWhenNoData()
    {
        var w = new GsiWatchdog();
        var t0 = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(GsiProblem.None, w.Tick(true, true, null, 0, t0));
        Assert.Equal(GsiProblem.None, w.Tick(true, true, null, 0, t0.AddSeconds(60)));
        Assert.Equal(GsiProblem.NoData, w.Tick(true, true, null, 0, t0.AddSeconds(95)));
        Assert.Equal(GsiProblem.None, w.Tick(true, true, null, 0, t0.AddSeconds(120)));   // второй раз не пристаёт
        Assert.Equal(GsiProblem.NoData, w.Current);

        // игру закрыли и открыли снова — новый запуск, можно предупредить ещё раз
        w.Tick(false, true, null, 0, t0.AddSeconds(130));
        Assert.Equal(GsiProblem.None, w.Current);
        w.Tick(true, true, null, 0, t0.AddSeconds(140));
        Assert.Equal(GsiProblem.NoData, w.Tick(true, true, null, 0, t0.AddSeconds(240)));
    }

    [Fact]
    public void Watchdog_SilentInMenuWhenSetupIsReady()
    {
        // 11.11.2026: Dota пролежала в меню 9 минут, данных не было — это нормально, данные приходят с загрузкой матча
        var w = new GsiWatchdog();
        var t0 = new DateTime(2026, 11, 11, 0, 0, 0, DateTimeKind.Utc);
        w.Tick(true, true, null, 0, t0, setupReady: true);
        Assert.Equal(GsiProblem.None, w.Tick(true, true, null, 0, t0.AddSeconds(561), setupReady: true));
        Assert.Equal(GsiProblem.None, w.Current);
        // файла связи нет — тогда молчание игры и есть поломка
        Assert.Equal(GsiProblem.NoData, w.Tick(true, true, null, 0, t0.AddSeconds(600), setupReady: false));
        // неверный ключ — всегда поломка, даже если файл на месте
        var w2 = new GsiWatchdog();
        w2.Tick(true, true, null, 1, t0, setupReady: true);
        Assert.Equal(GsiProblem.BadToken, w2.Tick(true, true, null, 4, t0.AddSeconds(100), setupReady: true));
    }

    [Fact]
    public void Watchdog_SilentWhenDataArrives()
    {
        var w = new GsiWatchdog();
        var t0 = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
        w.Tick(true, true, null, 0, t0);
        Assert.Equal(GsiProblem.None, w.Tick(true, true, t0.AddSeconds(20), 0, t0.AddSeconds(200)));
        Assert.Equal(GsiProblem.None, w.Current);
    }

    [Fact]
    public void Watchdog_OldPacketDoesNotCount_AndDetectsBadToken()
    {
        var w = new GsiWatchdog();
        var t0 = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
        w.Tick(true, true, t0.AddHours(-1), 3, t0);                         // пакет от прошлой игры
        Assert.Equal(GsiProblem.BadToken, w.Tick(true, true, t0.AddHours(-1), 7, t0.AddSeconds(100)));
    }

    [Theory]
    [InlineData("shift+ctrl+f8", "Ctrl+Shift+F8")]
    [InlineData("F8", "F8")]
    [InlineData("alt + k", "Alt+K")]
    [InlineData("Control+Shift+K", "Ctrl+Shift+K")]
    [InlineData("ctrl+", "")]
    [InlineData("", "")]
    public void Hotkey_Normalize(string input, string expected) => Assert.Equal(expected, HotkeyRules.Normalize(input));

    [Fact]
    public void Hotkey_DotaClashAndDuplicates()
    {
        Assert.True(HotkeyRules.ClashesWithDota("Q"));
        Assert.True(HotkeyRules.ClashesWithDota("d1"));
        Assert.False(HotkeyRules.ClashesWithDota("Ctrl+Q"));
        Assert.False(HotkeyRules.ClashesWithDota("F8"));

        var dup = HotkeyRules.Duplicates(new Dictionary<string, string>
        {
            ["toggle"] = "F8", ["details"] = "ctrl+f8", ["edit"] = "Ctrl+F8", ["roshan"] = "Ctrl+Shift+K"
        });
        Assert.Equal(new[] { "Ctrl+F8" }, dup);
    }

    private const string LocalConfig = @"""UserLocalConfigStore""
{
	""Software""
	{
		""Valve""
		{
			""Steam""
			{
				""apps""
				{
					""730""
					{
						""LaunchOptions""		""-novid""
					}
					""570""
					{
						""LastPlayed""		""1700000000""
						""LaunchOptions""		""-novid -gamestateintegration -high""
					}
				}
			}
		}
	}
}";

    [Fact]
    public void LaunchOptions_FindsDotaEntry()
    {
        var opts = DotaLaunchOptions.Parse(LocalConfig);
        Assert.Equal("-novid -gamestateintegration -high", opts);
        Assert.True(DotaLaunchOptions.HasGsiFlag(opts));
        Assert.Equal("-novid", DotaLaunchOptions.Parse(LocalConfig, "730"));
        Assert.False(DotaLaunchOptions.HasGsiFlag("-novid"));
        Assert.False(DotaLaunchOptions.HasGsiFlag(null));
        Assert.Null(DotaLaunchOptions.Parse(@"""apps"" { ""570"" { ""LastPlayed"" ""1"" } }"));
    }
}
