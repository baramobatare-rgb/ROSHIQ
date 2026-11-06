using Roshiq.Core.Layout;
using Roshiq.Core.Models;
using Roshiq.Core.Settings;
using Xunit;

namespace Roshiq.Tests;

public class SettingsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dp_settings_" + Guid.NewGuid().ToString("N"));

    public SettingsTests()
    {
        Directory.CreateDirectory(_dir);
        Roshiq.Core.Diagnostics.Log.FileEnabled = false;
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public void SaveAndLoad_RoundTrip()
    {
        var path = Path.Combine(_dir, "settings.json");
        var svc = new SettingsService(path);
        svc.Update(s =>
        {
            s.Language = Language.English;
            s.Position = Position.SoftSupport;
            s.UsageContext = UsageContext.Training;
            s.Overlay.Mode = OverlayMode.Expanded;
            s.Overlay.Opacity = 0.5;
            s.Notifications.ObserverIntervalSec = 300;
        });
        var again = new SettingsService(path).Current;
        Assert.Equal(Language.English, again.Language);
        Assert.Equal(Position.SoftSupport, again.Position);
        Assert.Equal(UsageContext.Training, again.UsageContext);
        Assert.Equal(OverlayMode.Expanded, again.Overlay.Mode);
        Assert.Equal(0.5, again.Overlay.Opacity, 3);
        Assert.Equal(300, again.Notifications.ObserverIntervalSec);
        Assert.Equal(svc.Current.Gsi.Token, again.Gsi.Token);
    }

    [Fact]
    public void Normalize_ClampsInvalidValues()
    {
        var s = new AppSettings();
        s.Overlay.Opacity = 7;
        s.Notifications.ObserverIntervalSec = 1;
        s.Gsi.Port = 5;
        s.Vision.IntervalMs = 10;
        s.Overlay.Panel = new RelRect(0.0, 0.8, 0.2, 0.2); // поверх мини-карты
        s.Normalize();
        Assert.Equal(1.0, s.Overlay.Opacity);
        Assert.Equal(60, s.Notifications.ObserverIntervalSec);
        Assert.Equal(53742, s.Gsi.Port);
        Assert.Equal(1000, s.Vision.IntervalMs);
        Assert.False(s.Overlay.Panel.Intersects(SafeZone.Minimap));
    }

    [Fact]
    public void BrokenFile_FallsBackToDefaults()
    {
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, "{ this is not json");
        var svc = new SettingsService(path);
        Assert.Equal(Position.Carry, svc.Current.Position);
        Assert.True(File.Exists(path + ".broken"));
    }

    [Fact]
    public void ExportImport_And_InvalidImportKeepsCurrent()
    {
        var svc = new SettingsService(Path.Combine(_dir, "a.json"));
        svc.Update(s => s.Position = Position.Mid);
        var export = Path.Combine(_dir, "export.json");
        svc.Export(export);

        var other = new SettingsService(Path.Combine(_dir, "b.json"));
        Assert.True(other.Import(export, out _));
        Assert.Equal(Position.Mid, other.Current.Position);

        var bad = Path.Combine(_dir, "bad.json");
        File.WriteAllText(bad, "[1,2");
        Assert.False(other.Import(bad, out var err));
        Assert.NotNull(err);
        Assert.Equal(Position.Mid, other.Current.Position);
    }

    [Fact]
    public void EnumsAreStoredAsText()
    {
        var path = Path.Combine(_dir, "settings.json");
        new SettingsService(path).Save();
        var json = File.ReadAllText(path);
        Assert.Contains("\"Russian\"", json);
        Assert.Contains("\"Minimal\"", json);
    }
}
