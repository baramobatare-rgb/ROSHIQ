using Roshiq.Core.Settings;

namespace Roshiq.Vision;

public enum Side { Radiant, Dire }

public sealed record SlotRegion(Side Side, int Index, int X, int Y, int Width, int Height);

public sealed record SlotResult(SlotRegion Region, SlotMatch Match, RgbImage Crop);

public sealed record DraftRecognitionResult(IReadOnlyList<SlotResult> Slots, bool DraftUiVisible, TimeSpan Elapsed)
{
    public long SessionId { get; init; }
    public bool IsManual { get; init; }
    public IEnumerable<SlotResult> For(Side side) => Slots.Where(s => s.Region.Side == side);
    public int UncertainCount => Slots.Count(s => s.Match.Status is SlotStatus.Unknown or SlotStatus.Ambiguous);
}

/// <summary>Вычисляет прямоугольники 10 портретов верхней панели из настроек калибровки.</summary>
public static class DraftLayout
{
    public static IReadOnlyList<SlotRegion> Compute(DraftLayoutSettings s, int frameWidth, int frameHeight)
    {
        var list = new List<SlotRegion>(10);
        int w = Math.Max(4, (int)Math.Round(s.SlotWidth * frameWidth));
        int h = Math.Max(4, (int)Math.Round(s.SlotHeight * frameHeight));
        int y = (int)Math.Round(s.Top * frameHeight);
        for (int i = 0; i < 5; i++)
        {
            list.Add(new SlotRegion(Side.Radiant, i, (int)Math.Round((s.RadiantStartX + i * s.SlotStep) * frameWidth), y, w, h));
            list.Add(new SlotRegion(Side.Dire, i, (int)Math.Round((s.DireStartX + i * s.SlotStep) * frameWidth), y, w, h));
        }
        return list.OrderBy(r => r.Side).ThenBy(r => r.Index).ToList();
    }
}

/// <summary>Распознавание видимых портретов в кадре, полученном разрешённым захватом экрана.</summary>
public sealed class DraftRecognizer
{
    private readonly PortraitMatcher _matcher;

    public DraftRecognizer(PortraitMatcher matcher) { _matcher = matcher; }

    public DraftRecognitionResult Recognize(RgbImage frame, DraftLayoutSettings layout)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = new List<SlotResult>(10);
        foreach (var region in DraftLayout.Compute(layout, frame.Width, frame.Height))
        {
            if (region.X >= frame.Width || region.Y >= frame.Height) continue;
            var crop = frame.Crop(region.X, region.Y, region.Width, region.Height);
            results.Add(new SlotResult(region, _matcher.Match(crop), crop));
        }
        // Эвристика "интерфейс драфта виден": хотя бы одна ячейка не пустая и выглядит как портрет.
        bool visible = results.Count(r => r.Match.Status != SlotStatus.Empty) >= 1;
        return new DraftRecognitionResult(results, visible, sw.Elapsed);
    }
}

/// <summary>Ограничитель частоты распознавания: не чаще интервала и без наложения запусков.</summary>
public sealed class RecognitionThrottle
{
    private DateTime _last = DateTime.MinValue;
    private int _running;

    public bool TryBegin(DateTime nowUtc, TimeSpan interval)
    {
        if (nowUtc - _last < interval) return false;
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0) return false;
        _last = nowUtc;
        return true;
    }

    public void End() => Interlocked.Exchange(ref _running, 0);
}
