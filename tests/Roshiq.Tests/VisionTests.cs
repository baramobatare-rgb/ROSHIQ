using Roshiq.Core.Settings;
using Roshiq.Vision;
using Xunit;

namespace Roshiq.Tests;

/// <summary>
/// Распознавание на подготовленных изображениях. Портреты здесь синтетические (блочные узоры),
/// поэтому тест проверяет алгоритм, а не игровые картинки — реальные шаблоны пользователь создаёт сам.
/// </summary>
public class VisionTests
{
    private static RgbImage Pattern(int seed, int w = 62, int h = 35)
    {
        var rnd = new Random(seed);
        var colors = new (byte, byte, byte)[4, 8];
        for (int y = 0; y < 4; y++)
            for (int x = 0; x < 8; x++)
                colors[y, x] = ((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256));
        var img = new RgbImage(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                var (r, g, b) = colors[y * 4 / h, x * 8 / w];
                img.SetPixel(x, y, r, g, b);
            }
        return img;
    }

    private static RgbImage Noisy(RgbImage src, int amplitude, int seed)
    {
        var rnd = new Random(seed);
        var copy = new RgbImage(src.Width, src.Height, (byte[])src.Data.Clone());
        for (int i = 0; i < copy.Data.Length; i++)
            copy.Data[i] = (byte)Math.Clamp(copy.Data[i] + rnd.Next(-amplitude, amplitude + 1), 0, 255);
        return copy;
    }

    private static readonly string[] Keys = { "axe", "lion", "pudge", "lina", "sven", "tiny", "zuus", "riki", "bane", "luna" };

    private static PortraitMatcher Trained()
    {
        var m = new PortraitMatcher { Threshold = 0.82, AmbiguityMargin = 0.025 };
        for (int i = 0; i < Keys.Length; i++) m.AddTemplate(Keys[i], Pattern(i + 1));
        return m;
    }

    [Fact]
    public void ExactPortrait_IsRecognized()
    {
        var r = Trained().Match(Pattern(3));
        Assert.Equal(SlotStatus.Recognized, r.Status);
        Assert.Equal("pudge", r.HeroKey);
        Assert.True(r.Score > 0.99);
    }

    [Fact]
    public void NoisyPortrait_IsRecognized()
    {
        var r = Trained().Match(Noisy(Pattern(5), 12, 42));
        Assert.Equal(SlotStatus.Recognized, r.Status);
        Assert.Equal("sven", r.HeroKey);
    }

    [Fact]
    public void Portrait_At1440p_MatchesTemplateFrom1080p()
    {
        var r = Trained().Match(Pattern(7, 83, 47)); // тот же портрет, в 4/3 раза крупнее
        Assert.Equal(SlotStatus.Recognized, r.Status);
        Assert.Equal("zuus", r.HeroKey);
    }

    [Fact]
    public void UnknownPortrait_ReportsUncertainty_WithCandidates()
    {
        var r = Trained().Match(Pattern(5000));
        Assert.Equal(SlotStatus.Unknown, r.Status);
        Assert.Null(r.HeroKey);
        Assert.NotEmpty(r.Candidates);
    }

    [Fact]
    public void FlatSlot_IsEmpty()
    {
        var img = new RgbImage(62, 35);
        for (int i = 0; i < img.Data.Length; i++) img.Data[i] = 40;
        Assert.Equal(SlotStatus.Empty, Trained().Match(img).Status);
    }

    [Fact]
    public void IdenticalTemplatesForTwoHeroes_AreAmbiguous()
    {
        var m = Trained();
        m.AddTemplate("sniper", Pattern(1)); // совпадает с шаблоном axe
        var r = m.Match(Pattern(1));
        Assert.Equal(SlotStatus.Ambiguous, r.Status);
        Assert.Null(r.HeroKey);
        Assert.Equal(2, r.Candidates.Take(2).Count(c => c.Score > 0.99));
    }

    [Fact]
    public void NoTemplates_IsUnknownNotCrash()
    {
        Assert.Equal(SlotStatus.Unknown, new PortraitMatcher().Match(Pattern(1)).Status);
    }

    [Fact]
    public void Layout_ProducesTenSlots_InsideFrame()
    {
        var s = new DraftLayoutSettings();
        foreach (var (w, h) in new[] { (1920, 1080), (2560, 1440) })
        {
            var slots = DraftLayout.Compute(s, w, h);
            Assert.Equal(10, slots.Count);
            Assert.Equal(5, slots.Count(x => x.Side == Side.Radiant));
            Assert.All(slots, x => Assert.True(x.X >= 0 && x.X + x.Width <= w && x.Y + x.Height <= h));
        }
    }

    [Fact]
    public void Recognizer_FindsPortraitsInFrame_AndSidesAreCorrect()
    {
        var layout = new DraftLayoutSettings();
        var frame = new RgbImage(1920, 1080); // чёрный кадр — все ячейки пусты
        var slots = DraftLayout.Compute(layout, 1920, 1080);
        var radiant0 = slots.First(s => s.Side == Side.Radiant && s.Index == 0);
        var dire2 = slots.First(s => s.Side == Side.Dire && s.Index == 2);
        Blit(frame, Pattern(1, radiant0.Width, radiant0.Height), radiant0.X, radiant0.Y);
        Blit(frame, Pattern(2, dire2.Width, dire2.Height), dire2.X, dire2.Y);

        var result = new DraftRecognizer(Trained()).Recognize(frame, layout);
        Assert.True(result.DraftUiVisible);
        var r0 = result.Slots.Single(s => s.Region.Side == Side.Radiant && s.Region.Index == 0);
        var d2 = result.Slots.Single(s => s.Region.Side == Side.Dire && s.Region.Index == 2);
        Assert.Equal("axe", r0.Match.HeroKey);
        Assert.Equal("lion", d2.Match.HeroKey);
        Assert.Equal(8, result.Slots.Count(s => s.Match.Status == SlotStatus.Empty));
    }

    [Fact]
    public void Templates_SaveAndLoadFromDisk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dp_tpl_" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new TemplateStore(dir);
            store.Save("axe", Pattern(1));
            store.Save("lion", Pattern(2));
            var m = new PortraitMatcher();
            Assert.Equal(2, store.LoadInto(m));
            Assert.Equal("lion", m.Match(Pattern(2)).HeroKey);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Throttle_LimitsFrequency_AndPreventsOverlap()
    {
        var t = new RecognitionThrottle();
        var now = DateTime.UtcNow;
        Assert.True(t.TryBegin(now, TimeSpan.FromSeconds(1)));
        Assert.False(t.TryBegin(now.AddSeconds(5), TimeSpan.FromSeconds(1))); // предыдущий ещё идёт
        t.End();
        Assert.False(t.TryBegin(now.AddMilliseconds(500), TimeSpan.FromSeconds(1)));
        Assert.True(t.TryBegin(now.AddSeconds(2), TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void FromBgra_ConvertsChannelOrder()
    {
        var bgra = new byte[] { 10, 20, 30, 255 };
        var img = RgbImage.FromBgra(bgra, 1, 1, 4);
        Assert.Equal(((byte)30, (byte)20, (byte)10), img.GetPixel(0, 0));
    }

    private static void Blit(RgbImage dst, RgbImage src, int x0, int y0)
    {
        for (int y = 0; y < src.Height; y++)
            for (int x = 0; x < src.Width; x++)
            {
                var (r, g, b) = src.GetPixel(x, y);
                dst.SetPixel(x0 + x, y0 + y, r, g, b);
            }
    }
}
