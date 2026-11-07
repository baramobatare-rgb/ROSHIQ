using System.Globalization;

namespace Roshiq.Core.Layout;

/// <summary>Положение панели для каждого разрешения окна игры и прилипание к краям при перетаскивании.</summary>
public static class LayoutMemory
{
    /// <summary>Ключ разрешения: "2560x1440". null — размер неизвестен.</summary>
    public static string? Key(int width, int height) =>
        width < 320 || height < 240 ? null : string.Create(CultureInfo.InvariantCulture, $"{width}x{height}");

    public static bool IsKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return false;
        var parts = key.Split('x');
        return parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h) && w >= 320 && h >= 240;
    }

    /// <summary>"2560x1440" → "2560 × 1440".</summary>
    public static string Pretty(string key) => key.Replace("x", " × ");

    /// <summary>
    /// Прилипание: если край панели ближе threshold (доля экрана) к краю окна (с отступом margin) или к краю
    /// запретной зоны (миникарта, верхняя полоса, нижний HUD), край ставится вплотную.
    /// </summary>
    public static RelRect Snap(RelRect r, double threshold = 0.015, double margin = 0.01)
    {
        var xs = new List<double> { margin, 1 - margin };
        var ys = new List<double> { margin, 1 - margin };
        foreach (var z in SafeZone.Forbidden)
        {
            xs.Add(z.X); xs.Add(z.Right);
            ys.Add(z.Y); ys.Add(z.Bottom);
        }
        double x = SnapEdge(r.X, r.W, xs, threshold);
        double y = SnapEdge(r.Y, r.H, ys, threshold);
        return r with { X = x, Y = y };
    }

    private static double SnapEdge(double start, double size, IEnumerable<double> lines, double threshold)
    {
        double best = start, bestDist = threshold;
        foreach (var line in lines)
        {
            double d1 = Math.Abs(start - line);          // левый/верхний край к линии
            if (d1 < bestDist) { bestDist = d1; best = line; }
            double d2 = Math.Abs(start + size - line);   // правый/нижний край к линии
            if (d2 < bestDist) { bestDist = d2; best = line - size; }
        }
        return best;
    }
}
