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

/// <summary>
/// Вычисляет прямоугольники 10 слотов верхней панели из настроек калибровки. Доли считаются от области 16:9 по центру
/// экрана игры: на 16:9 это весь кадр, на ultrawide (3440×1440) — центральные 2560 пикселей, на 16:10 — полоса по ширине.
/// </summary>
public static class DraftLayout
{
    /// <param name="screenHeight">Высота всего окна игры, если <paramref name="frameHeight"/> — только верхняя полоса.</param>
    public static IReadOnlyList<SlotRegion> Compute(DraftLayoutSettings s, int frameWidth, int frameHeight, int? screenHeight = null)
    {
        int fullH = Math.Max(1, screenHeight ?? frameHeight);
        double refW = Math.Min(frameWidth, fullH * 16.0 / 9.0);
        double refH = refW * 9.0 / 16.0;
        double offset = (frameWidth - refW) / 2;
        var list = new List<SlotRegion>(10);
        int w = Math.Max(4, (int)Math.Round(s.SlotWidth * refW));
        int h = Math.Max(4, (int)Math.Round(s.SlotHeight * refH));
        int y = (int)Math.Round(s.Top * refH);
        for (int i = 0; i < 5; i++)
        {
            list.Add(new SlotRegion(Side.Radiant, i, (int)Math.Round(offset + (s.RadiantStartX + i * s.SlotStep) * refW), y, w, h));
            list.Add(new SlotRegion(Side.Dire, i, (int)Math.Round(offset + (s.DireStartX + i * s.SlotStep) * refW), y, w, h));
        }
        return list.OrderBy(r => r.Side).ThenBy(r => r.Index).ToList();
    }
}

/// <summary>Распознавание видимых портретов в кадре, полученном разрешённым захватом экрана.</summary>
public sealed class DraftRecognizer
{
    private readonly PortraitMatcher _matcher;

    public DraftRecognizer(PortraitMatcher matcher) { _matcher = matcher; }

    /// <param name="screenHeight">Высота всего окна игры, если в <paramref name="frame"/> только верхняя полоса.</param>
    public DraftRecognitionResult Recognize(RgbImage frame, DraftLayoutSettings layout, int? screenHeight = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var results = new List<SlotResult>(10);
        foreach (var region in DraftLayout.Compute(layout, frame.Width, frame.Height, screenHeight))
        {
            if (region.X < 0 || region.X >= frame.Width || region.Y >= frame.Height) continue;
            var crop = frame.Crop(region.X, region.Y, Math.Min(region.Width, frame.Width - region.X), Math.Min(region.Height, frame.Height - region.Y));
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
