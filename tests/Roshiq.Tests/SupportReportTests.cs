using System.IO.Compression;
using Roshiq.Core.Diagnostics;
using Xunit;

namespace Roshiq.Tests;

/// <summary>0.18.6: «Собрать отчёт» — всё нужное для разбора проблемы и ничего личного.</summary>
public class SupportReportTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "roshiq-report-" + Guid.NewGuid().ToString("N"));
    private string Zip => _root + ".zip";

    public SupportReportTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "logs", "tips"));
        Directory.CreateDirectory(Path.Combine(_root, "logs", "vision"));
        Directory.CreateDirectory(Path.Combine(_root, "logs", "crashes"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
        try { File.Delete(Zip); } catch { }
    }

    private static string Read(ZipArchive z, string name)
    {
        using var r = new StreamReader(z.GetEntry(name)!.Open());
        return r.ReadToEnd();
    }

    [Fact]
    public void Report_HasLogsFramesAndSettings_WithoutPrivateData()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        File.WriteAllText(Path.Combine(_root, "logs", "roshiq-20261109.log"),
            "NET GET https://api.opendota.com/api/players/1831570413/recentMatches -> OK\n" +
            "Steam ID 76561198000000000 привязан\n" +
            $"Путь: {profile}\\AppData\\Roaming\\Roshiq\n" +
            "Vision: кадр 2560×109, распознано 0\n");
        var old = Path.Combine(_root, "logs", "roshiq-20260101.log");
        File.WriteAllText(old, "старый");
        File.SetLastWriteTime(old, DateTime.Now.AddDays(-30));
        File.WriteAllText(Path.Combine(_root, "logs", "tips", "tips-777.log"), "21:40:12 | 10:05 | показана | alert_no_tp | Нет ТП");
        File.WriteAllBytes(Path.Combine(_root, "logs", "vision", "draft-20261109-120000-unsure.png"), new byte[] { 137, 80, 78, 71 });
        File.WriteAllText(Path.Combine(_root, "logs", "crashes", "crash-20261109-120000.txt"), "Версия: 0.18.6\nNullReferenceException");
        File.WriteAllText(Path.Combine(_root, "logs", "crashes", "crash-seen.txt"), "x");
        File.WriteAllText(Path.Combine(_root, "settings.json"),
            "{\"Theme\":\"gold\",\"Gsi\":{\"Port\":53742,\"Token\":\"abcdef\"},\"Data\":{\"SteamAccountId\":\"1831570413\",\"SteamName\":\"FIGI6\",\"OpenDotaApiKey\":\"secret\",\"SteamLinkedVia\":\"gsi\"}," +
            "\"Overlay\":{\"ToggleHotkey\":\"F8\"}}");

        int n = SupportReport.Create(_root, Zip, "ROSHIQ 0.18.6\nОкно Dota 2: 3440×1440", DateTime.Now);

        using var z = ZipFile.OpenRead(Zip);
        var names = z.Entries.Select(e => e.FullName).ToList();
        Assert.Equal(n, names.Count);
        Assert.Contains("system.txt", names);
        Assert.Contains("logs/roshiq-20261109.log", names);
        Assert.DoesNotContain("logs/roshiq-20260101.log", names);              // старше недели — не нужен
        Assert.Contains("logs/tips/tips-777.log", names);
        Assert.Contains("logs/vision/draft-20261109-120000-unsure.png", names);
        Assert.Contains("logs/crashes/crash-20261109-120000.txt", names);
        Assert.DoesNotContain("logs/crashes/crash-seen.txt", names);

        var log = Read(z, "logs/roshiq-20261109.log");
        Assert.DoesNotContain("1831570413", log);
        Assert.DoesNotContain("76561198000000000", log);
        Assert.DoesNotContain(profile, log);
        Assert.Contains("players/***", log);
        Assert.Contains("кадр 2560×109", log);                                  // полезное остаётся

        var settings = Read(z, "settings.json");
        Assert.DoesNotContain("1831570413", settings);
        Assert.DoesNotContain("FIGI6", settings);
        Assert.DoesNotContain("secret", settings);
        Assert.DoesNotContain("abcdef", settings);
        Assert.Contains("gold", settings);
        Assert.Contains("F8", settings);                                         // ToggleHotkey — не ключ доступа
    }

    [Fact]
    public void BrokenSettings_DoNotBreakTheReport()
    {
        File.WriteAllText(Path.Combine(_root, "settings.json"), "{ сломано");
        SupportReport.Create(_root, Zip, "x", DateTime.Now);
        using var z = ZipFile.OpenRead(Zip);
        Assert.Contains("повреждён", Read(z, "settings.json"));
    }
}
