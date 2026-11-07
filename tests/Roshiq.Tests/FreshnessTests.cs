using Roshiq.Core.Settings;
using Roshiq.Data;
using Xunit;

namespace Roshiq.Tests;

/// <summary>0.12: обновление данных с GitHub, сверка с OpenDota, режимы подсказок.</summary>
public class FreshnessTests
{
    [Fact]
    public void ParseVersion_ReadsKeysAndSkipsComments()
    {
        var d = RemoteData.ParseVersion("# комментарий\ndataVersion=2026.11.07\r\nminApp = 0.12.0\n\nbroken line\n");
        Assert.Equal("2026.11.07", d["dataVersion"]);
        Assert.Equal("0.12.0", d["minapp"]);
        Assert.Equal(2, d.Count);
    }

    [Theory]
    [InlineData("2026.11.08", "2026.11.07", true)]
    [InlineData("2026.11.07b", "2026.11.07", true)]
    [InlineData("2026.11.07", "2026.11.07", false)]
    [InlineData("2026.10.30", "2026.11.07", false)]
    [InlineData("2026.11.07", "?", true)]
    [InlineData("2026.11.07", null, true)]
    [InlineData("", "2026.11.07", false)]
    [InlineData(null, "", false)]
    public void IsNewer_ComparesDataVersions(string? remote, string? current, bool expected) =>
        Assert.Equal(expected, RemoteData.IsNewer(remote, current));

    [Theory]
    [InlineData("0.12.0", "0.12.0", true)]
    [InlineData("0.12.0", "0.13.1", true)]
    [InlineData("0.13.0", "0.12.0", false)]
    [InlineData(null, "0.1.0", true)]
    [InlineData("мусор", "0.1.0", true)]
    public void AppIsEnough_ChecksMinimumVersion(string? minApp, string app, bool expected) =>
        Assert.Equal(expected, RemoteData.AppIsEnough(minApp, Version.Parse(app)));

    [Fact]
    public void Validate_AcceptsEmbeddedData()
    {
        Assert.Null(RemoteData.Validate(TestData.Db, TestData.Db));
    }

    [Fact]
    public void Validate_RejectsTruncatedData()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dp_remote_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "heroes.txt"), "# пусто\n");
            var broken = GameDatabase.Load(null, dir);
            Assert.NotNull(RemoteData.Validate(broken, TestData.Db));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void EmbeddedVersion_IsPresent()
    {
        Assert.False(string.IsNullOrWhiteSpace(RemoteData.EmbeddedVersion()));
        Assert.Equal(RemoteData.EmbeddedVersion(), TestData.Db.DataVersion);
    }

    [Fact]
    public void UsableRemoteDir_IgnoresOlderDownload()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dp_remote_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Null(RemoteData.UsableRemoteDir(dir));                       // нет data_version.txt
            File.WriteAllText(Path.Combine(dir, "data_version.txt"), "dataVersion=2000.01.01\n");
            Assert.Null(RemoteData.UsableRemoteDir(dir));                       // старее встроенных
            File.WriteAllText(Path.Combine(dir, "data_version.txt"), "dataVersion=2999.01.01\n");
            Assert.Equal(dir, RemoteData.UsableRemoteDir(dir));                 // новее — используется
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void Audit_FindsMissingHeroesAndRemovedItems()
    {
        var db = TestData.Db;
        var heroes = db.Heroes.ToDictionary(h => h.Id, h => h.Name);
        heroes[9999] = "Новый Герой";
        var items = db.Items.Select(i => i.Key).Skip(1).ToList();              // первого предмета «нет в игре»

        var audit = DataAudit.Build(db, null, heroes, items);
        Assert.Equal(new[] { "Новый Герой" }, audit.NewHeroes);
        Assert.Single(audit.RemovedItems);
        Assert.False(audit.PatchBehind);
        Assert.True(audit.HasIssues);
    }

    [Fact]
    public void Audit_CleanWhenListsMatchOrMissing()
    {
        var db = TestData.Db;
        var clean = DataAudit.Build(db, null, db.Heroes.ToDictionary(h => h.Id, h => h.Name), db.Items.Select(i => i.Key).ToList());
        Assert.False(clean.HasIssues);
        var offline = DataAudit.Build(db, null, new Dictionary<int, string>(), Array.Empty<string>());
        Assert.False(offline.HasIssues);                                         // OpenDota не ответила — не ругаемся
    }

    [Fact]
    public void Audit_DetectsNewerPatch()
    {
        var audit = DataAudit.Build(TestData.Db, "9.99", new Dictionary<int, string>(), Array.Empty<string>());
        Assert.True(audit.PatchBehind);
    }

    [Fact]
    public void Presets_MinimalNormalFull()
    {
        var s = new AppSettings();
        HintPresets.Apply(s, HintPreset.Minimal);
        Assert.Equal(HintPreset.Minimal, s.Preset);
        Assert.False(s.Timers.Toasts);
        Assert.False(s.Automation.LaneTips);
        Assert.True(s.Alerts.NoTeleport);
        Assert.True(s.Alerts.Buyback);
        Assert.False(s.Timers.IsOn("stack"));

        HintPresets.Apply(s, HintPreset.Full);
        Assert.Empty(s.Timers.Disabled);
        Assert.True(s.Timers.IsOn("lotus"));
        Assert.False(s.Alerts.Adaptive);
        Assert.True(s.Automation.PowerSpikes);

        HintPresets.Apply(s, HintPreset.Normal);
        var defaults = new AppSettings();
        Assert.Equal(defaults.Timers.Disabled, s.Timers.Disabled);
        Assert.Equal(defaults.Timers.Toasts, s.Timers.Toasts);
        Assert.Equal(defaults.Alerts.Adaptive, s.Alerts.Adaptive);
        Assert.Equal(defaults.Automation.LaneTips, s.Automation.LaneTips);
    }
}
