using System.Text.Json;
using Roshiq.Core.Layout;
using Roshiq.Core.Media;
using Roshiq.Core.Models;
using Roshiq.Core.Settings;
using Roshiq.Core.Text;
using Roshiq.Recommendations;
using Xunit;

namespace Roshiq.Tests;

/// <summary>0.14: поиск Ctrl+K, раскладки оверлея по разрешениям, звуки, итог матча.</summary>
public class ComfortTests
{
    // ------------------------------------------------------------ поиск

    [Fact]
    public void Search_PrefixBeatsContains()
    {
        Assert.True(QuickSearch.Score("пуд", "Пудж") > QuickSearch.Score("пуд", "Какой-то пудинг"));
        Assert.Equal(1000, QuickSearch.Score("Лотус", "лотус"));
        Assert.Equal(0, QuickSearch.Score("xyz", "Pudge"));
    }

    [Fact]
    public void Search_AbbreviationAndSubsequence()
    {
        Assert.True(QuickSearch.Score("bkb", "Black King Bar") > 0);
        Assert.True(QuickSearch.Score("blnk", "Blink Dagger") > 0);
        Assert.True(QuickSearch.Score("ёлка", "Елка") > 0);
    }

    [Fact]
    public void Search_WrongKeyboardLayout()
    {
        Assert.Equal("пудж", QuickSearch.SwapLayout("gel;"));
        Assert.Equal("ghbdtn", QuickSearch.SwapLayout("привет"));
        Assert.Null(QuickSearch.SwapLayout("123"));
        Assert.True(QuickSearch.Best("gel;", new[] { "Пудж", "Pudge" }) > 0);
        Assert.True(QuickSearch.Best("ыефыр", new[] { "stash" }) > 0);                      // «stash» в русской раскладке
    }

    // ------------------------------------------------------------ раскладки оверлея

    [Fact]
    public void Layout_KeyAndValidation()
    {
        Assert.Equal("2560x1440", LayoutMemory.Key(2560, 1440));
        Assert.Null(LayoutMemory.Key(0, 0));
        Assert.True(LayoutMemory.IsKey("1920x1080"));
        Assert.False(LayoutMemory.IsKey("abc"));
        Assert.False(LayoutMemory.IsKey("10x10"));
        Assert.Equal("1920 × 1080", LayoutMemory.Pretty("1920x1080"));
    }

    [Fact]
    public void Layout_SnapsToRightEdgeAndHudLines()
    {
        var r = LayoutMemory.Snap(new RelRect(0.785, 0.40, 0.20, 0.30));      // правый край 0.985 → 0.99
        Assert.Equal(0.99, r.X + r.W, 3);
        var top = LayoutMemory.Snap(new RelRect(0.80, 0.083, 0.15, 0.2));      // верх около нижней границы верхней полосы (0.075)
        Assert.Equal(SafeZone.TopBar.Bottom, top.Y, 3);
        var free = LayoutMemory.Snap(new RelRect(0.5, 0.4, 0.1, 0.1));         // далеко от линий — не двигается
        Assert.Equal(0.5, free.X, 6);
        Assert.Equal(0.4, free.Y, 6);
    }

    [Fact]
    public void Layout_SettingsRoundTripAndCleanup()
    {
        var s = new AppSettings();
        s.Overlay.Layouts["2560x1440"] = new RelRect(0.7, 0.2, 0.2, 0.3);
        s.Overlay.Layouts["мусор"] = new RelRect(0.1, 0.1, 0.1, 0.1);
        var json = JsonSerializer.Serialize(s, SettingsService.JsonOptions);
        var back = JsonSerializer.Deserialize<AppSettings>(json, SettingsService.JsonOptions)!.Normalize();
        Assert.True(back.Overlay.Layouts.ContainsKey("2560x1440"));
        Assert.False(back.Overlay.Layouts.ContainsKey("мусор"));
        Assert.Equal(0.7, back.Overlay.Layouts["2560x1440"].X, 3);
    }

    // ------------------------------------------------------------ звуки

