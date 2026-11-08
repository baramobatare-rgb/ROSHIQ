using Roshiq.Core.Diagnostics;
using Roshiq.Recommendations;
using Xunit;

namespace Roshiq.Tests;

/// <summary>0.18: журнал подсказок по матчам.</summary>
public class TipJournalTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "roshiq-tips-" + Guid.NewGuid().ToString("N"));

    public TipJournalTests() { Log.FileEnabled = false; }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private static Tip T(string kind, string text) => new() { Kind = kind, Header = "ROSHIQ", Text = text };

    [Fact]
    public void Line_HasTimeGameClockOutcomeKindAndText()
    {
        var line = TipJournal.FormatLine(new DateTime(2026, 11, 9, 21, 40, 12), 605, "alert_no_tp", TipOutcome.Shown, "СРОЧНО", "Нет телепорта\nкупи ТП | сейчас");
        Assert.Equal("21:40:12 | 10:05 | показана | alert_no_tp | СРОЧНО: Нет телепорта купи ТП / сейчас", line);
        Assert.Contains("| -0:30 |", TipJournal.FormatLine(DateTime.Now, -30, "lane", TipOutcome.Expired, "", "x"));
        Assert.Contains("| — |", TipJournal.FormatLine(DateTime.Now, null, "service", TipOutcome.Dropped, "", "x"));
    }

    [Fact]
    public void FileName_PerMatch_OrServiceDay_AndUnsafeIdIgnored()
    {
        var day = new DateTime(2026, 11, 9);
        Assert.Equal("tips-9080379776.log", TipJournal.FileName("9080379776", day));
        Assert.Equal("tips-service-20261109.log", TipJournal.FileName(null, day));
        Assert.Equal("tips-service-20261109.log", TipJournal.FileName("..\\evil", day));
    }

    [Fact]
    public void Add_AppendsToMatchFile_AndKeepsAtMost30Files()
    {
        var j = new TipJournal(_dir);
        j.Add("777", 600, T("pace_lh", "К 10:00: 40 добиваний"), TipOutcome.Shown, DateTime.Now);
        j.Add("777", 640, T("alert_no_tp", "Нет телепорта"), TipOutcome.Invalidated, DateTime.Now);
        var lines = File.ReadAllLines(Path.Combine(_dir, "tips-777.log"));
        Assert.Equal(2, lines.Length);
        Assert.Contains("снята — уже неактуальна", lines[1]);

        for (int i = 0; i < 40; i++)
        {
            j.Add("m" + i, 100, T("spike", "x"), TipOutcome.Shown, DateTime.Now);
            File.SetLastWriteTimeUtc(Path.Combine(_dir, $"tips-m{i}.log"), DateTime.UtcNow.AddMinutes(-100 + i));   // в прошлом: только что созданный файл всегда новее
        }
        Assert.Equal(TipJournal.KeepFiles, j.Count());
        Assert.True(File.Exists(Path.Combine(_dir, "tips-m39.log")));
    }

    [Fact]
    public void Add_NeverThrows_WhenFolderIsAFile()
    {
        File.WriteAllText(_dir, "занято");
        try
        {
            var j = new TipJournal(_dir);
            j.Add("1", 1, T("x", "y"), TipOutcome.Shown, DateTime.Now);   // не бросает
            Assert.Equal(0, j.Count());
        }
        finally { File.Delete(_dir); }
    }
}
