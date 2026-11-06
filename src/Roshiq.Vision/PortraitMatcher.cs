using Roshiq.Core.Diagnostics;

namespace Roshiq.Vision;

/// <summary>Вектор признаков портрета: нормированная уменьшенная картинка + цветовая гистограмма.</summary>
public sealed class PortraitFeature
{
    public const int W = 24, H = 14, Bins = 8;

    public float[] Pixels { get; }
    public float[] Histogram { get; }

    private PortraitFeature(float[] pixels, float[] hist) { Pixels = pixels; Histogram = hist; }

    public static PortraitFeature From(RgbImage img)
    {
        var small = img.ResizeArea(W, H);
        var px = new float[W * H * 3];
        double mean = 0;
        for (int i = 0; i < px.Length; i++) { px[i] = small.Data[i]; mean += px[i]; }
        mean /= px.Length;
        double norm = 0;
        for (int i = 0; i < px.Length; i++) { px[i] -= (float)mean; norm += px[i] * px[i]; }
        norm = Math.Sqrt(norm);
        if (norm > 1e-6) for (int i = 0; i < px.Length; i++) px[i] /= (float)norm;

        var hist = new float[Bins * 3];
        int n = small.Width * small.Height;
        for (int i = 0; i < small.Data.Length; i += 3)
        {
            hist[small.Data[i] * Bins / 256]++;
            hist[Bins + small.Data[i + 1] * Bins / 256]++;
            hist[2 * Bins + small.Data[i + 2] * Bins / 256]++;
        }
        for (int i = 0; i < hist.Length; i++) hist[i] /= n;
        return new PortraitFeature(px, hist);
    }

    /// <summary>Сходство 0..1: 70% — нормированная корреляция, 30% — пересечение гистограмм.</summary>
    public double Similarity(PortraitFeature other)
    {
        double dot = 0;
        for (int i = 0; i < Pixels.Length; i++) dot += Pixels[i] * other.Pixels[i];
        double ncc = (Math.Clamp(dot, -1, 1) + 1) / 2;
        double inter = 0;
        for (int i = 0; i < Histogram.Length; i++) inter += Math.Min(Histogram[i], other.Histogram[i]);
        inter /= 3; // три канала
        return 0.7 * ncc + 0.3 * inter;
    }
}

public enum SlotStatus { Empty, Recognized, Ambiguous, Unknown }

public sealed record MatchCandidate(string HeroKey, double Score);

public sealed record SlotMatch(SlotStatus Status, string? HeroKey, double Score, IReadOnlyList<MatchCandidate> Candidates);

/// <summary>
/// Сопоставление портрета с локальными шаблонами. Шаблоны создаются самим пользователем
/// (кнопка "Запомнить" при ручном исправлении) — никаких игровых файлов не читается.
/// </summary>
public sealed class PortraitMatcher
{
    private readonly Dictionary<string, List<PortraitFeature>> _templates = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _sync = new();

    public double Threshold { get; set; } = 0.82;
    public double AmbiguityMargin { get; set; } = 0.025;
    public double EmptyStdDev { get; set; } = 6.0;

    public int TemplateCount { get { lock (_sync) return _templates.Values.Sum(v => v.Count); } }
    public int HeroCount { get { lock (_sync) return _templates.Count; } }

    public void AddTemplate(string heroKey, RgbImage image)
    {
        var f = PortraitFeature.From(image);
        lock (_sync)
        {
            if (!_templates.TryGetValue(heroKey, out var list)) _templates[heroKey] = list = new();
            list.Add(f);
        }
    }

    public void Clear() { lock (_sync) _templates.Clear(); }

    public SlotMatch Match(RgbImage portrait)
    {
        if (portrait.LumaStdDev() < EmptyStdDev)
            return new SlotMatch(SlotStatus.Empty, null, 0, Array.Empty<MatchCandidate>());

        var f = PortraitFeature.From(portrait);
        List<MatchCandidate> ranked;
        lock (_sync)
        {
            ranked = _templates
                .Select(kv => new MatchCandidate(kv.Key, kv.Value.Max(t => t.Similarity(f))))
                .OrderByDescending(c => c.Score)
                .Take(5)
                .ToList();
        }

        if (ranked.Count == 0 || ranked[0].Score < Threshold)
            return new SlotMatch(SlotStatus.Unknown, null, ranked.Count > 0 ? ranked[0].Score : 0, ranked);

        if (ranked.Count > 1 && ranked[0].Score - ranked[1].Score < AmbiguityMargin)
            return new SlotMatch(SlotStatus.Ambiguous, null, ranked[0].Score, ranked);

        return new SlotMatch(SlotStatus.Recognized, ranked[0].HeroKey, ranked[0].Score, ranked);
    }
}

/// <summary>Хранилище шаблонов на диске: templates\{heroKey}\*.dpt</summary>
public sealed class TemplateStore
{
    private readonly string _root;
    public TemplateStore(string root) { _root = root; }

    public string Root => _root;

    public int LoadInto(PortraitMatcher matcher)
    {
        int n = 0;
        if (!Directory.Exists(_root)) return 0;
        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            var key = Path.GetFileName(dir);
            foreach (var file in Directory.EnumerateFiles(dir, "*.dpt"))
            {
                try { matcher.AddTemplate(key, RgbImage.Load(file)); n++; }
                catch (Exception ex) { Log.Warn($"Шаблон {file} пропущен: {ex.Message}"); }
            }
        }
        return n;
    }

    public string Save(string heroKey, RgbImage portrait)
    {
        var dir = Path.Combine(_root, Sanitize(heroKey));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"{DateTime.UtcNow:yyyyMMddHHmmssfff}.dpt");
        portrait.Save(path);
        return path;
    }

    private static string Sanitize(string s) =>
        new string(s.Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray());
}
