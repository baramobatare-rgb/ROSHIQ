using System.IO.Compression;
using System.Runtime.CompilerServices;
using Roshiq.Vision;
using Xunit;

namespace Roshiq.Tests;

/// <summary>
/// 0.19.2: распознавание на настоящих слотах драфта (кадр 2560×1440 из игры 11.11.2026, Fixtures/draft). В слотах поверх
/// портретов лежат плашки рангов и полосы цвета игрока — до 0.19.2 из-за них не узнавался ни один герой.
/// Имя файла — сторона, номер слота и герой; «cosmetic_» — портрет с косметикой, его узнавать не обязательно, «empty» — герой не выбран.
/// </summary>
public class DraftSlotTests
{
    private static string Root([CallerFilePath] string self = "") => Path.GetDirectoryName(self)!;

    private static readonly Lazy<PortraitMatcher> Matcher = new(() =>
    {
        var m = new PortraitMatcher();
        var zip = Path.Combine(Root(), "..", "..", "src", "Roshiq.App", "Assets", "Portraits.zip");
        using var archive = ZipFile.OpenRead(zip);
        foreach (var e in archive.Entries.Where(e => e.Name == "base.dpt"))
        {
            using var s = e.Open();
            m.AddArtTemplate(e.FullName.Split('/')[0], RgbImage.Load(s));
        }
        return m;
    });

    public static IEnumerable<object[]> Slots() =>
        Directory.GetFiles(Path.Combine(Root(), "Fixtures", "draft"), "*.dpt").OrderBy(f => f).Select(f => new object[] { Path.GetFileName(f) });

    [Theory]
    [MemberData(nameof(Slots))]
    public void RealSlot_IsRecognizedOrHonestlyUnknown(string file)
    {
        var hero = Path.GetFileNameWithoutExtension(file).Split('-', 2)[1];
        var slot = RgbImage.Load(Path.Combine(Root(), "Fixtures", "draft", file));
        var r = Matcher.Value.MatchSlot(slot);
        if (hero.StartsWith("cosmetic_") || hero == "empty")
            Assert.NotEqual(SlotStatus.Recognized, r.Status);                       // лучше «не знаю», чем чужой герой
        else
        {
            Assert.Equal(SlotStatus.Recognized, r.Status);
            Assert.Equal(hero, r.HeroKey);
        }
    }

    [Fact]
    public void MostOfTheRealDraftIsRecognized()
    {
        var files = Directory.GetFiles(Path.Combine(Root(), "Fixtures", "draft"), "*.dpt");
        int ok = files.Count(f =>
        {
            var hero = Path.GetFileNameWithoutExtension(f).Split('-', 2)[1];
            var r = Matcher.Value.MatchSlot(RgbImage.Load(f));
            return r.Status == SlotStatus.Recognized && r.HeroKey == hero;
        });
        Assert.True(ok >= 8, $"узнано {ok} из 8 видимых героев");
    }

    [Fact]
    public void SameHeroInTwoSlots_KeepsOnlyTheStrongerMatch()
    {
        var m = new PortraitMatcher();
        var a = RgbImage.Load(Path.Combine(Root(), "Fixtures", "draft", "radiant0-hoodwink.dpt"));
        m.AddTemplate("hoodwink", a);
        var layout = new Roshiq.Core.Settings.DraftLayoutSettings();
        var frame = new RgbImage(2560, 187);
        var slots = DraftLayout.Compute(layout, 2560, 187, 1440);
        foreach (var s in slots.Where(s => s.Side == Side.Radiant && s.Index < 2))
            for (int y = 0; y < a.Height && y < s.Height; y++)
                for (int x = 0; x < a.Width && x < s.Width; x++)
                {
                    var (r, g, b) = a.GetPixel(x, y);
                    frame.SetPixel(s.X + x, s.Y + y, r, g, b);
                }
        var result = new DraftRecognizer(m).Recognize(frame, layout, 1440);
        Assert.Single(result.Slots, s => s.Match.Status == SlotStatus.Recognized && s.Match.HeroKey == "hoodwink");
    }
}