    [Theory]
    [InlineData("soft")]
    [InlineData("chime")]
    [InlineData("ping")]
    [InlineData("alert")]
    public void Tones_AreValidWav(string name)
    {
        var wav = Tones.Wav(name)!;
        Assert.Equal("RIFF", System.Text.Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Equal("WAVE", System.Text.Encoding.ASCII.GetString(wav, 8, 4));
        Assert.Equal(wav.Length - 8, BitConverter.ToInt32(wav, 4));
        Assert.Equal(Tones.SampleRate, BitConverter.ToInt32(wav, 24));
        Assert.InRange(wav.Length, 2000, 30000);                               // короткий звук
    }

    [Fact]
    public void Tones_NoneIsSilent()
    {
        Assert.Null(Tones.Wav("none"));
        Assert.Null(Tones.Wav("что-то"));
    }

    [Fact]
    public void Sounds_NormalizeFixesBadValues()
    {
        var s = new SoundSettings { Volume = 7, Reminder = "boom", Warning = "ping", MinGapSec = -1 };
        s.Normalize();
        Assert.Equal(1, s.Volume);
        Assert.Equal("chime", s.Reminder);
        Assert.Equal("ping", s.Warning);
        Assert.Equal(0, s.MinGapSec);
        Assert.False(new SoundSettings().Enabled);                              // по умолчанию выключены
    }

    // ------------------------------------------------------------ итог матча

    private static MatchRecord Match(string id, int deaths, int lh10, int gpm, MatchResult result = MatchResult.Win, int kills = 5, int assists = 5,
        int durationMin = 35, int heroId = 14)
    {
        var m = new MatchRecord
        {
            Id = id, MatchId = id, Source = "GSI", HeroId = heroId, Position = Position.Mid, Result = result,
            DurationSec = durationMin * 60, Kills = kills, Deaths = deaths, Assists = assists, Gpm = gpm, LastHits = lh10 * 4
        };
        m.Samples.Add(new MatchSample { Clock = 600, LastHits = lh10, Gpm = gpm });
        return m;
    }

    private static List<MatchRecord> History() => Enumerable.Range(0, 6).Select(i => Match("h" + i, deaths: 5, lh10: 50, gpm: 550)).ToList();

    [Fact]
    public void PostMatch_FindsMainMistake()
    {
        var m = Match("now", deaths: 12, lh10: 30, gpm: 420, result: MatchResult.Loss, kills: 2, assists: 4);
        for (int i = 0; i < 6; i++) m.DeathEvents.Add(new DeathEvent { Clock = 700 + i * 60, HadTeleport = true });
        var sum = PostMatch.Build(m, History());
        Assert.NotNull(sum.MainMistake);
        Assert.Equal("deaths", sum.MainMistake!.Kind);
        Assert.Contains("12", sum.MainMistake.Ru);
        Assert.Contains("10–25", sum.MainMistake.Ru);                          // большинство смертей в середине игры
        Assert.Equal(6, sum.Compared);
    }

    [Fact]
    public void PostMatch_FindsGoodThings()
    {
        var m = Match("now", deaths: 1, lh10: 72, gpm: 700, kills: 12, assists: 10);
        var sum = PostMatch.Build(m, History());
        Assert.True(sum.Good.Count >= 1 && sum.Good.Count <= 2);
        Assert.Contains(sum.Good, g => g.Kind is "lh10" or "kda" or "deaths");
        Assert.Null(sum.MainMistake);
        Assert.True(sum.HasContent);
    }

    [Fact]
    public void PostMatch_SkipsShortAndImported()
    {
        Assert.False(PostMatch.Worth(Match("x", 0, 0, 0, durationMin: 4)));
        var imported = Match("y", 0, 0, 0);
        imported.Source = "OpenDota";
        Assert.False(PostMatch.Worth(imported));
        Assert.True(PostMatch.Worth(Match("z", 0, 0, 0)));
    }

    [Fact]
    public void PostMatch_WorksWithoutHistory()
    {
        var m = Match("now", deaths: 11, lh10: 20, gpm: 300, result: MatchResult.Loss);
        var sum = PostMatch.Build(m, Array.Empty<MatchRecord>());
        Assert.NotNull(sum.MainMistake);
        Assert.Equal(0, sum.Compared);
    }

    [Fact]
    public void PostMatch_IgnoredAlertIsReported()
    {
        var m = Match("now", deaths: 5, lh10: 50, gpm: 550);
        m.AlertsIgnored[MatchAlerts.NoTeleport] = 4;
        var sum = PostMatch.Build(m, History());
        Assert.Equal("ignored", sum.MainMistake?.Kind);
        Assert.Contains("нет телепорта", sum.MainMistake!.Ru);
    }
}
